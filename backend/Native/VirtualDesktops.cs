using System.Runtime.InteropServices;

namespace MapaOkien.Backend.Native;

/// <summary>
/// Udokumentowany interfejs IVirtualDesktopManager (shobjidl_core.h). Pozwala sprawdzić,
/// na którym pulpicie wirtualnym jest okno. Nie pozwala przełączać pulpitów – to
/// sprawdzamy pośrednio przez SetForegroundWindow (patrz WindowSwitcher).
/// </summary>
[ComImport]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

    [PreserveSig]
    int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

    [PreserveSig]
    int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

[ComImport]
[Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
internal class VirtualDesktopManagerClass
{
}

internal static class VirtualDesktops
{
    private static readonly Lazy<IVirtualDesktopManager?> Manager = new(() =>
    {
        try { return (IVirtualDesktopManager)new VirtualDesktopManagerClass(); }
        catch { return null; }
    });

    public static bool Available => Manager.Value != null;

    public static Guid? DesktopId(IntPtr hwnd)
    {
        var m = Manager.Value;
        if (m == null) return null;
        return m.GetWindowDesktopId(hwnd, out var id) == 0 ? id : null;
    }

    public static bool? IsOnCurrent(IntPtr hwnd)
    {
        var m = Manager.Value;
        if (m == null) return null;
        return m.IsWindowOnCurrentVirtualDesktop(hwnd, out var on) == 0 ? on != 0 : null;
    }
}
