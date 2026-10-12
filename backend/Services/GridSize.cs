namespace MapaOkien.Backend.Services;

/// <summary>Rozmiar siatki wg rozdzielczości monitora głównego (specyfikacja pkt 5).</summary>
public static class GridSize
{
    // (maks. szerokość, maks. wysokość, liczba kolumn = wierszy) – progi włącznie.
    private static readonly (int W, int H, int Size)[] Tiers =
    {
        (1920, 1080, 5),
        (2560, 1440, 6),
        (3840, 2160, 8),
    };

    public static int For(int width, int height)
    {
        foreach (var t in Tiers)
            if (width <= t.W && height <= t.H) return t.Size;
        return Tiers[^1].Size; // powyżej 3840×2160 – największa siatka
    }

    /// <summary>Komunikat o przepełnieniu albo null, gdy wszystkie nody się mieszczą.</summary>
    public static string? OverflowMessage(int nodes, int cells) =>
        nodes <= cells ? null
            : $"Liczba nodów ({nodes}) przekracza liczbę pól siatki ({cells}). Nie wyświetlono {nodes - cells} nodów.";
}
