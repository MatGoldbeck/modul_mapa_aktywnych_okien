using System.Runtime.InteropServices;

namespace SpikeTest;

/// <summary>
/// Rozmiar siatki (Grid) zależny od rozdzielczości monitora głównego, odczytanej raz przy
/// uruchomieniu (docs/specyfikacja.md, pkt 5).
/// </summary>
internal static class Grid
{
    // (maks. szerokość, maks. wysokość, liczba kolumn = wierszy) – progi włącznie.
    private static readonly (int W, int H, int Size)[] Tiers =
    {
        (1920, 1080, 5),
        (2560, 1440, 6),
        (3840, 2160, 8),
    };

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private const int DESKTOPVERTRES = 117;
    private const int DESKTOPHORZRES = 118;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public static int Width { get; private set; }
    public static int Height { get; private set; }
    public static int Size { get; private set; }
    public static int Cells => Size * Size;

    /// <summary>
    /// Wywołać na samym początku programu: bez świadomości DPI Windows podaje rozdzielczość
    /// przeskalowaną (np. 4K przy 150% → 2560×1440), co dałoby złą siatkę i złe rozmiary okien.
    /// </summary>
    public static void Init()
    {
        SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            Width = GetDeviceCaps(hdc, DESKTOPHORZRES);
            Height = GetDeviceCaps(hdc, DESKTOPVERTRES);
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
        Size = SizeFor(Width, Height);
    }

    public static int SizeFor(int width, int height)
    {
        foreach (var t in Tiers)
            if (width <= t.W && height <= t.H) return t.Size;
        return Tiers[^1].Size; // powyżej 3840×2160 – największa siatka
    }

    /// <summary>Komunikat o przepełnieniu albo null, gdy wszystkie nody się mieszczą.</summary>
    public static string? OverflowMessage(int nodes) =>
        nodes <= Cells ? null
            : $"Liczba nodów ({nodes}) przekracza liczbę pól siatki ({Cells}). Nie wyświetlono {nodes - Cells} nodów.";
}
