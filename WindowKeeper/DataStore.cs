using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowKeeper;

internal sealed class MonitorRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dpi { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool Primary { get; set; }
}

internal sealed class WindowRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProcessPath { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Browser windows: titles of the open tabs (identifies the window better than its title).</summary>
    public List<string> Tabs { get; set; } = new();

    /// <summary>Monitor the window was on; position is relative to that monitor's top-left corner.</summary>
    public string MonitorId { get; set; } = "";
    public int MonitorWidth { get; set; }
    public int MonitorHeight { get; set; }

    /// <summary>Normal (restored) rectangle, relative to the monitor, in physical pixels.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool Maximized { get; set; }
    public bool Minimized { get; set; }

    public Guid DesktopId { get; set; }
    /// <summary>0-based index of the virtual desktop, used if the desktop id no longer exists.</summary>
    public int DesktopIndex { get; set; } = -1;
}

/// <summary>All window positions saved for one monitor configuration.</summary>
internal sealed class LayoutSnapshot
{
    public string Key { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public List<MonitorRecord> Monitors { get; set; } = new();
    public List<WindowRecord> Windows { get; set; } = new();
}

internal sealed class LayoutFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, LayoutSnapshot> Layouts { get; set; } = new();
}

internal sealed class Settings
{
    public bool RestoreOnDisplayChange { get; set; } = true;
    public bool RestoreNewWindows { get; set; } = true;
    public bool RestoreVirtualDesktops { get; set; } = true;

    /// <summary>Global shortcut to save the layout, e.g. "Ctrl+Alt+Shift+S". Empty = no shortcut.</summary>
    public string SaveHotkey { get; set; } = "Ctrl+Alt+Shift+S";
    /// <summary>Global shortcut to restore the layout. Empty = no shortcut.</summary>
    public string RestoreHotkey { get; set; } = "Ctrl+Alt+Shift+R";
}

/// <summary>Loads and saves layouts.json and settings.json in the data folder.</summary>
internal sealed class DataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Folder { get; }
    public LayoutFile Layouts { get; private set; } = new();
    public Settings Settings { get; private set; } = new();

    private string LayoutsPath => Path.Combine(Folder, "layouts.json");
    private string SettingsPath => Path.Combine(Folder, "settings.json");

    public DataStore(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        Layouts = Load<LayoutFile>(LayoutsPath) ?? new LayoutFile();
        Settings = Load<Settings>(SettingsPath) ?? new Settings();
    }

    public void SaveLayouts() => Write(LayoutsPath, Layouts);
    public void SaveSettings() => Write(SettingsPath, Settings);

    private static T? Load<T>(string path) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), JsonOptions);
            }
            catch (Exception ex)
            {
                Log.Write($"Could not read {candidate}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>Atomic write: a crash or power loss never leaves a half-written file behind.</summary>
    private static void Write<T>(string path, T value)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
        else File.Move(tmp, path);
    }
}
