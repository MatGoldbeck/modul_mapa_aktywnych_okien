using System.Text.Encodings.Web;
using System.Text.Json;

namespace MapaOkien.Backend.Services;

/// <summary>Ścieżki w %APPDATA%\MapaAktywnychOkien (specyfikacja pkt 9).</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MapaAktywnychOkien");

    public static string Map => Path.Combine(Root, "map.json");
    public static string Config => Path.Combine(Root, "config.json");
    public static string Thumbs => Path.Combine(Root, "thumbs");
}

public sealed class WindowPosition
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Zawartość map.json: pola nodów (także zamkniętych okien) i pozycja okna siatki.</summary>
public sealed class MapFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, SavedCell> Cells { get; set; } = new();
    public WindowPosition? Window { get; set; }
}

/// <summary>Zawartość config.json – edytowana ręcznie (bez okna ustawień w prototypie).</summary>
public sealed class ConfigFile
{
    /// <summary>Nazwy procesów (bez .exe), których okna nigdy nie trafiają na mapę.</summary>
    public List<string> ExcludedProcesses { get; set; } = new() { "1Password", "KeePass", "KeePassXC", "Bitwarden" };

    public int Port { get; set; } = 5892;
}

public static class Storage
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Polskie znaki w tytułach okien zapisujemy czytelnie, nie jako ą.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T LoadOrCreate<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T();
        }
        catch (Exception ex)
        {
            // Uszkodzony plik nie może blokować startu – zachowujemy kopię i zaczynamy od nowa.
            Log.Error($"Nie można odczytać {path}: {ex.Message} – kopia w {path}.bad");
            File.Copy(path, path + ".bad", overwrite: true);
        }
        var fresh = new T();
        Save(path, fresh);
        return fresh;
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
