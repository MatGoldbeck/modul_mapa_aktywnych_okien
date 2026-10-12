using System.Diagnostics;
using System.Net;
using System.Text;
using MapaOkien.Backend.Native;
using MapaOkien.Backend.Services;

namespace MapaOkien.Backend;

/// <summary>
/// Backend modułu „mapa aktywnych okien”. Uruchamiany przez frontend (Electron) z argumentem
/// --parent-pid; kończy się razem z nim. Można go też uruchomić ręcznie (dotnet run) do diagnozy.
/// </summary>
public static class Program
{
    private static readonly ManualResetEventSlim ExitRequested = new();

    public static void RequestExit() => ExitRequested.Set();

    public static int Main(string[] args)
    {
        // Świadomość DPI jako pierwsza: rozdzielczość fizyczna monitora (siatka) i prawdziwe rozmiary
        // okien (miniatury). Bez niej 4K przy 150% byłoby widziane jako 2560×1440 (sprawdzone w spike).
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(AppPaths.Root);
        Log.Init(AppPaths.Root);

        // Jedna instancja: drugi backend (np. ręcznie uruchomiony + z frontendu) od razu się kończy,
        // a frontend łączy się z tym, który już działa.
        using var mutex = new Mutex(true, @"Local\MapaAktywnychOkien.Backend", out bool first);
        if (!first)
        {
            Log.Info("Backend już działa – kończę tę instancję.");
            return 0;
        }

        var parentPid = ParseParentPid(args);
        if (parentPid != null) WindowEnumerator.OwnProcessIds[(uint)parentPid.Value] = 0;

        var config = Storage.LoadOrCreate<ConfigFile>(AppPaths.Config);
        var map = Storage.LoadOrCreate<MapFile>(AppPaths.Map);
        var (width, height) = PrimaryScreenResolution();

        var ws = new WsServer(config.Port);
        try
        {
            ws.Start();
        }
        catch (HttpListenerException ex)
        {
            Log.Error($"Port {config.Port} jest zajęty ({ex.Message}). Zmień „port” w {AppPaths.Config} " +
                      "(i adres w rozszerzeniu) albo zamknij program, który go używa.");
            return 2;
        }

        var nodes = new NodeManager(ws, new Thumbnails(AppPaths.Thumbs), map, config, width, height);
        var watcher = new WindowWatcher();
        nodes.Attach(watcher);
        watcher.Start();

        Log.Info($"Backend uruchomiony. Monitor główny {width}×{height} → siatka {nodes.GridSizeValue}×{nodes.GridSizeValue}. " +
                 $"Dane: {AppPaths.Root}");

        using var cleanup = new System.Threading.Timer(_ => nodes.CleanupOrphanThumbnails(), null,
            TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        if (parentPid != null) WatchParent(parentPid.Value);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            RequestExit();
        };

        ExitRequested.Wait();

        nodes.SaveMap();
        watcher.Stop();
        ws.Stop();
        Log.Info("Backend zakończony.");
        return 0;
    }

    private static int? ParseParentPid(string[] args)
    {
        var i = Array.IndexOf(args, "--parent-pid");
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid) ? pid : null;
    }

    /// <summary>Gdy frontend zniknie (zamknięty, awaria), backend kończy się razem z nim.</summary>
    private static void WatchParent(int pid)
    {
        try
        {
            var parent = Process.GetProcessById(pid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) =>
            {
                Log.Info("Frontend zakończony – kończę backend.");
                RequestExit();
            };
            if (parent.HasExited) RequestExit();
        }
        catch (ArgumentException)
        {
            Log.Info($"Proces nadrzędny {pid} nie istnieje – kończę backend.");
            RequestExit();
        }
    }

    /// <summary>
    /// Rozdzielczość fizyczna monitora głównego, odczytywana raz przy starcie (specyfikacja pkt 5).
    /// Prawidłowa dzięki świadomości DPI ustawionej w Main.
    /// </summary>
    private static (int Width, int Height) PrimaryScreenResolution()
    {
        var hdc = Win32.GetDC(IntPtr.Zero);
        try
        {
            return (Win32.GetDeviceCaps(hdc, Win32.DESKTOPHORZRES), Win32.GetDeviceCaps(hdc, Win32.DESKTOPVERTRES));
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, hdc);
        }
    }
}
