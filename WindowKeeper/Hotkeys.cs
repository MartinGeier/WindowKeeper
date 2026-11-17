using System.Runtime.InteropServices;

namespace WindowKeeper;

/// <summary>
/// System-wide keyboard shortcuts (RegisterHotKey). A registered combination works in every app, and
/// no other app receives it while WindowKeeper runs.
/// </summary>
internal sealed class Hotkeys : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public Hotkeys()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
    }

    /// <summary>Registers e.g. "Ctrl+Alt+Shift+S". Returns null on success, otherwise why it failed.</summary>
    public string? Register(string combination, Action action)
    {
        if (!TryParse(combination, out uint modifiers, out uint vk))
            return $"\"{combination}\" is not a valid shortcut";
        int id = _nextId++;
        if (!RegisterHotKey(Handle, id, modifiers | MOD_NOREPEAT, vk))
            return $"{combination} is already used by another program";
        _actions[id] = action;
        return null;
    }

    public void UnregisterAll()
    {
        foreach (int id in _actions.Keys) UnregisterHotKey(Handle, id);
        _actions.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue(m.WParam.ToInt32(), out var action))
        {
            try { action(); }
            catch (Exception ex) { Log.Write("Hotkey action failed: " + ex); }
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Parses "Ctrl+Alt+Shift+S", "Win+F9", "Ctrl+Shift+D1", ... (key names as in System.Windows.Forms.Keys).</summary>
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts[..^1])
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": case "windows": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }
        string key = parts[^1];
        if (key.Length == 1 && char.IsDigit(key[0])) key = "D" + key;
        if (!Enum.TryParse(key, ignoreCase: true, out Keys k) || k == Keys.None || (k & Keys.Modifiers) != 0) return false;
        vk = (uint)k;
        return modifiers != 0 || (k >= Keys.F1 && k <= Keys.F24);   // a plain letter alone would break typing
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
