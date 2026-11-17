namespace WindowKeeper;

internal sealed record SaveResult(int Windows, string LayoutDescription);
internal sealed record RestoreResult(int Matched, int Moved, string LayoutDescription, bool HasSavedLayout);

/// <summary>
/// Saves window positions on request and restores them:
/// - on request,
/// - when the monitor configuration changes (after it has settled),
/// - when an application opens a window that has a saved position (e.g. after a reboot).
/// Nothing is ever saved automatically.
/// </summary>
internal sealed class LayoutEngine
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NewWindowDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan BrowserTabWait = TimeSpan.FromSeconds(10);

    private readonly DataStore _data;
    private readonly VirtualDesktops _vd;

    /// <summary>Which saved record each open window was matched to (so two windows never share one).</summary>
    private readonly Dictionary<IntPtr, Guid> _assigned = new();
    /// <summary>Windows we have already seen; only windows not in here count as "newly opened".</summary>
    private readonly HashSet<IntPtr> _known = new();
    private readonly Dictionary<IntPtr, DateTime> _pending = new();

    private bool _settling = true;   // true at startup: restore everything once the monitors are stable
    private string? _settlingKey;
    private DateTime _stableSince;

    /// <summary>Only windows passing this filter are saved or restored (used for testing).</summary>
    private readonly Func<LiveWindow, bool> _scope;

    public event Action<string>? Notify;

    public LayoutEngine(DataStore data, VirtualDesktops vd, Func<LiveWindow, bool>? scope = null)
    {
        _data = data;
        _vd = vd;
        _scope = scope ?? (_ => true);
    }

    public Settings Settings => _data.Settings;

    // ------------------------------------------------------------------ save

    public SaveResult SaveCurrent()
    {
        var layout = MonitorLayout.Current();
        var desktops = VirtualDesktops.ListDesktopIds();
        var windows = WindowEnumerator.GetWindows(_vd).Where(_scope).ToList();

        var snapshot = new LayoutSnapshot
        {
            Key = layout.Key,
            Description = layout.Description,
            SavedAt = DateTime.Now,
            Monitors = layout.Monitors.Select(m => new MonitorRecord
            {
                Id = m.Id, Name = m.Name, Width = m.Bounds.Width, Height = m.Bounds.Height,
                Dpi = m.Dpi, X = m.Bounds.Left, Y = m.Bounds.Top, Primary = m.Primary,
            }).ToList(),
        };

        foreach (var w in windows)
        {
            var record = Capture(w, layout, desktops);
            if (record == null) continue;
            snapshot.Windows.Add(record);
            _assigned[w.Handle] = record.Id;
            _known.Add(w.Handle);
        }

        _data.Layouts.Layouts[layout.Key] = snapshot;
        _data.SaveLayouts();
        Log.Write($"Saved {snapshot.Windows.Count} windows for layout {layout.Key} [{layout.Description}]");
        return new SaveResult(snapshot.Windows.Count, layout.Description);
    }

    private WindowRecord? Capture(LiveWindow w, MonitorLayout layout, List<Guid> desktops)
    {
        var h = w.Handle;
        var wp = Native.WINDOWPLACEMENT.Create();
        if (!Native.GetWindowPlacement(h, ref wp)) return null;
        bool min = Native.IsIconic(h);
        bool max = Native.IsZoomed(h);

        Native.RECT screen;
        if (!min && !max)
        {
            // The actual rectangle (also correct for snapped windows, whose "normal" rect differs).
            if (!Native.GetWindowRect(h, out screen)) return null;
        }
        else
        {
            screen = ToScreen(wp.rcNormalPosition, w.IsToolWindow, layout);
        }

        var mon = layout.FromRect(screen);
        if (max && !min && layout.FromHandle(Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST)) is { } maxMon && maxMon != mon)
        {
            // Maximized on another monitor than its restore rectangle: move the restore rectangle there.
            screen = screen.Offset(maxMon.Bounds.Left - mon.Bounds.Left, maxMon.Bounds.Top - mon.Bounds.Top);
            mon = maxMon;
        }

        Guid desktop = _vd.GetDesktopId(h);
        return new WindowRecord
        {
            ProcessPath = w.ProcessPath,
            ProcessName = w.ProcessName,
            ClassName = w.ClassName,
            Title = w.Title,
            Tabs = new List<string>(w.Tabs),
            MonitorId = mon.Id,
            MonitorWidth = mon.Bounds.Width,
            MonitorHeight = mon.Bounds.Height,
            X = screen.Left - mon.Bounds.Left,
            Y = screen.Top - mon.Bounds.Top,
            Width = Math.Min(screen.Width, Math.Max(mon.Bounds.Width, 200) * 2),
            Height = Math.Min(screen.Height, Math.Max(mon.Bounds.Height, 200) * 2),
            Maximized = max || (min && (wp.flags & Native.WPF_RESTORETOMAXIMIZED) != 0),
            Minimized = min,
            DesktopId = desktop,
            DesktopIndex = desktops.IndexOf(desktop),
        };
    }

    // ------------------------------------------------------------------ restore

    public RestoreResult RestoreAll(string reason)
    {
        var layout = MonitorLayout.Current();
        bool hasExact = _data.Layouts.Layouts.ContainsKey(layout.Key);
        var windows = WindowEnumerator.GetWindows(_vd).Where(_scope).ToList();
        var candidates = BuildCandidates(layout);

        // Greedy best-first assignment: every window gets at most one record and vice versa.
        var pairs = new List<(LiveWindow w, Candidate c, int score)>();
        foreach (var w in windows)
        {
            Guid liveDesktop = _vd.GetDesktopId(w.Handle);
            foreach (var c in candidates)
            {
                int s = Score(w, liveDesktop, c);
                if (s >= 0) pairs.Add((w, c, s));
            }
        }

        var usedWindows = new HashSet<IntPtr>();
        var usedRecords = new HashSet<Guid>();
        var matches = new List<(LiveWindow w, Candidate c, int score)>();
        foreach (var p in pairs.OrderByDescending(p => p.score))
        {
            if (usedWindows.Contains(p.w.Handle) || usedRecords.Contains(p.c.Record.Id)) continue;
            usedWindows.Add(p.w.Handle);
            usedRecords.Add(p.c.Record.Id);
            matches.Add(p);
        }

        IntPtr foreground = Native.GetForegroundWindow();
        int moved = 0;
        foreach (var (w, c, score) in matches)
        {
            _assigned[w.Handle] = c.Record.Id;
            _known.Add(w.Handle);
            bool changed = Apply(w, c.Record, layout);
            if (changed) moved++;
            Log.Write($"  {w} -> saved \"{c.Record.Title}\" (desktop {c.Record.DesktopIndex + 1}, score {score}){(changed ? " moved" : "")}");
        }
        if (moved > 0 && foreground != IntPtr.Zero && Native.GetForegroundWindow() != foreground)
            Native.SetForegroundWindow(foreground);

        Log.Write($"Restore ({reason}): layout {layout.Key} [{layout.Description}], saved={hasExact}, " +
                  $"{windows.Count} windows open, {matches.Count} matched, {moved} moved");
        return new RestoreResult(matches.Count, moved, layout.Description, hasExact);
    }

    /// <summary>Places one newly opened window. Returns false if it should be tried again later.</summary>
    private bool RestoreSingle(IntPtr h, TimeSpan age)
    {
        var w = WindowEnumerator.Describe(h);
        if (w == null || !_scope(w)) return true;
        // A browser restoring its session may not have its tab strip filled yet; without the tabs we
        // cannot tell its windows apart, so wait a little (but not forever).
        if (TabReader.IsBrowser(w) && w.Tabs.Count == 0 && age < BrowserTabWait) return false;

        var layout = MonitorLayout.Current();
        var taken = _assigned.Values.ToHashSet();
        Guid liveDesktop = _vd.GetDesktopId(h);
        var best = BuildCandidates(layout)
            .Where(c => !taken.Contains(c.Record.Id))
            .Select(c => (c, score: Score(w, liveDesktop, c)))
            .Where(x => x.score >= 0)
            .OrderByDescending(x => x.score)
            .FirstOrDefault();
        if (best.c == null) return true;

        _assigned[h] = best.c.Record.Id;
        bool moved = Apply(w, best.c.Record, layout);
        Log.Write($"New window {w}: {(moved ? "restored" : "already in place")} as saved \"{best.c.Record.Title}\" " +
                  $"(desktop {best.c.Record.DesktopIndex + 1}, score {best.score})" +
                  (best.c.Exact ? "" : $" from layout [{best.c.Snapshot.Description}]"));
        return true;
    }

    private sealed record Candidate(WindowRecord Record, LayoutSnapshot Snapshot, bool Exact, int Rank);

    /// <summary>
    /// Records usable in the current monitor layout: all records saved for exactly this layout, plus
    /// records from other layouts whose monitor (same physical monitor, same resolution) is connected now.
    /// The latter lets a window remember its place per monitor even in combinations never saved.
    /// </summary>
    private List<Candidate> BuildCandidates(MonitorLayout layout)
    {
        var list = new List<Candidate>();
        if (_data.Layouts.Layouts.TryGetValue(layout.Key, out var exact))
            list.AddRange(exact.Windows.Select(r => new Candidate(r, exact, true, 0)));

        int rank = 1;
        foreach (var snap in _data.Layouts.Layouts.Values
                     .Where(s => s.Key != layout.Key)
                     .OrderByDescending(s => s.SavedAt))
        {
            foreach (var r in snap.Windows)
            {
                var m = layout.FindById(r.MonitorId);
                if (m != null && m.Bounds.Width == r.MonitorWidth && m.Bounds.Height == r.MonitorHeight)
                    list.Add(new Candidate(r, snap, false, rank));
            }
            rank++;
        }
        return list;
    }

    /// <summary>
    /// How well an open window fits a saved record (-1: not the same app). Process and window class must
    /// match. Browser windows are told apart by their tabs, because their title only shows the active tab.
    /// </summary>
    private static int Score(LiveWindow w, Guid liveDesktop, Candidate c)
    {
        var r = c.Record;
        if (!string.Equals(w.ProcessName, r.ProcessName, StringComparison.OrdinalIgnoreCase)) return -1;
        if (w.ClassName != r.ClassName) return -1;
        bool sameTitle = w.Title == r.Title;
        // UWP apps all live in ApplicationFrameHost.exe; only the title tells them apart.
        if (w.ClassName == "ApplicationFrameWindow" && !sameTitle) return -1;

        int score = c.Exact ? 1000 : Math.Max(100, 500 - c.Rank * 10);
        bool browser = TabReader.IsBrowser(w);
        if (browser && r.Tabs.Count > 0 && w.Tabs.Count > 0)
            score += (int)Math.Round(TabReader.Similarity(w.Tabs, r.Tabs) * 400);
        if (sameTitle) score += browser ? 50 : 200;
        if (string.Equals(w.ProcessPath, r.ProcessPath, StringComparison.OrdinalIgnoreCase)) score += 50;
        // Tie-breaker: a window already on the saved desktop is probably the saved one.
        if (liveDesktop != Guid.Empty && liveDesktop == r.DesktopId) score += 30;
        return score;
    }

    /// <summary>Moves one window to its saved place. Returns true if anything was changed.</summary>
    private bool Apply(LiveWindow w, WindowRecord r, MonitorLayout layout)
    {
        var h = w.Handle;
        var mon = layout.FindById(r.MonitorId);
        if (mon == null || mon.Bounds.Width != r.MonitorWidth || mon.Bounds.Height != r.MonitorHeight) return false;
        if (!Native.IsWindow(h) || Native.IsHungAppWindow(h)) return false;

        try
        {
            var target = new Native.RECT(mon.Bounds.Left + r.X, mon.Bounds.Top + r.Y,
                                         mon.Bounds.Left + r.X + r.Width, mon.Bounds.Top + r.Y + r.Height);
            Guid targetDesktop = Settings.RestoreVirtualDesktops ? ResolveDesktop(r) : Guid.Empty;

            var wp = Native.WINDOWPLACEMENT.Create();
            Native.GetWindowPlacement(h, ref wp);
            bool isMin = Native.IsIconic(h);
            bool isMax = Native.IsZoomed(h);
            var curNormal = ToScreen(wp.rcNormalPosition, w.IsToolWindow, layout);
            bool changed = false;

            if (isMin)
            {
                // Never un-minimize: just fix where it will come back.
                bool restoreToMax = (wp.flags & Native.WPF_RESTORETOMAXIMIZED) != 0;
                if (!curNormal.NearlyEquals(target) || restoreToMax != r.Maximized)
                {
                    wp.rcNormalPosition = ToWorkspace(target, w.IsToolWindow, layout);
                    wp.showCmd = Native.SW_SHOWMINNOACTIVE;
                    wp.flags = r.Maximized ? Native.WPF_RESTORETOMAXIMIZED : 0;
                    Native.SetWindowPlacement(h, ref wp);
                    changed = true;
                }
            }
            else if (r.Maximized)
            {
                bool rightMonitor = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST) == mon.Handle;
                if (!(isMax && rightMonitor && curNormal.NearlyEquals(target)))
                {
                    // Maximizing activates the window. Activating a window on another virtual desktop
                    // would switch desktops, so bring it here first; it is moved back below.
                    if (!_vd.IsOnCurrentDesktop(h))
                    {
                        if (!_vd.CanMove) return false;   // cannot do it without switching desktops

                        if (targetDesktop == Guid.Empty) targetDesktop = _vd.GetDesktopId(h);
                        _vd.MoveToDesktop(h, _vd.CurrentDesktopId());
                    }
                    if (isMax)
                    {
                        wp.showCmd = Native.SW_SHOWNOACTIVATE;
                        wp.flags = 0;
                        wp.rcNormalPosition = ToWorkspace(target, w.IsToolWindow, layout);
                        Native.SetWindowPlacement(h, ref wp);
                    }
                    MoveExactly(h, target);
                    var fresh = Native.WINDOWPLACEMENT.Create();
                    Native.GetWindowPlacement(h, ref fresh);
                    fresh.showCmd = Native.SW_SHOWMAXIMIZED;
                    fresh.flags = 0;
                    Native.SetWindowPlacement(h, ref fresh);
                    changed = true;
                }
            }
            else
            {
                if (isMax)
                {
                    wp.showCmd = Native.SW_SHOWNOACTIVATE;
                    wp.flags = 0;
                    wp.rcNormalPosition = ToWorkspace(target, w.IsToolWindow, layout);
                    Native.SetWindowPlacement(h, ref wp);
                    changed = true;
                }
                changed |= MoveExactly(h, target);
            }

            if (targetDesktop != Guid.Empty && _vd.GetDesktopId(h) != targetDesktop)
                changed |= _vd.MoveToDesktop(h, targetDesktop);

            return changed;
        }
        catch (Exception ex)
        {
            Log.Write($"Restoring {w} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// SetWindowPos to the exact rectangle. Moving across monitors with different scaling makes
    /// DPI-aware apps resize themselves, so the size is applied a second time if needed.
    /// </summary>
    private static bool MoveExactly(IntPtr h, Native.RECT target)
    {
        const uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
        bool changed = false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (Native.GetWindowRect(h, out var cur) && cur.Equals(target)) break;
            Native.SetWindowPos(h, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height, flags);
            changed = true;
        }
        return changed;
    }

    private Guid ResolveDesktop(WindowRecord r)
    {
        var desktops = VirtualDesktops.ListDesktopIds();
        if (r.DesktopId != Guid.Empty && desktops.Contains(r.DesktopId)) return r.DesktopId;
        if (r.DesktopIndex >= 0 && r.DesktopIndex < desktops.Count) return desktops[r.DesktopIndex];
        return Guid.Empty;
    }

    /// <summary>
    /// WINDOWPLACEMENT.rcNormalPosition uses "workspace" coordinates (relative to the work area,
    /// i.e. shifted by a taskbar on the top or left edge) for all windows except tool windows.
    /// </summary>
    private static Native.RECT ToScreen(Native.RECT ws, bool toolWindow, MonitorLayout layout)
    {
        if (toolWindow) return ws;
        var m = layout.FromRect(ws);
        return ws.Offset(m.WorkArea.Left - m.Bounds.Left, m.WorkArea.Top - m.Bounds.Top);
    }

    private static Native.RECT ToWorkspace(Native.RECT screen, bool toolWindow, MonitorLayout layout)
    {
        if (toolWindow) return screen;
        var m = layout.FromRect(screen);
        return screen.Offset(m.Bounds.Left - m.WorkArea.Left, m.Bounds.Top - m.WorkArea.Top);
    }

    // ------------------------------------------------------------------ background tick

    /// <summary>The monitor setup changed (or may have): restore once it has settled.</summary>
    public void DisplayChanged(string reason)
    {
        Log.Write("Display change: " + reason);
        _settling = true;
        _settlingKey = null;
    }

    /// <summary>Called about once per second on the UI thread.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;

        foreach (var dead in _known.Where(h => !Native.IsWindow(h)).ToList())
        {
            _known.Remove(dead);
            _assigned.Remove(dead);
            _pending.Remove(dead);
        }

        var handles = WindowEnumerator.EnumCandidates(_vd);

        if (_settling)
        {
            var key = MonitorLayout.Current().Key;
            if (key != _settlingKey)
            {
                _settlingKey = key;
                _stableSince = now;
                return;
            }
            if (now - _stableSince < SettleTime) return;

            _settling = false;
            _pending.Clear();
            foreach (var h in handles) _known.Add(h);
            if (Settings.RestoreOnDisplayChange)
            {
                var result = RestoreAll("monitor setup settled");
                if (result.Moved > 0)
                    Notify?.Invoke($"Restored {result.Moved} window(s) for {result.LayoutDescription}");
            }
            return;
        }

        foreach (var h in handles)
        {
            if (_known.Add(h) && Settings.RestoreNewWindows) _pending[h] = now;
        }

        foreach (var (h, seen) in _pending.ToList())
        {
            var age = now - seen;
            if (age < NewWindowDelay) continue;
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || RestoreSingle(h, age))
                _pending.Remove(h);
        }
    }

    // ------------------------------------------------------------------ layout management

    public string CurrentLayoutKey => MonitorLayout.Current().Key;

    public void DeleteLayout(string key)
    {
        if (_data.Layouts.Layouts.Remove(key))
        {
            _data.SaveLayouts();
            var removed = _assigned.Where(kv => !_data.Layouts.Layouts.Values.Any(s => s.Windows.Any(w => w.Id == kv.Value)))
                                   .Select(kv => kv.Key).ToList();
            foreach (var h in removed) _assigned.Remove(h);
            Log.Write("Deleted layout " + key);
        }
    }
}
