using System.Text;

namespace SpikeTest;

internal sealed record WindowInfo(
    IntPtr Hwnd,
    string Title,
    string ClassName,
    string ProcessName,
    uint ProcessId,
    bool Minimized,
    int Cloaked,
    Guid? DesktopId,
    bool? OnCurrentDesktop,
    IntPtr Monitor,
    Native.RECT Bounds)
{
    /// <summary>Klucz noda wg specyfikacji: proces + tytuł okna.</summary>
    public string Key => $"{ProcessName.ToLowerInvariant()}|{Title}";

    public string Edge => WindowEnumerator.EdgeFor(ProcessName);
}

/// <summary>
/// Lista okien „jak w Alt+Tab”, ze wszystkich monitorów i pulpitów wirtualnych.
/// </summary>
internal static class WindowEnumerator
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", // treść aplikacji UWP – widoczna przez ApplicationFrameWindow
    };

    public static readonly HashSet<string> SupportedBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome",
    };

    public static readonly HashSet<string> OtherBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox", "opera", "brave", "vivaldi", "iexplore", "waterfox", "librewolf", "tor",
    };

    private static readonly HashSet<string> OfficeApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "winword", "excel", "powerpnt", "outlook", "olk",
    };

    public static string EdgeFor(string processName)
    {
        if (SupportedBrowsers.Contains(processName)) return "góra";
        if (OfficeApps.Contains(processName)) return "lewa";
        if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return "dół";
        return "prawa";
    }

    public static List<WindowInfo> List()
    {
        var result = new List<WindowInfo>();
        Native.EnumWindows((hwnd, _) =>
        {
            var info = TryDescribe(hwnd);
            if (info != null) result.Add(info);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Zwraca opis okna, jeśli kwalifikuje się jako nod; w przeciwnym razie null.</summary>
    public static WindowInfo? TryDescribe(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return null;
        if (hwnd == Native.GetConsoleWindow()) return null;

        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        var owner = Native.GetWindow(hwnd, Native.GW_OWNER);
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return null;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0 && !appWindow) return null;
        if (owner != IntPtr.Zero && !appWindow) return null;

        var title = GetTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var cls = GetClass(hwnd);
        if (IgnoredClasses.Contains(cls)) return null;

        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int));
        var desktopId = VirtualDesktops.DesktopId(hwnd);
        var onCurrent = VirtualDesktops.IsOnCurrent(hwnd);

        // Ukrycie (cloak) przez powłokę jest normalne dla okien z innych pulpitów wirtualnych –
        // takie okna zostawiamy. Pozostałe ukryte okna (np. uśpione aplikacje UWP) pomijamy.
        if (cloaked != 0)
        {
            bool otherDesktop = cloaked == Native.DWM_CLOAKED_SHELL
                                && desktopId is { } id && id != Guid.Empty
                                && onCurrent == false;
            if (!otherDesktop) return null;
        }

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return null;

        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT rect,
            System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>());

        return new WindowInfo(hwnd, title, cls, GetProcessName(pid), pid, Native.IsIconic(hwnd),
            cloaked, desktopId, onCurrent, Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), rect);
    }

    public static string GetTitle(IntPtr hwnd)
    {
        int len = Native.GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetProcessName(uint pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return $"pid:{pid}";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref size)
                ? Path.GetFileNameWithoutExtension(sb.ToString())
                : $"pid:{pid}";
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }
}
