using System.Runtime.InteropServices;
using MapaOkien.Backend.Native;

namespace MapaOkien.Backend.Services;

/// <summary>
/// Osobny wątek z pętlą komunikatów Windows. Obsługuje:
///  - globalny hook myszy (WH_MOUSE_LL) – gest Ctrl+Shift + ruch ≥ 50 px bez wciśniętego przycisku,
///  - SetWinEventHook – pojawianie się, znikanie, zmiany tytułu i minimalizację okien oraz
///    zmianę okna na pierwszym planie (zdarzeniowo, bez pollingu).
/// Oba mechanizmy wymagają pętli komunikatów na wątku, który je zarejestrował.
/// Zdarzenia są wywoływane na wątku hooków albo puli wątków – odbiorca musi być bezpieczny wątkowo.
/// </summary>
public sealed class WindowWatcher
{
    public const int GestureDistancePx = 50;

    /// <summary>Okno musi przetrwać tyle czasu, zanim stanie się nodem (odsiewa okna chwilowe).</summary>
    public const int StableMs = 800;

    // Delegaty muszą żyć tak długo jak hooki (inaczej GC je zbierze).
    private readonly Win32.LowLevelProc _mouseProc;
    private readonly Win32.WinEventDelegate _winEventProc;

    private (int X, int Y)? _anchor;
    private bool _firedInThisHold;

    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, WindowInfo> _known = new();
    // Referencje do timerów trzymamy, bo niereferencjonowany System.Threading.Timer może zostać zebrany przez GC.
    private readonly Dictionary<IntPtr, System.Threading.Timer> _pending = new();
    private uint _threadId;

    public event Action? Gesture;
    public event Action<WindowInfo>? Added;
    public event Action<WindowInfo>? Removed;
    /// <summary>Zmiana tytułu lub stanu minimalizacji: (przed, po).</summary>
    public event Action<WindowInfo, WindowInfo>? Updated;
    public event Action<IntPtr>? Foreground;

    public WindowWatcher()
    {
        _mouseProc = OnMouse;
        _winEventProc = OnWinEvent;
    }

    public IReadOnlyList<WindowInfo> Snapshot()
    {
        lock (_gate) return _known.Values.ToList();
    }

    public void Start()
    {
        var ready = new ManualResetEventSlim();
        var t = new Thread(() => Run(ready)) { IsBackground = true, Name = "hooks" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
    }

    public void Stop()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = Win32.GetCurrentThreadId();
        var initial = WindowEnumerator.List();
        lock (_gate)
            foreach (var w in initial) _known[w.Hwnd] = w;
        foreach (var w in initial) Added?.Invoke(w);

        var mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, Win32.GetModuleHandle(null), 0);
        Log.Info(mouseHook == IntPtr.Zero
            ? $"Nie udało się założyć hooka myszy (błąd {Marshal.GetLastWin32Error()}) – gest nie będzie działał"
            : "Hook myszy aktywny (gest Ctrl+Shift + ruch ≥ 50 px)");

        Hook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND);
        Hook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND);
        Hook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_HIDE);
        Hook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE);
        Hook(Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED);
        ready.Set();

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    private void Hook(uint min, uint max) =>
        Win32.SetWinEventHook(min, max, IntPtr.Zero, _winEventProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);

    // --- Gest -------------------------------------------------------------

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Hook musi być szybki – Windows usuwa hooki, które odpowiadają zbyt wolno.
        if (nCode >= 0 && wParam == Win32.WM_MOUSEMOVE)
        {
            var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            bool keys = Down(Win32.VK_CONTROL) && Down(Win32.VK_SHIFT);
            bool buttons = Down(Win32.VK_LBUTTON) || Down(Win32.VK_RBUTTON) || Down(Win32.VK_MBUTTON)
                           || Down(Win32.VK_XBUTTON1) || Down(Win32.VK_XBUTTON2);

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
                if (dx * dx + dy * dy >= GestureDistancePx * GestureDistancePx)
                {
                    _firedInThisHold = true; // jedno wywołanie na jedno przytrzymanie Ctrl+Shift
                    ThreadPool.QueueUserWorkItem(_ => Gesture?.Invoke());
                }
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // --- Zdarzenia okien --------------------------------------------------

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != Win32.OBJID_WINDOW || idChild != 0) return;

        if (ev == Win32.EVENT_SYSTEM_FOREGROUND)
        {
            Foreground?.Invoke(hwnd);
            // Okno, które właśnie wyszło na pierwszy plan, mogło dopiero się pojawić – sprawdzamy dalej.
        }

        var now = ev == Win32.EVENT_OBJECT_DESTROY ? null : WindowEnumerator.TryDescribe(hwnd);
        WindowInfo? removed = null;
        (WindowInfo Before, WindowInfo After)? updated = null;
        lock (_gate)
        {
            bool wasKnown = _known.TryGetValue(hwnd, out var before);

            if (now != null && !wasKnown)
            {
                if (!_pending.ContainsKey(hwnd))
                    _pending[hwnd] = new System.Threading.Timer(_ => ConfirmNew(hwnd), null, StableMs, Timeout.Infinite);
            }
            else if (now == null && wasKnown)
            {
                _known.Remove(hwnd);
                removed = before;
            }
            else if (now != null && wasKnown && (now.Title != before!.Title || now.Minimized != before.Minimized
                                                 || now.OnCurrentDesktop != before.OnCurrentDesktop))
            {
                _known[hwnd] = now;
                updated = (before, now);
            }
        }
        if (removed != null) Removed?.Invoke(removed);
        if (updated != null) Updated?.Invoke(updated.Value.Before, updated.Value.After);
    }

    private void ConfirmNew(IntPtr hwnd)
    {
        var now = WindowEnumerator.TryDescribe(hwnd);
        lock (_gate)
        {
            if (_pending.Remove(hwnd, out var timer)) timer.Dispose();
            if (now == null || _known.ContainsKey(hwnd)) return;
            _known[hwnd] = now;
        }
        Added?.Invoke(now);
    }
}
