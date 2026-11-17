using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WindowKeeper;

internal sealed class MonitorInfo
{
    public IntPtr Handle { get; init; }
    /// <summary>Stable id of the physical monitor (device interface path, e.g. \\?\DISPLAY#DEL4123#...).</summary>
    public string Id { get; init; } = "";
    /// <summary>Short readable name, e.g. the EDID model code "DEL4123".</summary>
    public string Name { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public Native.RECT Bounds { get; init; }
    public Native.RECT WorkArea { get; init; }
    public int Dpi { get; init; } = 96;
    public bool Primary { get; init; }

    public string Resolution => $"{Bounds.Width}x{Bounds.Height}";
    public int ScalePercent => (int)Math.Round(Dpi * 100.0 / 96);

    public override string ToString() =>
        $"{Name} {Resolution}{(ScalePercent != 100 ? $" @{ScalePercent}%" : "")}";
}

/// <summary>
/// The set of monitors currently connected. Its <see cref="Key"/> identifies the monitor configuration:
/// same physical monitors with the same resolutions and scaling give the same key, regardless of how
/// they are arranged relative to each other.
/// </summary>
internal sealed class MonitorLayout
{
    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public string Key { get; }
    public string Description { get; }

    private MonitorLayout(List<MonitorInfo> monitors)
    {
        Monitors = monitors;
        var canonical = string.Join(";", monitors
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => $"{m.Id}|{m.Resolution}|{m.Dpi}"));
        Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
        Description = string.Join(" + ", monitors
            .OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top)
            .Select(m => m.ToString()));
    }

    public static MonitorLayout Current()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(hMon, ref info)) return true;

            int dpi = 96;
            if (Native.GetDpiForMonitor(hMon, 0, out uint dx, out _) == 0) dpi = (int)dx;

            var (id, name) = GetMonitorIdentity(info.szDevice);
            list.Add(new MonitorInfo
            {
                Handle = hMon,
                Id = id,
                Name = name,
                DeviceName = info.szDevice,
                Bounds = info.rcMonitor,
                WorkArea = info.rcWork,
                Dpi = dpi,
                Primary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
            });
            return true;
        }, IntPtr.Zero);
        return new MonitorLayout(list);
    }

    /// <summary>Finds the physical monitor(s) attached to a display output like \\.\DISPLAY1.</summary>
    private static (string id, string name) GetMonitorIdentity(string deviceName)
    {
        var ids = new List<string>();
        var names = new List<string>();
        for (uint i = 0; ; i++)
        {
            var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            if (!Native.EnumDisplayDevices(deviceName, i, ref dd, Native.EDD_GET_DEVICE_INTERFACE_NAME)) break;
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ACTIVE) == 0 || string.IsNullOrEmpty(dd.DeviceID)) continue;
            ids.Add(dd.DeviceID.ToLowerInvariant());
            // \\?\DISPLAY#DEL4123#5&...#{guid}  ->  DEL4123
            var parts = dd.DeviceID.Split('#');
            names.Add(parts.Length > 1 ? parts[1] : dd.DeviceString);
        }
        if (ids.Count == 0) return (deviceName.ToLowerInvariant(), deviceName.TrimStart('\\', '.'));
        // Several ids only happen when monitors are cloned (mirrored) on one output.
        return (string.Join("+", ids), string.Join("+", names));
    }

    public MonitorInfo? FindById(string id) => Monitors.FirstOrDefault(m => m.Id == id);

    public MonitorInfo? FromHandle(IntPtr hMon) => Monitors.FirstOrDefault(m => m.Handle == hMon);

    /// <summary>Monitor with the largest overlap with the rectangle, or the nearest one.</summary>
    public MonitorInfo FromRect(Native.RECT r)
    {
        var best = Monitors.MaxBy(m => m.Bounds.IntersectionArea(r));
        if (best != null && best.Bounds.IntersectionArea(r) > 0) return best;
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        return Monitors.MinBy(m =>
        {
            long dx = Math.Max(0, Math.Max(m.Bounds.Left - cx, cx - m.Bounds.Right));
            long dy = Math.Max(0, Math.Max(m.Bounds.Top - cy, cy - m.Bounds.Bottom));
            return dx * dx + dy * dy;
        })!;
    }
}
