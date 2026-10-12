using System.Runtime.InteropServices;
using System.Text;
using MapaOkien.Backend.Native;

namespace MapaOkien.Backend.Services;

public sealed record WindowInfo(
    IntPtr Hwnd,
    string Title,
    string ClassName,
    string ProcessName,
    bool Minimized,
    bool? OnCurrentDesktop)
{
    /// <summary>Klucz noda wg specyfikacji: proces + tytuł okna.</summary>
    public string Key => KeyFor(ProcessName, Title);

    public static string KeyFor(string process, string title) => $"win:{process.ToLowerInvariant()}|{title}";
}

/// <summary>
/// Okna „jak w Alt+Tab”, ze wszystkich monitorów i pulpitów wirtualnych. Filtry odsiewające
/// okna pomocnicze pochodzą z testu technicznego (docs/wyniki-testu-technicznego.md).
/// </summary>
public static class WindowEnumerator
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", // treść aplikacji UWP – widoczna przez ApplicationFrameWindow
    };

    // Ukryte okna dostępności Chromium/Electron (pojawiały się w msedge i Logseq).
    private static readonly HashSet<string> IgnoredTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome Legacy Window",
    };

    /// <summary>Procesy, których okna nigdy nie są nodami: backend i frontend (okno siatki).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> OwnProcessIds = new();

    /// <summary>Okna mniejsze niż to (w px) nie są nodami – to paski, dymki, uchwyty.</summary>
    private const int MinSizePx = 80;

    public static readonly Dictionary<string, string> SupportedBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "edge",
        ["chrome"] = "chrome",
    };

    public static readonly HashSet<string> OtherBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox", "opera", "brave", "vivaldi", "iexplore", "waterfox", "librewolf", "tor",
    };

    private static readonly HashSet<string> OfficeApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "winword", "excel", "powerpnt", "outlook", "olk",
    };

    public static Edge EdgeFor(string processName)
    {
        if (SupportedBrowsers.ContainsKey(processName)) return Edge.Top;
        if (OfficeApps.Contains(processName)) return Edge.Left;
        if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return Edge.Bottom;
        return Edge.Right;
    }

    public static List<WindowInfo> List()
    {
        var result = new List<WindowInfo>();
        Win32.EnumWindows((hwnd, _) =>
        {
            var info = TryDescribe(hwnd);
            if (info != null) result.Add(info);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Opis okna, jeśli kwalifikuje się jako nod; w przeciwnym razie null.</summary>
    public static WindowInfo? TryDescribe(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd)) return null;

        var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        var owner = Win32.GetWindow(hwnd, Win32.GW_OWNER);
        bool appWindow = (ex & Win32.WS_EX_APPWINDOW) != 0;
        if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && !appWindow) return null;
        if ((ex & Win32.WS_EX_NOACTIVATE) != 0 && !appWindow) return null;
        if (owner != IntPtr.Zero && !appWindow) return null;

        var title = GetTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title) || IgnoredTitles.Contains(title)) return null;

        var cls = GetClass(hwnd);
        if (IgnoredClasses.Contains(cls)) return null;

        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId || OwnProcessIds.ContainsKey(pid)) return null;

        Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int));
        var desktopId = VirtualDesktops.DesktopId(hwnd);
        var onCurrent = VirtualDesktops.IsOnCurrent(hwnd);

        // Ukrycie (cloak) przez powłokę jest normalne dla okien z innych pulpitów wirtualnych –
        // takie okna zostawiamy. Pozostałe ukryte okna (np. uśpione aplikacje UWP) pomijamy.
        if (cloaked != 0)
        {
            bool otherDesktop = cloaked == Win32.DWM_CLOAKED_SHELL
                                && desktopId is { } id && id != Guid.Empty
                                && onCurrent == false;
            if (!otherDesktop) return null;
        }

        bool minimized = Win32.IsIconic(hwnd);
        if (!minimized)
        {
            Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT rect,
                Marshal.SizeOf<Win32.RECT>());
            if (rect.Width < MinSizePx || rect.Height < MinSizePx) return null;
        }

        return new WindowInfo(hwnd, title, cls, GetProcessName(pid), minimized, onCurrent);
    }

    public static string GetTitle(IntPtr hwnd)
    {
        int len = Win32.GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetProcessName(uint pid)
    {
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return $"pid{pid}";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Win32.QueryFullProcessImageName(h, 0, sb, ref size)
                ? Path.GetFileNameWithoutExtension(sb.ToString())
                : $"pid{pid}";
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }
}
