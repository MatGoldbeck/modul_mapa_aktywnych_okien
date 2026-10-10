using System.Text;

namespace SpikeTest;

/// <summary>Wypisuje na konsolę i jednocześnie dopisuje do raportu (raport.txt).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    public static string OutputDir { get; private set; } = "";

    public static void Init(string dir)
    {
        OutputDir = dir;
        Directory.CreateDirectory(dir);
        _file = new StreamWriter(Path.Combine(dir, "raport.txt"), append: true, new UTF8Encoding(false)) { AutoFlush = true };
        _file.WriteLine();
        _file.WriteLine($"===== Sesja testu: {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
    }

    public static void Line(string text = "")
    {
        lock (Gate)
        {
            Console.WriteLine(text);
            _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
        }
    }
}
