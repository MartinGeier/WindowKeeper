using System.Text.RegularExpressions;

namespace WindowKeeper;

/// <summary>A top-level application window that is currently open.</summary>
internal sealed class LiveWindow
{
    public IntPtr Handle { get; init; }
    public uint ProcessId { get; init; }
    public string ProcessPath { get; init; } = "";
    public string ProcessName { get; init; } = "";
    /// <summary>Window class, normalized so it is stable across app restarts.</summary>
    public string ClassName { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsToolWindow { get; init; }

    public override string ToString() => $"[{ProcessName}] \"{Title}\" ({ClassName})";
}

internal static partial class WindowEnumerator
{
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd", "Shell_InputSwitchTopLevelWindow",
        "SysShadow", "tooltips_class32", "#32768",
    };

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Handles of all windows that look like normal application windows (cheap check, no process lookup).</summary>
    public static List<IntPtr> EnumCandidates(VirtualDesktops vd)
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (IsCandidate(h, vd)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static List<LiveWindow> GetWindows(VirtualDesktops vd) =>
        EnumCandidates(vd).Select(Describe).OfType<LiveWindow>().ToList();

    private static bool IsCandidate(IntPtr h, VirtualDesktops vd)
    {
        if (!Native.IsWindowVisible(h)) return false;
        long style = Native.GetWindowLong(h, Native.GWL_STYLE);
        long ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        if ((style & Native.WS_CHILD) != 0) return false;

        if ((ex & Native.WS_EX_APPWINDOW) == 0)
        {
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & Native.WS_EX_NOACTIVATE) != 0) return false;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
        }
        if (Native.GetWindowTextLength(h) == 0) return false;

        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == OwnPid) return false;
        if (ExcludedClasses.Contains(Native.GetClassName(h))) return false;

        int cloaked = Native.GetCloakedState(h);
        if (cloaked != 0)
        {
            // Windows on another virtual desktop are cloaked by the shell; those we keep.
            // Anything else that is cloaked (suspended UWP frames etc.) is invisible to the user.
            if ((cloaked & Native.DWM_CLOAKED_SHELL) == 0) return false;
            if (vd.IsOnCurrentDesktop(h)) return false;
            if (vd.GetDesktopId(h) == Guid.Empty) return false;
        }

        if (!Native.GetWindowRect(h, out var r) || r.IsEmpty) return false;
        return true;
    }

    public static LiveWindow? Describe(IntPtr h)
    {
        if (!Native.IsWindow(h)) return null;
        Native.GetWindowThreadProcessId(h, out uint pid);
        string path = Native.GetProcessPath(pid);
        string name = path.Length > 0 ? Path.GetFileName(path) : TryGetProcessName(pid);
        if (path.Length == 0) path = name;
        long ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        return new LiveWindow
        {
            Handle = h,
            ProcessId = pid,
            ProcessPath = path,
            ProcessName = name,
            ClassName = NormalizeClassName(Native.GetClassName(h)),
            Title = Native.GetWindowText(h),
            IsToolWindow = (ex & Native.WS_EX_TOOLWINDOW) != 0,
        };
    }

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return "pid" + pid;
        }
    }

    /// <summary>
    /// Some frameworks put per-run values into class names (WPF: HwndWrapper[App;;guid],
    /// WinForms: WindowsForms10.Window.8.app.0.2bf8098_r6_ad1). Strip those so a window can be recognized
    /// after the app (or the PC) restarts.
    /// </summary>
    public static string NormalizeClassName(string cls)
    {
        if (cls.StartsWith("HwndWrapper[", StringComparison.Ordinal)) return "HwndWrapper";
        return WinFormsSuffix().Replace(cls, ".app");
    }

    [GeneratedRegex(@"\.app\.[0-9a-fA-F]+\..*$")]
    private static partial Regex WinFormsSuffix();
}
