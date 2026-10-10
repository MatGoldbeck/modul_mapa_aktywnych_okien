using System.Runtime.InteropServices;

namespace SpikeTest;

/// <summary>
/// Osobny wątek z pętlą komunikatów Windows. Obsługuje:
///  - globalny hook myszy (WH_MOUSE_LL) – gest Ctrl+Shift + ruch ≥ 50 px bez wciśniętego przycisku,
///  - SetWinEventHook – pojawianie się i znikanie okien (zdarzeniowo, bez pollingu).
/// Oba mechanizmy wymagają pętli komunikatów na wątku, który je zarejestrował.
/// </summary>
internal sealed class HookThread
{
    public const int GestureDistancePx = 50;

    // Delegaty muszą żyć tak długo jak hooki (inaczej GC je zbierze).
    private readonly Native.LowLevelProc _mouseProc;
    private readonly Native.WinEventDelegate _winEventProc;

    private (int X, int Y)? _anchor;
    private bool _firedInThisHold;
    private readonly Dictionary<IntPtr, WindowInfo> _known = new();

    public bool LogWindowEvents { get; set; } = true;
    public int GestureCount { get; private set; }

    public HookThread()
    {
        _mouseProc = OnMouse;
        _winEventProc = OnWinEvent;
    }

    public void Start()
    {
        var t = new Thread(Run) { IsBackground = true, Name = "hooks" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private void Run()
    {
        foreach (var w in WindowEnumerator.List()) _known[w.Hwnd] = w;

        var mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, Native.GetModuleHandle(null), 0);
        Log.Line(mouseHook == IntPtr.Zero
            ? $"[HOOK] Nie udało się założyć hooka myszy (błąd {Marshal.GetLastWin32Error()})"
            : "[HOOK] Hook myszy aktywny – gest: trzymaj Ctrl+Shift i przesuń mysz o ≥ 50 px");

        Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_HIDE, IntPtr.Zero,
            _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Native.SetWinEventHook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero,
            _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Native.SetWinEventHook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero,
            _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    // --- Gest -------------------------------------------------------------

    private static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Hook musi być szybki – Windows usuwa hooki, które odpowiadają zbyt wolno.
        if (nCode >= 0 && wParam == Native.WM_MOUSEMOVE)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            bool keys = Down(Native.VK_CONTROL) && Down(Native.VK_SHIFT);
            bool buttons = Down(Native.VK_LBUTTON) || Down(Native.VK_RBUTTON) || Down(Native.VK_MBUTTON)
                           || Down(Native.VK_XBUTTON1) || Down(Native.VK_XBUTTON2);

            if (!keys || buttons)
            {
                _anchor = null;
                _firedInThisHold = false;
            }
            else if (_anchor is null)
            {
                _anchor = (data.x, data.y);
            }
            else if (!_firedInThisHold)
            {
                double dx = data.x - _anchor.Value.X, dy = data.y - _anchor.Value.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= GestureDistancePx)
                {
                    _firedInThisHold = true; // jedno wywołanie na jedno przytrzymanie Ctrl+Shift
                    GestureCount++;
                    var fg = Native.GetForegroundWindow();
                    Log.Line($"[GEST #{GestureCount}] wykryty w ({data.x},{data.y}), dystans {dist:0} px, " +
                             $"okno na pierwszym planie: {WindowEnumerator.GetTitle(fg)}");
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // --- Zdarzenia okien --------------------------------------------------

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != Native.OBJID_WINDOW || idChild != 0) return;

        var now = ev == Native.EVENT_OBJECT_DESTROY ? null : WindowEnumerator.TryDescribe(hwnd);
        bool wasKnown = _known.TryGetValue(hwnd, out var before);

        if (now != null && !wasKnown)
        {
            _known[hwnd] = now;
            if (LogWindowEvents)
                Log.Line($"[+ OKNO] [{now.ProcessName}] {now.Title}  (krawędź: {now.Edge})");
            if (WindowEnumerator.OtherBrowsers.Contains(now.ProcessName))
                Program.WarnOtherBrowserOnce(now.ProcessName);
        }
        else if (now == null && wasKnown)
        {
            _known.Remove(hwnd);
            if (LogWindowEvents) Log.Line($"[- OKNO] [{before!.ProcessName}] {before.Title}");
        }
        else if (now != null && wasKnown && now.Title != before!.Title)
        {
            _known[hwnd] = now;
            // Przeglądarki zmieniają tytuł przy każdej zmianie karty – karty raportuje rozszerzenie.
            if (LogWindowEvents && !WindowEnumerator.SupportedBrowsers.Contains(now.ProcessName))
                Log.Line($"[~ TYTUŁ] [{now.ProcessName}] „{before.Title}” → „{now.Title}”");
        }
    }
}
