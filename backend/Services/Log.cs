using System.Text;

namespace MapaOkien.Backend.Services;

/// <summary>
/// Log do pliku %APPDATA%\MapaAktywnychOkien\backend.log (backend nie ma okna konsoli).
/// Plik jest przycinany przy starcie, żeby nie rósł bez końca.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    public static void Init(string dir)
    {
        var path = Path.Combine(dir, "backend.log");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Delete(path);
        }
        catch (IOException) { /* plik otwarty przez działającą instancję */ }
        // FileShare.ReadWrite: druga instancja (kończąca się od razu) i podgląd logu nie blokują pliku.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _file.WriteLine();
    }

    public static void Info(string text) => Write("INFO ", text);
    public static void Error(string text) => Write("BŁĄD ", text);

    private static void Write(string level, string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level}{text}";
        lock (Gate)
        {
            _file?.WriteLine(line);
            Console.WriteLine(line); // widoczne przy uruchomieniu z terminala (dotnet run)
        }
    }
}
