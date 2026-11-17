using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowKeeper;

/// <summary>
/// Virtual desktop support.
/// Reading which desktop a window is on uses the documented IVirtualDesktopManager.
/// Moving a window of ANOTHER process to a desktop is not possible with the documented API, so the
/// undocumented shell interfaces of Windows 11 24H2/25H2 (build 26100+) are used for that. They are
/// verified at startup against the registry; if anything does not match, moving is simply disabled
/// and window positions are still restored.
/// </summary>
internal sealed class VirtualDesktops
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    private IVirtualDesktopManager? _manager;
    private IVirtualDesktopManagerInternal? _internal;
    private IApplicationViewCollection? _views;

    public string Status { get; private set; } = "";
    public bool CanMove => _internal != null && _views != null;

    public VirtualDesktops()
    {
        try
        {
            _manager = (IVirtualDesktopManager)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"))!)!;
        }
        catch (Exception ex)
        {
            Log.Write("IVirtualDesktopManager unavailable: " + ex.Message);
        }
        InitInternal();
    }

    private void InitInternal()
    {
        _internal = null;
        _views = null;
        int build = Environment.OSVersion.Version.Build;
        if (build < 26100)
        {
            Status = $"Moving windows between desktops is not supported on Windows build {build} (needs 24H2 or newer).";
            return;
        }
        try
        {
            var shell = (IServiceProvider10)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239"))!)!;
            var sidInternal = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
            var iidInternal = typeof(IVirtualDesktopManagerInternal).GUID;
            var internalMgr = (IVirtualDesktopManagerInternal)shell.QueryService(ref sidInternal, ref iidInternal);
            var iidViews = typeof(IApplicationViewCollection).GUID;
            var views = (IApplicationViewCollection)shell.QueryService(ref iidViews, ref iidViews);

            // Sanity check: the internal API must report exactly the desktops listed in the registry.
            var fromApi = GetDesktopIds(internalMgr);
            var fromRegistry = ListDesktopIds();
            if (fromApi.Count != internalMgr.GetCount() ||
                (fromRegistry.Count > 0 && !fromApi.SequenceEqual(fromRegistry)))
            {
                Status = "Virtual desktop API check failed - moving windows between desktops disabled.";
                Log.Write(Status + $" api=[{string.Join(",", fromApi)}] registry=[{string.Join(",", fromRegistry)}]");
                return;
            }
            _internal = internalMgr;
            _views = views;
            Status = $"Virtual desktops: {fromApi.Count} desktop(s), moving windows between desktops enabled.";
        }
        catch (Exception ex)
        {
            Status = "Virtual desktop API unavailable - moving windows between desktops disabled.";
            Log.Write(Status + " " + ex.Message);
        }
    }

    private static List<Guid> GetDesktopIds(IVirtualDesktopManagerInternal mgr)
    {
        var result = new List<Guid>();
        foreach (var d in GetDesktops(mgr)) result.Add(d.GetId());
        return result;
    }

    private static List<IVirtualDesktop> GetDesktops(IVirtualDesktopManagerInternal mgr)
    {
        mgr.GetDesktops(out IObjectArray array);
        array.GetCount(out int count);
        var list = new List<IVirtualDesktop>(count);
        var iid = typeof(IVirtualDesktop).GUID;
        for (int i = 0; i < count; i++)
        {
            array.GetAt(i, ref iid, out object obj);
            list.Add((IVirtualDesktop)obj);
        }
        return list;
    }

    /// <summary>Desktop ids in their order (Desktop 1, Desktop 2, ...). Persisted by Windows across reboots.</summary>
    public static List<Guid> ListDesktopIds()
    {
        var result = new List<Guid>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            if (key?.GetValue("VirtualDesktopIDs") is byte[] bytes)
            {
                for (int i = 0; i + 16 <= bytes.Length; i += 16)
                    result.Add(new Guid(bytes.AsSpan(i, 16)));
            }
        }
        catch (Exception ex)
        {
            Log.Write("Reading virtual desktop list failed: " + ex.Message);
        }
        return result;
    }

    public Guid GetDesktopId(IntPtr hwnd)
    {
        if (_manager == null) return Guid.Empty;
        try
        {
            return _manager.GetWindowDesktopId(hwnd, out Guid id) == 0 ? id : Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    public bool IsOnCurrentDesktop(IntPtr hwnd)
    {
        if (_manager == null) return true;
        try
        {
            return _manager.IsWindowOnCurrentVirtualDesktop(hwnd, out int on) != 0 || on != 0;
        }
        catch
        {
            return true;
        }
    }

    public Guid CurrentDesktopId()
    {
        if (_internal != null)
        {
            try { return _internal.GetCurrentDesktop().GetId(); }
            catch { /* fall through to registry */ }
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            if (key?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } b) return new Guid(b);
        }
        catch { /* ignore */ }
        return Guid.Empty;
    }

    public bool MoveToDesktop(IntPtr hwnd, Guid desktopId) => TryMove(hwnd, desktopId, retry: true);

    private bool TryMove(IntPtr hwnd, Guid desktopId, bool retry)
    {
        if (desktopId == Guid.Empty || GetDesktopId(hwnd) == desktopId) return true;
        if (!CanMove) return false;
        try
        {
            var desktop = GetDesktops(_internal!).FirstOrDefault(d => d.GetId() == desktopId);
            if (desktop == null) return false;
            if (_views!.GetViewForHwnd(hwnd, out IApplicationView view) != 0 || view == null) return false;
            if (!_internal!.CanViewMoveDesktops(view)) return false;
            _internal.MoveViewToDesktop(view, desktop);
            return true;
        }
        catch (COMException ex) when (retry)
        {
            // Explorer may have been restarted; the shell objects must be recreated.
            Log.Write($"Desktop move failed (0x{ex.HResult:X8}), reconnecting to shell.");
            InitInternal();
            return TryMove(hwnd, desktopId, retry: false);
        }
        catch (Exception ex)
        {
            Log.Write("Desktop move failed: " + ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- COM interfaces

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider10
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    // Undocumented, Windows 11 24H2 / 25H2 (build 26100+). Only the leading methods we call are declared,
    // so later vtable changes cannot be hit by accident.

    [ComImport, Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationView { }

    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        [PreserveSig] int GetViews(out IObjectArray array);
        [PreserveSig] int GetViewsByZOrder(out IObjectArray array);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IObjectArray array);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
    }

    [ComImport, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktop
    {
        bool IsViewVisible(IApplicationView view);
        Guid GetId();
    }

    [ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManagerInternal
    {
        int GetCount();
        void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
        bool CanViewMoveDesktops(IApplicationView view);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
    }
}
