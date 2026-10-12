using System.Runtime.InteropServices;
using MapaOkien.Backend.Native;

namespace MapaOkien.Backend.Services;

/// <summary>
/// Wysuwanie okna na pierwszy plan. Windows blokuje SetForegroundWindow procesom, które nie są
/// na pierwszym planie (foreground lock). Kolejność metod z testu technicznego: zwykła (8/9 prób),
/// potem „trik z Alt”, na końcu AttachThreadInput.
/// </summary>
public static class WindowSwitcher
{
    private enum Method { Plain, AltKey, AttachInput }

    /// <summary>Zwraca true, jeśli okno jest na pierwszym planie.</summary>
    public static bool BringToFront(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd)) return false;
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

        foreach (var m in Enum.GetValues<Method>())
        {
            Try(m, hwnd);
            for (int i = 0; i < 6; i++)
            {
                if (Win32.GetForegroundWindow() == hwnd)
                {
                    if (m != Method.Plain) Log.Info($"Przełączenie metodą {m}");
                    return true;
                }
                Thread.Sleep(25);
            }
        }
        Log.Error($"Nie udało się wysunąć okna: {WindowEnumerator.GetTitle(hwnd)}");
        return false;
    }

    private static void Try(Method m, IntPtr hwnd)
    {
        switch (m)
        {
            case Method.Plain:
                Win32.SetForegroundWindow(hwnd);
                break;

            case Method.AltKey:
                // Syntetyczne naciśnięcie Alt zdejmuje blokadę pierwszego planu dla bieżącego procesu.
                var inputs = new[]
                {
                    new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new() { ki = new() { wVk = Win32.VK_MENU } } },
                    new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new() { ki = new() { wVk = Win32.VK_MENU, dwFlags = Win32.KEYEVENTF_KEYUP } } },
                };
                Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
                Win32.SetForegroundWindow(hwnd);
                break;

            case Method.AttachInput:
                var fg = Win32.GetForegroundWindow();
                uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
                uint me = Win32.GetCurrentThreadId();
                bool attached = fgThread != me && Win32.AttachThreadInput(me, fgThread, true);
                try
                {
                    Win32.BringWindowToTop(hwnd);
                    Win32.SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) Win32.AttachThreadInput(me, fgThread, false);
                }
                break;
        }
    }
}
