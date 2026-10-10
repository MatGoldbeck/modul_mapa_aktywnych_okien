using System.Runtime.InteropServices;

namespace SpikeTest;

/// <summary>
/// Przełączanie do okna. Windows blokuje SetForegroundWindow dla procesów, które nie są
/// na pierwszym planie (foreground lock), a w docelowej aplikacji przełączać będzie
/// backend .NET, gdy na pierwszym planie jest okno Electrona. Dlatego testujemy trzy metody.
/// </summary>
internal static class WindowSwitcher
{
    public enum Method { Plain, AltKey, AttachInput }

    public static void SwitchTo(WindowInfo w)
    {
        var desktopBefore = VirtualDesktops.IsOnCurrent(w.Hwnd);
        Log.Line($"  Cel: [{w.ProcessName}] {w.Title}");
        Log.Line($"  Przed: zminimalizowane={w.Minimized}, na bieżącym pulpicie={Fmt(desktopBefore)}");

        if (Native.IsIconic(w.Hwnd)) Native.ShowWindow(w.Hwnd, Native.SW_RESTORE);

        foreach (var m in Enum.GetValues<Method>())
        {
            bool api = Try(m, w.Hwnd);
            Thread.Sleep(300);
            bool foreground = Native.GetForegroundWindow() == w.Hwnd;
            Log.Line($"  Metoda {m,-12} SetForegroundWindow={api,-5} okno na pierwszym planie={foreground}");
            if (foreground) break;
        }

        var desktopAfter = VirtualDesktops.IsOnCurrent(w.Hwnd);
        Log.Line($"  Po: zminimalizowane={Native.IsIconic(w.Hwnd)}, na bieżącym pulpicie={Fmt(desktopAfter)}"
                 + (desktopBefore == false && desktopAfter == true ? "  → pulpit wirtualny PRZEŁĄCZONY" : ""));
    }

    private static bool Try(Method m, IntPtr hwnd)
    {
        switch (m)
        {
            case Method.Plain:
                return Native.SetForegroundWindow(hwnd);

            case Method.AltKey:
                // Syntetyczne naciśnięcie Alt zdejmuje blokadę pierwszego planu dla bieżącego procesu.
                var inputs = new[]
                {
                    new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new() { ki = new() { wVk = Native.VK_MENU } } },
                    new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new() { ki = new() { wVk = Native.VK_MENU, dwFlags = Native.KEYEVENTF_KEYUP } } },
                };
                Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
                return Native.SetForegroundWindow(hwnd);

            case Method.AttachInput:
                var fg = Native.GetForegroundWindow();
                uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
                uint me = Native.GetCurrentThreadId();
                bool attached = fgThread != me && Native.AttachThreadInput(me, fgThread, true);
                try
                {
                    Native.BringWindowToTop(hwnd);
                    return Native.SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) Native.AttachThreadInput(me, fgThread, false);
                }
        }
        return false;
    }

    private static string Fmt(bool? b) => b switch { true => "tak", false => "nie", null => "?" };
}
