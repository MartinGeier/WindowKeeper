using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace WindowKeeper;

/// <summary>Notification-area icon with the menu. All work happens on the UI thread.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly DataStore _data;
    private readonly LayoutEngine _engine;
    private readonly VirtualDesktops _vd;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public TrayApp(DataStore data, VirtualDesktops vd, Func<LiveWindow, bool>? scope = null)
    {
        _data = data;
        _vd = vd;
        _engine = new LayoutEngine(data, vd, scope);
        _engine.Notify += msg => ShowBalloon(msg);

        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "WindowKeeper",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        // Left click opens the menu too.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(_icon, null);
        };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _timer.Tick += (_, _) =>
        {
            try { _engine.Tick(); }
            catch (Exception ex) { Log.Write("Tick failed: " + ex); }
        };
        _timer.Start();
        Log.Write("WindowKeeper started. " + vd.Status);
    }

    private void OnDisplaySettingsChanged(object? s, EventArgs e) => _engine.DisplayChanged("display settings changed");

    private void OnPowerModeChanged(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _engine.DisplayChanged("resume from sleep");
    }

    private void OnSessionSwitch(object? s, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _engine.DisplayChanged("session " + e.Reason);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var layout = MonitorLayout.Current();
        bool hasCurrent = _data.Layouts.Layouts.TryGetValue(layout.Key, out var current);

        var header = new ToolStripMenuItem($"Monitors: {layout.Description}") { Enabled = false };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripMenuItem(hasCurrent
            ? $"Saved: {current!.Windows.Count} windows, {current.SavedAt:g}"
            : "No layout saved for these monitors yet") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        var save = new ToolStripMenuItem("Save window layout for these monitors", null, (_, _) => Save())
        {
            Font = new Font(_menu.Font, FontStyle.Bold),
        };
        _menu.Items.Add(save);
        _menu.Items.Add(new ToolStripMenuItem("Restore saved layout now", null, (_, _) => Restore()));

        var layouts = new ToolStripMenuItem("Saved layouts");
        foreach (var snap in _data.Layouts.Layouts.Values.OrderByDescending(s => s.SavedAt))
        {
            var item = new ToolStripMenuItem($"{snap.Description}  ({snap.Windows.Count} windows, {snap.SavedAt:g})")
            {
                Checked = snap.Key == layout.Key,
            };
            foreach (var w in snap.Windows.OrderBy(w => w.ProcessName))
            {
                string title = w.Title.Length > 60 ? w.Title[..57] + "..." : w.Title;
                string state = w.Maximized ? "maximized" : $"{w.Width}x{w.Height}";
                item.DropDownItems.Add(new ToolStripMenuItem($"{w.ProcessName}: {title}  [{state}]") { Enabled = false });
            }
            item.DropDownItems.Add(new ToolStripSeparator());
            string key = snap.Key;
            item.DropDownItems.Add(new ToolStripMenuItem("Delete this layout", null, (_, _) =>
            {
                if (MessageBox.Show($"Delete the saved layout for\n{snap.Description}?", "WindowKeeper",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    _engine.DeleteLayout(key);
            }));
            layouts.DropDownItems.Add(item);
        }
        if (layouts.DropDownItems.Count == 0)
            layouts.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
        _menu.Items.Add(layouts);
        _menu.Items.Add(new ToolStripSeparator());

        var s = _data.Settings;
        _menu.Items.Add(Toggle("Restore automatically when monitors change", s.RestoreOnDisplayChange,
            v => s.RestoreOnDisplayChange = v));
        _menu.Items.Add(Toggle("Restore windows when apps open them", s.RestoreNewWindows,
            v => s.RestoreNewWindows = v));
        var vdItem = Toggle("Restore virtual desktops", s.RestoreVirtualDesktops, v => s.RestoreVirtualDesktops = v);
        vdItem.ToolTipText = _vd.Status;
        _menu.Items.Add(vdItem);

        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled };
        autostart.Click += (_, _) =>
        {
            try { Autostart.Set(!Autostart.IsEnabled); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WindowKeeper"); }
        };
        _menu.Items.Add(autostart);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_data.Folder}\"") { UseShellExecute = true })));
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
    }

    private ToolStripMenuItem Toggle(string text, bool value, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = value };
        item.Click += (_, _) =>
        {
            set(!item.Checked);
            _data.SaveSettings();
        };
        return item;
    }

    private void Save()
    {
        try
        {
            var r = _engine.SaveCurrent();
            ShowBalloon($"Saved {r.Windows} window(s) for {r.LayoutDescription}");
        }
        catch (Exception ex)
        {
            Log.Write("Save failed: " + ex);
            MessageBox.Show("Saving failed: " + ex.Message, "WindowKeeper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Restore()
    {
        try
        {
            var r = _engine.RestoreAll("menu");
            ShowBalloon(r.HasSavedLayout || r.Matched > 0
                ? $"Restored {r.Moved} window(s) ({r.Matched} recognized)"
                : $"Nothing saved for {r.LayoutDescription} yet");
        }
        catch (Exception ex)
        {
            Log.Write("Restore failed: " + ex);
            MessageBox.Show("Restoring failed: " + ex.Message, "WindowKeeper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowBalloon(string text)
    {
        _icon.BalloonTipTitle = "WindowKeeper";
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(3000);
    }

    /// <summary>Two overlapping windows, drawn at runtime so no .ico file is needed.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            DrawWindow(g, new Rectangle(2, 4, 20, 16), Color.FromArgb(90, 90, 90));
            DrawWindow(g, new Rectangle(10, 12, 20, 16), Color.FromArgb(0, 120, 215));
        }
        return Icon.FromHandle(bmp.GetHicon());

        static void DrawWindow(Graphics g, Rectangle r, Color c)
        {
            using var fill = new SolidBrush(Color.White);
            using var bar = new SolidBrush(c);
            using var pen = new Pen(c, 2);
            g.FillRectangle(fill, r);
            g.FillRectangle(bar, r.X, r.Y, r.Width, 5);
            g.DrawRectangle(pen, r);
        }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _icon.Visible = false;
        _icon.Dispose();
        Log.Write("WindowKeeper stopped.");
        base.ExitThreadCore();
    }
}
