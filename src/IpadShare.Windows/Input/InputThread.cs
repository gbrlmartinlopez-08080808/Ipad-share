using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IpadShare.App.App;
using IpadShare.Core.Touch;
using static IpadShare.App.Native.NativeMethods;

namespace IpadShare.App.Input;

internal readonly record struct KeyHookEvent(uint Vk, uint ScanCode, bool Extended, bool Up, bool Injected, UIntPtr ExtraInfo);
internal readonly record struct MouseHookEvent(int Message, int X, int Y, uint MouseData, bool Injected, UIntPtr ExtraInfo);
internal readonly record struct RawMouseEvent(IntPtr Device, int Dx, int Dy, bool Absolute, ushort ButtonFlags, short ButtonData, double Time);

/// <summary>
/// The real-time input thread. Owns the low-level keyboard/mouse hooks, Raw Input (mice and
/// precision touchpads), and the timers for gesture momentum and watchdogs. Everything that
/// touches input runs here, single-threaded, so the hot path has no locks. Hook callbacks
/// must stay fast: Windows silently removes hooks that time out.
/// </summary>
internal sealed class InputThread : IDisposable
{
    public const uint OurInjectionSignature = 0x1FAD5EA0u;  // marks events we inject ourselves (dwExtraInfo)
    private const uint WM_RUN_POSTED = WM_APP + 1;

    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private IntPtr _kbHook, _mouseHook;
    private HookProc? _kbProc, _mouseProc;
    private WndProc? _wndProc;
    private IntPtr _rawBuffer;
    private uint _rawBufferSize;
    private volatile bool _stop;
    private bool _timerPeriod;
    private readonly List<PrecisionTouchpad> _touchpads = new();
    private volatile PrecisionTouchpad[] _touchpadSnapshot = Array.Empty<PrecisionTouchpad>();

    public Func<KeyHookEvent, bool>? OnKey;
    public Func<MouseHookEvent, bool>? OnMouse;
    public Action<RawMouseEvent>? OnRawMouse;
    public Action<PrecisionTouchpad, TouchFrame>? OnTouch;
    /// <summary>Called on every loop iteration; return seconds until the next tick is needed.</summary>
    public Func<double, double>? OnTick;
    public Action? OnDevicesChanged;

    public long HookEvents;
    public long RawEvents;
    public bool HooksInstalled => _kbHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;
    /// <summary>Immutable snapshot, safe to read from any thread.</summary>
    public IReadOnlyList<PrecisionTouchpad> Touchpads => _touchpadSnapshot;
    public string? TouchpadError { get; private set; }
    public IntPtr Window => _hwnd;

    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>IPADSHARE_TRACE_INPUT=1 logs the first input events (troubleshooting).</summary>
    private static readonly bool s_trace = Environment.GetEnvironmentVariable("IPADSHARE_TRACE_INPUT") == "1";
    private int _traced;
    private void Trace(string what)
    {
        if (s_trace && _traced++ < 300) Log.Info("TRACE " + what);
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "iPadShare input", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Input thread did not start");
    }

    /// <summary>Run an action on the input thread.</summary>
    public void Post(Action a)
    {
        _posted.Enqueue(a);
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_RUN_POSTED, IntPtr.Zero, IntPtr.Zero);
    }

    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread?.ManagedThreadId;

    /// <summary>Request ~1 ms timer resolution while precise ticks are needed (momentum, calibration).</summary>
    public void SetHighResolutionTimer(bool on)
    {
        if (on == _timerPeriod) return;
        _timerPeriod = on;
        if (on) timeBeginPeriod(1); else timeEndPeriod(1);
    }

    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            CreateMessageWindow();
            InstallHooks();
            RegisterRawInput();
            EnumerateTouchpads();
        }
        catch (Exception ex)
        {
            Log.Error("Input thread init failed", ex);
        }
        finally
        {
            _ready.Set();
        }

        while (!_stop)
        {
            double wait = 0.25;
            try { wait = OnTick?.Invoke(Now) ?? 0.25; }
            catch (Exception ex) { Log.Error("Tick failed", ex); }

            uint ms = wait <= 0 ? 0 : wait >= 0.5 ? 500u : (uint)Math.Max(1, wait * 1000);
            MsgWaitForMultipleObjectsEx(0, null, ms, QS_ALLINPUT, MWMO_INPUTAVAILABLE);

            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) { _stop = true; break; }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            RunPosted();
        }

        Cleanup();
    }

    private void RunPosted()
    {
        while (_posted.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) { Log.Error("Posted action failed", ex); }
        }
    }

    // ---------------- window ----------------
    private void CreateMessageWindow()
    {
        _wndProc = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "iPadShareInputSink",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, wc.lpszClassName, "iPadShare input", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_INPUT:
                    HandleRawInput(lParam);
                    return DefWindowProc(hWnd, msg, wParam, lParam); // lets Windows clean up the input
                case WM_INPUT_DEVICE_CHANGE:
                    EnumerateTouchpads();
                    OnDevicesChanged?.Invoke();
                    return IntPtr.Zero;
                case WM_RUN_POSTED:
                    RunPosted();
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"WndProc 0x{msg:X}", ex);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ---------------- hooks ----------------
    private void InstallHooks()
    {
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
        IntPtr mod = GetModuleHandle(null);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
        if (_kbHook == IntPtr.Zero) Log.Warn($"Keyboard hook failed ({Marshal.GetLastWin32Error()})");
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
        if (_mouseHook == IntPtr.Zero) Log.Warn($"Mouse hook failed ({Marshal.GetLastWin32Error()})");
    }

    /// <summary>Re-arm the hooks (Windows removes hooks that were too slow once).</summary>
    public void ReinstallHooks()
    {
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _kbHook = _mouseHook = IntPtr.Zero;
        InstallHooks();
        Log.Info("Hooks reinstalled");
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                HookEvents++;
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int m = (int)wParam;
                bool up = m == WM_KEYUP || m == WM_SYSKEYUP;
                var ev = new KeyHookEvent(k.vkCode, k.scanCode, (k.flags & LLKHF_EXTENDED) != 0, up,
                                          (k.flags & LLKHF_INJECTED) != 0, k.dwExtraInfo);
                Trace($"key {ev}");
                if (OnKey?.Invoke(ev) == true) return new IntPtr(1);
            }
            catch (Exception ex) { Log.Error("Keyboard hook", ex); }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                HookEvents++;
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var ev = new MouseHookEvent((int)wParam, m.pt.X, m.pt.Y, m.mouseData, (m.flags & LLMHF_INJECTED) != 0, m.dwExtraInfo);
                Trace($"mouse {ev}");
                if (OnMouse?.Invoke(ev) == true) return new IntPtr(1);
            }
            catch (Exception ex) { Log.Error("Mouse hook", ex); }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // ---------------- raw input ----------------
    private void RegisterRawInput()
    {
        var devs = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _hwnd },
        };
        if (!RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            int err = Marshal.GetLastWin32Error();
            Log.Warn($"RegisterRawInputDevices failed ({err}); trying mouse only");
            TouchpadError = $"Raw input registration failed ({err})";
            RegisterRawInputDevices(new[] { devs[0] }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
    }

    private void EnumerateTouchpads()
    {
        var old = _touchpads.ToArray();
        _touchpads.Clear();
        _touchpadSnapshot = Array.Empty<PrecisionTouchpad>();
        foreach (var t in old) t.Dispose();
        try
        {
            _touchpads.AddRange(PrecisionTouchpad.Enumerate(m => Log.Info(m)));
            foreach (var t in _touchpads) Log.Info("Touchpad: " + t.Describe());
            if (_touchpads.Count == 0) Log.Info("No Precision Touchpad found");
            _touchpadSnapshot = _touchpads.ToArray();
        }
        catch (Exception ex)
        {
            TouchpadError = ex.Message;
            Log.Error("Touchpad enumeration", ex);
        }
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        if (size > _rawBufferSize)
        {
            if (_rawBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_rawBuffer);
            _rawBufferSize = Math.Max(size, 1024);
            _rawBuffer = Marshal.AllocHGlobal((int)_rawBufferSize);
        }
        uint got = size;
        if (GetRawInputData(hRawInput, RID_INPUT, _rawBuffer, ref got, headerSize) != size) return;
        RawEvents++;
        double now = Now;

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(_rawBuffer);
        IntPtr body = _rawBuffer + (int)headerSize;

        if (header.dwType == RIM_TYPEMOUSE)
        {
            // RAWMOUSE: usFlags(2) pad(2) usButtonFlags(2) usButtonData(2) ulRawButtons(4) lLastX(4) lLastY(4) ulExtraInformation(4)
            ushort flags = (ushort)Marshal.ReadInt16(body, 0);
            ushort buttonFlags = (ushort)Marshal.ReadInt16(body, 4);
            short buttonData = Marshal.ReadInt16(body, 6);
            int dx = Marshal.ReadInt32(body, 12);
            int dy = Marshal.ReadInt32(body, 16);
            var rm = new RawMouseEvent(header.hDevice, dx, dy, (flags & MOUSE_MOVE_ABSOLUTE) != 0, buttonFlags, buttonData, now);
            Trace($"raw {rm}");
            OnRawMouse?.Invoke(rm);
        }
        else if (header.dwType == RIM_TYPEHID)
        {
            PrecisionTouchpad? pad = null;
            foreach (var t in _touchpads) if (t.Handle == header.hDevice) { pad = t; break; }
            if (pad == null) return;
            uint reportSize = (uint)Marshal.ReadInt32(body, 0);
            uint count = (uint)Marshal.ReadInt32(body, 4);
            IntPtr data = body + 8;
            for (uint i = 0; i < count; i++)
            {
                var frame = pad.Parse(data + (int)(i * reportSize), reportSize, now);
                if (frame != null) OnTouch?.Invoke(pad, frame);
            }
        }
    }

    // ---------------- shutdown ----------------
    public void Dispose()
    {
        if (_thread == null) return;
        _stop = true;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_RUN_POSTED, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Cleanup()
    {
        try
        {
            if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _kbHook = _mouseHook = IntPtr.Zero;
            SetHighResolutionTimer(false);
            _touchpadSnapshot = Array.Empty<PrecisionTouchpad>();
            foreach (var t in _touchpads) t.Dispose();
            _touchpads.Clear();
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            if (_rawBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_rawBuffer);
        }
        catch (Exception ex) { Log.Error("Input cleanup", ex); }
    }
}
