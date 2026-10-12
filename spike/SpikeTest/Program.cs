using System.Text;

namespace SpikeTest;

/// <summary>
/// Test techniczny modułu „mapa aktywnych okien” (docs/specyfikacja.md, pkt 11).
/// Interaktywna konsola; wszystko, co widać na ekranie, trafia też do wyniki\raport.txt.
/// </summary>
internal static class Program
{
    private static readonly HashSet<string> WarnedBrowsers = new(StringComparer.OrdinalIgnoreCase);
    private static List<WindowInfo> _lastList = new();
    private static readonly HookThread Hooks = new();
    private static readonly ExtensionServer Extension = new();

    public static void WarnOtherBrowserOnce(string processName)
    {
        lock (WarnedBrowsers)
        {
            // Jeden komunikat na sesję – niezależnie od tego, która z nieobsługiwanych przeglądarek.
            if (WarnedBrowsers.Count > 0) return;
            WarnedBrowsers.Add(processName);
        }
        Log.Line($"[KOMUNIKAT] W modul_mapa_aktywnych_okien obsługiwane są tylko przeglądarki Edge, Chrome. (wykryto: {processName})");
    }

    private static async Task Main()
    {
        Grid.Init(); // jako pierwsze – ustawia świadomość DPI procesu
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Log.Init(Path.Combine(Environment.CurrentDirectory, "wyniki"));

        Log.Line("Test techniczny – modul_mapa_aktywnych_okien");
        Log.Line("To jest test konsolowy: okno siatki (Grid) NIE pojawia się – powstanie w prototypie właściwym.");
        Log.Line("Gest jest tu tylko wykrywany i zapisywany jako [GEST #n]. Polecenia wpisuj poniżej (np. l + Enter).");
        Log.Line($"System: {Environment.OSVersion.VersionString}");
        Log.Line($"Pulpity wirtualne (IVirtualDesktopManager): {(VirtualDesktops.Available ? "dostępne" : "NIEDOSTĘPNE")}");
        Log.Line(Thumbnails.WgcStatus());
        Log.Line($"Monitor główny: {Grid.Width}×{Grid.Height} → siatka {Grid.Size}×{Grid.Size} ({Grid.Cells} pól)");
        Log.Line($"Wyniki: {Log.OutputDir}");
        Log.Line();

        Hooks.Start();
        Extension.Start();
        await Task.Delay(300);
        PrintHelp();

        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line == null) break;
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var arg = parts.Length > 1 ? parts[1] : null;
            Log.Line($"> {line.Trim()}");

            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "l": ListWindows(); break;
                    case "m": await CaptureAsync(arg); break;
                    case "p": SwitchWindow(arg, delay: false); break;
                    case "pp": SwitchWindow(arg, delay: true); break;
                    case "k": await TabsAsync(arg); break;
                    case "z":
                        Hooks.LogWindowEvents = Extension.LogTabEvents = !Hooks.LogWindowEvents;
                        Log.Line($"Zdarzenia okien/kart: {(Hooks.LogWindowEvents ? "włączone" : "wyłączone")}");
                        break;
                    case "?": PrintHelp(); break;
                    case "q": Log.Line("Koniec testu."); return;
                    default: Log.Line("Nieznane polecenie. ? = pomoc"); break;
                }
            }
            catch (Exception ex)
            {
                Log.Line($"BŁĄD: {ex}");
            }
        }
    }

    private static void PrintHelp()
    {
        Log.Line("""
            Polecenia:
              l          lista okien (jak Alt+Tab, wszystkie monitory i pulpity)
              m          miniatury WSZYSTKICH okien z listy (PrintWindow + WGC)
              m <nr>     miniatury jednego okna
              p <nr>     przełącz do okna
              pp <nr>    przełącz po 3 s (w tym czasie kliknij inne okno – test blokady pierwszego planu)
              k          lista kart z rozszerzenia
              k <nr>     aktywuj kartę
              z          włącz/wyłącz wypisywanie zdarzeń okien i kart
              q          koniec (wyłącza program; można też Ctrl+C albo zamknąć okno konsoli)
            W tle cały czas działa wykrywanie gestu Ctrl+Shift + ruch myszy ≥ 50 px.
            """);
    }

    private static List<WindowInfo> RefreshList()
    {
        _lastList = WindowEnumerator.List();
        return _lastList;
    }

    private static void ListWindows()
    {
        var list = RefreshList();
        var monitors = list.Select(w => w.Monitor).Distinct().ToList();
        Log.Line($"{"nr",3} {"proces",-18} {"krawędź",-7} {"min",-3} {"pulpit",-8} {"mon",3}  tytuł");
        for (int i = 0; i < list.Count; i++)
        {
            var w = list[i];
            var desktop = w.OnCurrentDesktop switch { true => "bieżący", false => "INNY", null => "?" };
            Log.Line($"{i + 1,3} {Trim(w.ProcessName, 18),-18} {w.Edge,-7} {(w.Minimized ? "tak" : ""),-3} {desktop,-8} " +
                     $"{monitors.IndexOf(w.Monitor) + 1,3}  {Trim(w.Title, 80)}");
            if (WindowEnumerator.OtherBrowsers.Contains(w.ProcessName)) WarnOtherBrowserOnce(w.ProcessName);
        }

        // Nody okien (bez przeglądarek – ich nodami są karty) + unikalne adresy kart.
        int nodes = list.Count(w => !WindowEnumerator.SupportedBrowsers.Contains(w.ProcessName))
                    + Extension.Tabs.Select(t => t.Url).Distinct().Count();
        Log.Line($"Siatka {Grid.Size}×{Grid.Size}: nodów {nodes} / pól {Grid.Cells}" +
                 (Extension.Tabs.Count == 0 ? " (bez kart – rozszerzenie niepołączone)" : ""));
        if (Grid.OverflowMessage(nodes) is { } overflow) Log.Line($"[KOMUNIKAT] {overflow}");

        var duplicates = list.GroupBy(w => w.Key).Where(g => g.Count() > 1).ToList();
        Log.Line($"Razem: {list.Count} okien, monitorów: {monitors.Count}, " +
                 $"pulpitów wirtualnych: {list.Select(w => w.DesktopId).Where(d => d != null).Distinct().Count()}, " +
                 $"zminimalizowanych: {list.Count(w => w.Minimized)}, na innych pulpitach: {list.Count(w => w.OnCurrentDesktop == false)}");
        Log.Line(duplicates.Count == 0
            ? "Klucz proces+tytuł: wszystkie unikalne."
            : $"Klucz proces+tytuł: {duplicates.Count} zduplikowanych kluczy – " +
              string.Join("; ", duplicates.Select(g => $"{g.Count()}× {Trim(g.Key, 60)}")));
    }

    private static async Task CaptureAsync(string? arg)
    {
        if (_lastList.Count == 0) RefreshList();
        var targets = arg == null ? _lastList.Select((w, i) => (w, i)).ToList()
                                  : PickWindow(arg) is { } p ? new List<(WindowInfo, int)> { p } : new();
        if (targets.Count == 0) return;

        var dir = Path.Combine(Log.OutputDir, "miniatury");
        Directory.CreateDirectory(dir);
        var results = new List<(WindowInfo W, CaptureResult R)>();

        foreach (var (w, i) in targets)
        {
            var stem = Path.Combine(dir, $"{i + 1:00}_{Safe(w.ProcessName)}");
            var category = Category(w);
            Log.Line($"[{i + 1}] [{w.ProcessName}] {Trim(w.Title, 60)}  ({category})");
            foreach (var r in new[]
                     {
                         Thumbnails.CapturePrintWindow(w, stem + "_printwindow.png"),
                         await Thumbnails.CaptureWgcAsync(w, stem + "_wgc.png"),
                     })
            {
                Log.Line($"     {r.Method,-11} {(r.Ok ? "OK " : "BŁĄD")} {r.Ms,5} ms  {r.Note}");
                results.Add((w, r));
            }
        }

        if (targets.Count > 1)
        {
            Log.Line("Podsumowanie (udane z treścią / wszystkie, średni czas):");
            foreach (var g in results.GroupBy(x => (Category(x.W), x.R.Method)).OrderBy(g => g.Key))
            {
                int good = g.Count(x => x.R.Ok && x.R.Note.Contains("z treścią"));
                Log.Line($"  {g.Key.Item1,-22} {g.Key.Method,-11} {good}/{g.Count()}  śr. {g.Average(x => x.R.Ms):0} ms");
            }
        }
        Log.Line($"Pliki: {dir}");
    }

    private static string Category(WindowInfo w) =>
        w.OnCurrentDesktop == false ? "inny pulpit" : w.Minimized ? "zminimalizowane" : "zwykłe";

    private static void SwitchWindow(string? arg, bool delay)
    {
        if (PickWindow(arg) is not { } p) return;
        if (delay)
        {
            Log.Line("  Za 3 s przełączenie – kliknij teraz inne okno…");
            Thread.Sleep(3000);
            Log.Line($"  Pierwszy plan przed przełączeniem: {WindowEnumerator.GetTitle(Native.GetForegroundWindow())}");
        }
        WindowSwitcher.SwitchTo(p.W);
    }

    private static async Task TabsAsync(string? arg)
    {
        if (arg == null)
        {
            await Extension.RequestTabsAsync();
            await Task.Delay(500);
        }
        var tabs = Extension.Tabs;
        if (tabs.Count == 0)
        {
            Log.Line("Brak kart – czy rozszerzenie jest załadowane i połączone? (patrz spike\\README.md)");
            return;
        }
        if (arg == null)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                var t = tabs[i];
                bool dup = tabs.Count(x => x.Url == t.Url) > 1;
                Log.Line($"{i + 1,3} {t.Browser,-6} okno {t.WindowId,-6} {(t.Active ? "aktywna" : ""),-7} {(dup ? "DUPLIKAT" : ""),-8} {Trim(t.Title, 50)} — {Trim(t.Url, 70)}");
            }
            Log.Line($"Razem: {tabs.Count} kart, {tabs.Select(t => t.Url).Distinct().Count()} nodów (unikalnych adresów).");
            return;
        }
        if (!int.TryParse(arg, out var n) || n < 1 || n > tabs.Count)
        {
            Log.Line("Podaj numer z listy (k).");
            return;
        }
        await Extension.ActivateAsync(tabs[n - 1]);
    }

    private static (WindowInfo W, int I)? PickWindow(string? arg)
    {
        if (!int.TryParse(arg, out var n) || n < 1 || n > _lastList.Count)
        {
            Log.Line("Podaj numer okna z listy (najpierw: l).");
            return null;
        }
        var w = _lastList[n - 1];
        if (!Native.IsWindow(w.Hwnd))
        {
            Log.Line("To okno już nie istnieje – odśwież listę (l).");
            return null;
        }
        return (w, n - 1);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Safe(string s) => string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-'));
}
