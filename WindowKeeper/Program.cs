using System.Text;

namespace WindowKeeper;

internal static class Program
{
    /// <summary>
    /// WindowKeeper.exe                 run in the notification area
    /// Command line (output goes to stdout, useful for scripts and testing):
    ///   --diag                         list monitors, layout key, virtual desktops and windows
    ///   --save                         save the layout for the current monitors and exit
    ///   --restore                      restore the layout for the current monitors and exit
    ///   --only &lt;text&gt;                  only handle windows whose process or title contains text (for testing)
    ///   --data &lt;folder&gt;                use another data folder (default %APPDATA%\WindowKeeper)
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        string? Arg(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        string folder = Arg("--data") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowKeeper");
        var data = new DataStore(folder);
        Log.Init(folder);

        string? only = Arg("--only");
        Func<LiveWindow, bool>? scope = only == null
            ? null
            : w => w.ProcessName.Contains(only, StringComparison.OrdinalIgnoreCase) ||
                   w.Title.Contains(only, StringComparison.OrdinalIgnoreCase);

        if (Has("--diag") || Has("--save") || Has("--restore"))
            return RunCommandLine(data, Has, scope);

        // One instance per data folder.
        string mutexName = @"Local\WindowKeeper-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToLowerInvariant())))[..16];
        using var mutex = new Mutex(true, mutexName, out bool first);
        if (!first)
        {
            MessageBox.Show("WindowKeeper is already running (see the notification area).", "WindowKeeper");
            return 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp(data, new VirtualDesktops(), scope));
        return 0;
    }

    private static int RunCommandLine(DataStore data, Func<string, bool> has, Func<LiveWindow, bool>? scope)
    {
        var vd = new VirtualDesktops();
        var engine = new LayoutEngine(data, vd, scope);

        if (has("--diag")) Console.Out.Write(Diagnostics(vd));
        if (has("--save"))
        {
            var r = engine.SaveCurrent();
            Console.Out.WriteLine($"Saved {r.Windows} window(s) for {r.LayoutDescription}");
        }
        if (has("--restore"))
        {
            var r = engine.RestoreAll("command line");
            Console.Out.WriteLine($"Restore: {r.Matched} recognized, {r.Moved} moved ({r.LayoutDescription}, saved layout: {r.HasSavedLayout})");
        }
        Console.Out.Flush();
        return 0;
    }

    private static string Diagnostics(VirtualDesktops vd)
    {
        var sb = new StringBuilder();
        var layout = MonitorLayout.Current();
        sb.AppendLine($"Layout key: {layout.Key}");
        sb.AppendLine($"Layout:     {layout.Description}");
        foreach (var m in layout.Monitors)
            sb.AppendLine($"  {(m.Primary ? "*" : " ")} {m.DeviceName}  {m.Name}  bounds {m.Bounds}  work {m.WorkArea}  dpi {m.Dpi}\n      id {m.Id}");

        sb.AppendLine();
        sb.AppendLine(vd.Status);
        var desktops = VirtualDesktops.ListDesktopIds();
        var current = vd.CurrentDesktopId();
        for (int i = 0; i < desktops.Count; i++)
            sb.AppendLine($"  Desktop {i + 1}: {desktops[i]}{(desktops[i] == current ? "  (current)" : "")}");

        sb.AppendLine();
        var windows = WindowEnumerator.GetWindows(vd);
        sb.AppendLine($"{windows.Count} application windows:");
        foreach (var w in windows)
        {
            Native.GetWindowRect(w.Handle, out var r);
            string state = Native.IsIconic(w.Handle) ? "min" : Native.IsZoomed(w.Handle) ? "max" : "normal";
            int idx = desktops.IndexOf(vd.GetDesktopId(w.Handle));
            sb.AppendLine($"  {w}  {state} {r}  desktop {(idx >= 0 ? (idx + 1).ToString() : "?")}" +
                          (vd.IsOnCurrentDesktop(w.Handle) ? " (visible now)" : ""));
            if (TabReader.IsBrowser(w))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var tabs = TabReader.ReadTabs(w.Handle);
                sb.AppendLine($"      {tabs.Count} tabs read in {sw.ElapsedMilliseconds} ms: {string.Join(" | ", tabs)}");
            }
        }
        return sb.ToString();
    }
}
