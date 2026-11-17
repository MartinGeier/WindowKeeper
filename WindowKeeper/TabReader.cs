using System.Diagnostics;
using System.Windows.Automation;

namespace WindowKeeper;

/// <summary>
/// Reads the tab titles of browser windows through UI Automation.
/// A browser window's title only shows the active tab, so it changes all the time; the set of open tabs
/// is what really identifies "the same browser window" - also after the browser restored its session
/// following a reboot.
/// </summary>
internal static class TabReader
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge.exe", "chrome.exe", "brave.exe", "vivaldi.exe", "opera.exe", "chromium.exe", "thorium.exe",
        "firefox.exe", "librewolf.exe", "waterfox.exe", "floorp.exe", "zen.exe",
    };

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    public static bool IsBrowser(LiveWindow w) => Browsers.Contains(w.ProcessName);

    /// <summary>Tab titles of the window, in tab-strip order. Empty if the window has no readable tabs.</summary>
    public static List<string> ReadTabs(IntPtr hwnd)
    {
        var tabs = new List<string>();
        if (Native.IsHungAppWindow(hwnd)) return tabs;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var walker = TreeWalker.ControlViewWalker;
            var sw = Stopwatch.StartNew();
            // Depth-first, but never into web page content (Document): the tab strip is part of the
            // browser's own UI, and page trees can be huge.
            var stack = new Stack<(AutomationElement element, int depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0 && sw.Elapsed < Budget)
            {
                var (element, depth) = stack.Pop();
                var children = new List<AutomationElement>();
                for (var child = walker.GetFirstChild(element); child != null; child = walker.GetNextSibling(child))
                    children.Add(child);
                foreach (var child in children)
                {
                    var type = child.Current.ControlType;
                    if (type == ControlType.TabItem)
                    {
                        string name = Clean(child.Current.Name);
                        if (name.Length > 0) tabs.Add(name);
                    }
                    else if (type != ControlType.Document && depth < 20)
                    {
                        stack.Push((child, depth + 1));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Reading tabs of window 0x{hwnd:X} failed: {ex.Message}");
        }
        return tabs;
    }

    /// <summary>How alike two tab lists are: 0 (nothing in common) .. 1 (same tabs).</summary>
    public static double Similarity(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var setA = new HashSet<string>(a.Select(t => Clean(t).ToLowerInvariant()));
        var setB = new HashSet<string>(b.Select(t => Clean(t).ToLowerInvariant()));
        int common = setA.Count(x => setB.Any(y => SameTab(x, y)));
        int union = setA.Count + setB.Count - common;
        return union <= 0 ? 0 : Math.Min(1.0, (double)common / union);
    }

    /// <summary>
    /// Equal, or one contains the other: right after a session restore, tabs that are not loaded yet
    /// may show their address or a longer/shorter variant of the title.
    /// </summary>
    private static bool SameTab(string x, string y) =>
        x == y || (Math.Min(x.Length, y.Length) >= 4 && (x.Contains(y) || y.Contains(x)));

    /// <summary>Chromium appends states such as " - Sleeping" or " - Memory usage - 120 MB" to tab names.</summary>
    public static string Clean(string tab)
    {
        foreach (var marker in new[] { " - Sleeping", " - Memory usage", " - Audio playing", " - Muted", " - Network error", " - Pinned" })
        {
            int i = tab.IndexOf(marker, StringComparison.Ordinal);
            if (i > 0) tab = tab[..i];
        }
        return tab.Trim();
    }
}
