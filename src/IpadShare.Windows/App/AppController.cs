using System.Text;
using Microsoft.Win32;
using IpadShare.App.Bluetooth;
using IpadShare.App.Input;
using IpadShare.Core.Handoff;
using IpadShare.Core.Hid;
using IpadShare.Core.Input;
using IpadShare.Core.Settings;
using IpadShare.Core.Touch;
using IpadShare.Core.Transport;
using MouseButtons = IpadShare.Core.Hid.MouseButtons;
using static IpadShare.App.Native.NativeMethods;

namespace IpadShare.App.App;

internal sealed record AppStatus(
    TransportState Transport, string TransportName, string? Detail, string? Peer,
    bool OnIpad, bool SharingEnabled, bool TouchpadFound, string? TouchpadName,
    bool NativeTrackpadActive, int MaxPayload, bool Calibrated);

/// <summary>
/// Orchestrates everything. Input handlers run on the <see cref="InputThread"/>; UI calls arrive on the
/// UI thread and are marshalled onto the input thread with <see cref="InputThread.Post"/>.
/// </summary>
internal sealed class AppController : IAsyncDisposable
{
    private const double TouchPushPxPerMm = 20;   // touchpad finger mm → push pixel-equivalents
    private const ushort VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_SPACE = 0x20;

    private readonly SettingsStore _store;
    private AppSettings _settings;
    private readonly InputThread _input = new();
    private readonly Overlays _overlays = new();
    private readonly TransportManager _transport;
    private readonly ReportScheduler _scheduler;
    private readonly KeyTranslator _keys;
    private readonly GestureEngine _gestures;
    private readonly NativeTouchMapper _native = new();
    private readonly IpadCursorModel _model;
    private readonly HandoffController _handoff;
    private readonly List<KeyboardReport> _keyScratch = new();
    private readonly CancellationTokenSource _cts = new();

    // ---- input-thread state ----
    private volatile bool _onIpad;
    private bool _forwardUserInput = true;            // false during calibration
    private readonly HashSet<uint> _pcKeysDown = new();
    private MouseButtons _pcButtons, _mouseButtons, _gestureButtons;
    private bool _ctrl, _alt, _hotkeySpaceDown;
    private ushort _consumerDown;
    private int _hookX, _hookY, _prevHookX, _prevHookY;
    private int _parkX, _parkY;
    private double _lastRawMouse = double.NegativeInfinity;
    private bool _rawMouseSeen, _touchSeen;      // Raw Input works on this machine
    private double _lastTouchTime = double.NegativeInfinity;
    private TouchFrame? _lastTouch;
    private readonly Dictionary<int, (double x, double y)> _prevContacts = new();
    private PointD? _pendingReturn;
    private double _lastWatchdog, _lastBattery = double.NegativeInfinity, _lastPushGlow;
    private long _hookEventsAtWatchdog;
    private double _disconnectedPush, _lastDisconnectedHint = double.NegativeInfinity;
    private ScreenLayout? _layout;
    private bool _nativeActive;

    public AppController(SettingsStore store)
    {
        _store = store;
        _settings = store.Load();
        _transport = new TransportManager(_settings.Transport, _settings.BridgePort);
        _scheduler = new ReportScheduler(_transport) { Accepting = false };
        _keys = new KeyTranslator(new KeyTranslatorOptions());
        _gestures = new GestureEngine(new GestureSettings(), new Sink(this));
        _model = new IpadCursorModel(IpadModel.IpadAirM1.LandscapePoints, GainCurve.DefaultMouse, GainCurve.DefaultTouch);
        _handoff = new HandoffController(DefaultLayout(), new HandoffSettings(), _model);
    }

    /// <summary>Raised (on any thread) when anything shown in the UI may have changed.</summary>
    public event Action? StatusChanged;
    /// <summary>Raised when the user pushes into the edge while no iPad is connected.</summary>
    public event Action? PushedWhileDisconnected;
    /// <summary>Raised (input thread) when Esc is pressed while user input is suspended (calibration).</summary>
    public event Action? CancelRequested;

    public AppSettings Settings => _settings;
    public IReadOnlyList<PrecisionTouchpad> Touchpads => _input.Touchpads;
    public TransportManager Transport => _transport;

    public AppStatus Status
    {
        get
        {
            var pad = _input.Touchpads.FirstOrDefault();
            return new AppStatus(_transport.State, _transport.Name, _transport.StatusDetail, _transport.PeerName,
                _onIpad, _settings.SharingEnabled, pad != null, pad?.DeviceName, _nativeActive,
                _transport.MaxReportPayload, _settings.Calibration.HasMouse);
        }
    }

    // ==================================================================
    // Startup / shutdown
    // ==================================================================
    public async Task StartAsync()
    {
        _input.OnKey = OnKey;
        _input.OnMouse = OnMouse;
        _input.OnRawMouse = OnRawMouse;
        _input.OnTouch = OnTouch;
        _input.OnTick = OnTick;
        _input.OnDevicesChanged = () => { ApplySettingsOnInputThread(); RaiseStatus(); };
        _input.Start();
        _input.Post(() =>
        {
            _overlays.Create();
            _overlays.SetAccent(SystemInfo.AccentColor());
            ApplySettingsOnInputThread();
        });

        _transport.StateChanged += (_, _) => _input.Post(OnTransportStateChanged);
        _transport.HostWrite += (_, w) => _input.Post(() => OnHostWrite(w));
        _scheduler.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Startup.Apply(_settings.StartWithWindows);
        await _transport.StartAsync(_cts.Token);
        RaiseStatus();
    }

    public async ValueTask DisposeAsync()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        var done = new TaskCompletionSource();
        _input.Post(() =>
        {
            try { if (_onIpad) ReturnToPc(null); _overlays.Dispose(); }
            finally { done.TrySetResult(); }
        });
        await Task.WhenAny(done.Task, Task.Delay(1000));
        await Task.Delay(100);  // let the release reports go out
        _cts.Cancel();
        await _scheduler.DisposeAsync();
        await _transport.DisposeAsync();
        _input.Dispose();
    }

    // ==================================================================
    // Settings
    // ==================================================================
    /// <summary>Apply and persist new settings (UI thread).</summary>
    public void UpdateSettings(Action<AppSettings> change)
    {
        var copy = _settings.Clone();
        change(copy);
        copy.Normalize();
        bool transportChanged = copy.Transport != _settings.Transport || copy.BridgePort != _settings.BridgePort;
        bool startupChanged = copy.StartWithWindows != _settings.StartWithWindows;
        _settings = copy;
        try { _store.Save(copy); } catch (Exception ex) { Log.Error("Saving settings", ex); }
        if (startupChanged) Startup.Apply(copy.StartWithWindows);
        if (transportChanged) _ = _transport.ReconfigureAsync(copy.Transport, copy.BridgePort);
        _input.Post(ApplySettingsOnInputThread);
        RaiseStatus();
    }

    private void ApplySettingsOnInputThread()
    {
        var s = _settings;
        var o = _keys.Options;
        o.Style = s.KeyboardStyle;
        o.SwapIsoKeys = s.SwapIsoKeys;
        o.WinTapOpensSpotlight = s.WinTapOpensSpotlight;

        var prefs = SystemInfo.ReadTouchpadPrefs();
        var g = _gestures.Settings;
        g.PointerSpeed = s.PointerSpeed;
        g.ScrollSpeed = s.ScrollSpeed;
        g.ReverseScroll = s.ReverseScroll;
        g.Momentum = s.Momentum;
        g.PinchToZoom = s.PinchToZoom;
        g.MultiFingerGestures = s.MultiFingerGestures;
        g.TapToClick = s.TapToClick ?? prefs.Taps ?? true;
        g.TwoFingerTapRightClick = s.TwoFingerTapRightClick ?? prefs.TwoFingerTap ?? true;
        g.TapAndDrag = s.TapAndDrag ?? prefs.TapAndDrag ?? true;
        g.RightClickZone = s.RightClickZone ?? prefs.RightClickZone ?? true;

        var pad = _input.Touchpads.FirstOrDefault();
        if (pad != null)
        {
            _gestures.Geometry = pad.Geometry;
            _native.Geometry = pad.Geometry;
        }

        _model.MouseCurve = s.Calibration.MouseCurve();
        _model.TouchCurve = s.Calibration.TouchCurve();
        var h = _handoff.Settings;
        h.PushThreshold = s.PushThreshold;
        h.Calibrated = s.Calibration.HasMouse;
        UpdateHandoffEnabled();

        _layout = BuildLayout();
        _handoff.SetLayout(_layout);
        PlaceGlow();
        UpdateNativeActive();
    }

    private ScreenLayout DefaultLayout() =>
        new(new RectD(0, 0, 1920, 1080), EdgeSide.Right, 100, 880, IpadModel.IpadAirM1.LandscapePoints);

    private ScreenLayout BuildLayout()
    {
        var monitors = SystemInfo.Monitors();
        var a = _settings.Arrangement;
        var mon = monitors.FirstOrDefault(m => m.Device == a.MonitorDevice) ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
        if (mon == null) return DefaultLayout();
        var model = IpadModel.ByName(a.IpadModel);
        double offset = a.Offset ?? ScreenLayout.CenteredOffset(mon.Bounds, mon.PhysicalMm, a.Side, model, a.Portrait);
        return ScreenLayout.FromArrangement(mon.Bounds, mon.PhysicalMm, a.Side, offset, model, a.Portrait);
    }

    private void PlaceGlow()
    {
        if (_layout == null) return;
        var (s, e) = _layout.CrossingSegment;
        int t = 6;
        var m = _layout.PcMonitor;
        switch (_layout.Side)
        {
            case EdgeSide.Right: _overlays.PlaceGlow((int)m.Right - t, (int)s, t, (int)(e - s)); break;
            case EdgeSide.Left: _overlays.PlaceGlow((int)m.Left, (int)s, t, (int)(e - s)); break;
            case EdgeSide.Bottom: _overlays.PlaceGlow((int)s, (int)m.Bottom - t, (int)(e - s), t); break;
            default: _overlays.PlaceGlow((int)s, (int)m.Top, (int)(e - s), t); break;
        }
    }

    private void UpdateHandoffEnabled() =>
        _handoff.Settings.Enabled = _settings.SharingEnabled && _transport.State == TransportState.Connected;

    private void UpdateNativeActive()
    {
        bool want = _settings.TrackpadMode == TrackpadMode.Native;
        bool fits = _transport.MaxReportPayload >= ReportMap.TouchpadReportLength;
        bool active = want && fits && _input.Touchpads.Count > 0;
        if (want && !fits && _transport.State == TransportState.Connected)
            Log.Warn($"iPad-native trackpad needs {ReportMap.TouchpadReportLength}-byte reports; link allows {_transport.MaxReportPayload}. Using translated gestures.");
        if (active != _nativeActive && _onIpad)
        {
            // switching mid-session: lift everything in the old mode
            ReleaseTouchState();
        }
        _nativeActive = active;
    }

    // ==================================================================
    // Keyboard hook
    // ==================================================================
    private bool OnKey(KeyHookEvent e)
    {
        bool ours = e.ExtraInfo == (UIntPtr)InputThread.OurInjectionSignature;
        bool fakeCtrl = e.ScanCode > 0xFF;               // AltGr's synthesized Left Ctrl

        if (!e.Injected && !fakeCtrl)
        {
            if (e.Vk is VK_LCONTROL or VK_RCONTROL) _ctrl = !e.Up;
            if (e.Vk is VK_LMENU or VK_RMENU) _alt = !e.Up;
        }

        // Ctrl+Alt+Space toggles between devices.
        if (e.Vk == VK_SPACE && _settings.HotkeyEnabled && !e.Injected)
        {
            if (!e.Up && _ctrl && _alt)
            {
                if (!_hotkeySpaceDown) ToggleDevice();
                _hotkeySpaceDown = true;
                return true;
            }
            if (e.Up && _hotkeySpaceDown) { _hotkeySpaceDown = false; return true; }
        }

        if (!_onIpad)
        {
            if (e.Up) _pcKeysDown.Remove(e.Vk); else _pcKeysDown.Add(e.Vk);
            return false;
        }

        // ---- iPad mode ----
        if (e.Up && _pcKeysDown.Remove(e.Vk)) return false;      // a key pressed on Windows releases on Windows
        var map = ScanCodeMap.Map((int)e.ScanCode, e.Extended, (int)e.Vk);
        // Drop other software's synthetic keys (e.g. touchpad-gesture shortcuts like Win+Tab), but keep
        // media keys: laptop hotkey utilities often inject volume/play keys.
        if (e.Injected && !ours && map.Kind != KeyMapKind.Consumer) return true;
        if (!_forwardUserInput)
        {
            if (!e.Up && e.Vk == 0x1B) CancelRequested?.Invoke();   // Esc cancels calibration
            return true;
        }

        switch (map.Kind)
        {
            case KeyMapKind.Keyboard:
                _keyScratch.Clear();
                if (e.Up) _keys.KeyUp((byte)map.Usage, _keyScratch); else _keys.KeyDown((byte)map.Usage, _keyScratch);
                foreach (var r in _keyScratch) _scheduler.EnqueueKeyboard(r);
                break;
            case KeyMapKind.Consumer:
                if (e.Up)
                {
                    if (_consumerDown == map.Usage) { _scheduler.EnqueueConsumer(0); _consumerDown = 0; }
                }
                else
                {
                    if (_consumerDown != 0) _scheduler.EnqueueConsumer(0);   // auto-repeat → re-press
                    _scheduler.EnqueueConsumer(map.Usage);
                    _consumerDown = map.Usage;
                }
                break;
        }
        return true;
    }

    // ==================================================================
    // Mouse hook
    // ==================================================================
    private bool OnMouse(MouseHookEvent e)
    {
        double now = InputThread.Now;
        if (!_onIpad)
        {
            switch (e.Message)
            {
                case WM_LBUTTONDOWN: _pcButtons |= MouseButtons.Left; break;
                case WM_LBUTTONUP: _pcButtons &= ~MouseButtons.Left; break;
                case WM_RBUTTONDOWN: _pcButtons |= MouseButtons.Right; break;
                case WM_RBUTTONUP: _pcButtons &= ~MouseButtons.Right; break;
                case WM_MBUTTONDOWN: _pcButtons |= MouseButtons.Middle; break;
                case WM_MBUTTONUP: _pcButtons &= ~MouseButtons.Middle; break;
                case WM_MOUSEMOVE:
                    _prevHookX = _hookX; _prevHookY = _hookY;
                    _hookX = e.X; _hookY = e.Y;
                    if (!e.Injected || e.ExtraInfo != (UIntPtr)InputThread.OurInjectionSignature)
                    {
                        // Raw sources (mouse counts / touchpad mm) measure the push precisely. Without them
                        // (pen, remote desktop…), estimate from the hook: at the edge the cursor is clamped,
                        // so count each event there as a small push.
                        bool rawRecent = now - _lastRawMouse < 0.05 || now - _lastTouchTime < 0.05;
                        double dx = 0, dy = 0;
                        if (!rawRecent && _layout != null)
                        {
                            dx = _hookX - _prevHookX; dy = _hookY - _prevHookY;
                            if (dx == 0 && dy == 0 && _layout.IsOnCrossingEdge(_hookX, _hookY))
                            {
                                (dx, dy) = _layout.Side switch
                                {
                                    EdgeSide.Right => (6.0, 0.0), EdgeSide.Left => (-6.0, 0.0),
                                    EdgeSide.Bottom => (0.0, 6.0), _ => (0.0, -6.0),
                                };
                            }
                        }
                        PcPointer(dx, dy, now);
                    }
                    break;
            }
            return false;
        }

        // ---- iPad mode: everything is blocked ----
        // ...except the release of a button that went down on Windows (e.g. hotkey pressed mid-drag),
        // so Windows never keeps a stuck button.
        var released = e.Message switch
        {
            WM_LBUTTONUP => MouseButtons.Left,
            WM_RBUTTONUP => MouseButtons.Right,
            WM_MBUTTONUP => MouseButtons.Middle,
            _ => MouseButtons.None,
        };
        if (released != MouseButtons.None && (_pcButtons & released) != 0)
        {
            _pcButtons &= ~released;
            return false;
        }
        if (!_forwardUserInput) return true;
        if (!_rawMouseSeen && !_touchSeen)
        {
            // Fallback only when Raw Input delivers nothing at all (e.g. some remote-desktop setups):
            // forward what Windows would have done. Otherwise the same event would be counted twice.
            switch (e.Message)
            {
                case WM_MOUSEMOVE:
                    int dx = e.X - _parkX, dy = e.Y - _parkY;
                    if (dx != 0 || dy != 0) MouseMotion(dx, dy, now);
                    break;
                case WM_LBUTTONDOWN: SetMouseButton(MouseButtons.Left, true); break;
                case WM_LBUTTONUP: SetMouseButton(MouseButtons.Left, false); break;
                case WM_RBUTTONDOWN: SetMouseButton(MouseButtons.Right, true); break;
                case WM_RBUTTONUP: SetMouseButton(MouseButtons.Right, false); break;
                case WM_MOUSEWHEEL: _scheduler.AddScroll(HiWordSigned(e.MouseData) / 120.0 * _settings.ScrollSpeed, 0); break;
                case WM_MOUSEHWHEEL: _scheduler.AddScroll(0, HiWordSigned(e.MouseData) / 120.0 * _settings.ScrollSpeed); break;
            }
            FlushPendingReturn();
        }
        return true;
    }

    private void PcPointer(double dx, double dy, double now)
    {
        if (_layout == null) return;
        var plan = _handoff.OnPcPointer(_hookX, _hookY, dx, dy, now, _pcButtons != MouseButtons.None);
        if (_settings.ShowEdgeGlow) { _overlays.SetGlow(_handoff.PushProgress); if (_handoff.PushProgress > 0) _lastPushGlow = now; }
        if (plan != null) { EnterIpad(plan, now); return; }

        // Pushing at the edge with no iPad connected: offer help (rate-limited).
        if (_settings.SharingEnabled && _transport.State != TransportState.Connected && _layout.IsOnCrossingEdge(_hookX, _hookY)
            && _layout.Outward(dx, dy) > 0)
        {
            _disconnectedPush += _layout.Outward(dx, dy);
            if (_disconnectedPush > 300 && now - _lastDisconnectedHint > 60)
            {
                _lastDisconnectedHint = now;
                _disconnectedPush = 0;
                PushedWhileDisconnected?.Invoke();
            }
        }
    }

    // ==================================================================
    // Raw mouse
    // ==================================================================
    private void OnRawMouse(RawMouseEvent e)
    {
        if (e.Device == IntPtr.Zero || e.Absolute) return;   // synthesized or remote-desktop input
        _lastRawMouse = e.Time;
        _rawMouseSeen = true;
        if (!_onIpad)
        {
            if (e.Dx != 0 || e.Dy != 0) PcPointer(e.Dx, e.Dy, e.Time);
            return;
        }
        if (!_forwardUserInput) return;
        // Some touchpads also report through a legacy mouse interface: while fingers are on the pad,
        // the gesture engine owns pointer input, so don't count the same motion twice.
        if (_lastTouch != null && _lastTouch.ActiveCount > 0 && e.Time - _lastTouchTime < 0.05) return;

        ushort f = e.ButtonFlags;
        if ((f & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) SetMouseButton(MouseButtons.Left, true);
        if ((f & RI_MOUSE_LEFT_BUTTON_UP) != 0) SetMouseButton(MouseButtons.Left, false);
        if ((f & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) SetMouseButton(MouseButtons.Right, true);
        if ((f & RI_MOUSE_RIGHT_BUTTON_UP) != 0) SetMouseButton(MouseButtons.Right, false);
        if ((f & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) SetMouseButton(MouseButtons.Middle, true);
        if ((f & RI_MOUSE_MIDDLE_BUTTON_UP) != 0) SetMouseButton(MouseButtons.Middle, false);
        if ((f & RI_MOUSE_BUTTON_4_DOWN) != 0) SetMouseButton(MouseButtons.Back, true);
        if ((f & RI_MOUSE_BUTTON_4_UP) != 0) SetMouseButton(MouseButtons.Back, false);
        if ((f & RI_MOUSE_BUTTON_5_DOWN) != 0) SetMouseButton(MouseButtons.Forward, true);
        if ((f & RI_MOUSE_BUTTON_5_UP) != 0) SetMouseButton(MouseButtons.Forward, false);
        if ((f & RI_MOUSE_WHEEL) != 0) _scheduler.AddScroll(e.ButtonData / 120.0, 0);
        if ((f & RI_MOUSE_HWHEEL) != 0) _scheduler.AddScroll(0, e.ButtonData / 120.0);

        if (e.Dx != 0 || e.Dy != 0) MouseMotion(e.Dx * _settings.MouseSpeed, e.Dy * _settings.MouseSpeed, e.Time);
        FlushPendingReturn();
    }

    private void MouseMotion(double dx, double dy, double now)
    {
        _scheduler.AddMouseMotion(dx, dy);
        var back = _handoff.OnIpadMouse(dx, dy, now, (_mouseButtons | _gestureButtons) != MouseButtons.None);
        if (back != null) _pendingReturn = back;
    }

    private void SetMouseButton(MouseButtons b, bool down)
    {
        var nb = down ? _mouseButtons | b : _mouseButtons & ~b;
        if (nb == _mouseButtons) return;
        _mouseButtons = nb;
        PushButtons();
    }

    private void PushButtons()
    {
        _keyScratch.Clear();
        _keys.OnPointerAction(_keyScratch);           // Shift/⌘ held apply to the click
        foreach (var r in _keyScratch) _scheduler.EnqueueKeyboard(r);
        _scheduler.SetMouseButtons(_mouseButtons | _gestureButtons);
    }

    // ==================================================================
    // Touchpad
    // ==================================================================
    private void OnTouch(PrecisionTouchpad pad, TouchFrame frame)
    {
        double now = frame.Time;
        _lastTouchTime = now;
        _lastTouch = frame;
        _touchSeen = true;

        // single-finger motion (for edge pushes and the native-mode pointer estimate)
        double fdx = 0, fdy = 0;
        int active = 0;
        TouchPoint one = default;
        foreach (var c in frame.Contacts) if (c.Tip && c.Confidence) { active++; one = c; }
        if (active == 1 && _prevContacts.TryGetValue(one.Id, out var prev)) { fdx = one.X - prev.x; fdy = one.Y - prev.y; }
        _prevContacts.Clear();
        foreach (var c in frame.Contacts) if (c.Tip && c.Confidence) _prevContacts[c.Id] = (c.X, c.Y);

        if (!_onIpad)
        {
            if (active == 1 && (fdx != 0 || fdy != 0) && !frame.Button)
                PcPointer(fdx * TouchPushPxPerMm, fdy * TouchPushPxPerMm, now);
            return;
        }
        if (!_forwardUserInput) return;

        if (_nativeActive)
        {
            var report = _native.Map(frame);
            if (report != null) _scheduler.SubmitTouch(report);
            if (active == 1 && !frame.Button && (fdx != 0 || fdy != 0))
            {
                var back = _handoff.OnIpadTouch(fdx, fdy, now, false);
                if (back != null) _pendingReturn = back;
            }
        }
        else
        {
            _gestures.Process(frame);
        }
        FlushPendingReturn();
    }

    /// <summary>Gesture engine output → iPad.</summary>
    private sealed class Sink : IGestureSink
    {
        private readonly AppController _c;
        public Sink(AppController c) => _c = c;

        public void Move(double dx, double dy) => _c.MouseMotion(dx, dy, InputThread.Now);

        public void SetButtons(MouseButtons buttons)
        {
            _c._gestureButtons = buttons;
            _c.PushButtons();
        }

        public void Scroll(double vertical, double horizontal) => _c._scheduler.AddScroll(vertical, horizontal);

        public void Gesture(GestureKind kind)
        {
            if (!_c._settings.GestureActions.TryGetValue(kind, out var text) || !ChordParser.TryParse(text, out var chord)) return;
            _c._keyScratch.Clear();
            _c._keys.Tap(chord, _c._keyScratch);
            foreach (var r in _c._keyScratch) _c._scheduler.EnqueueKeyboard(r);
            Log.Info($"Gesture {kind} → {text}");
        }
    }

    // ==================================================================
    // Switching
    // ==================================================================
    private void ToggleDevice()
    {
        if (_onIpad)
        {
            if (!_forwardUserInput) CancelRequested?.Invoke();
            ReturnToPc(_handoff.Return());
            return;
        }
        if (_transport.State != TransportState.Connected || _layout == null || !_settings.SharingEnabled)
        {
            PushedWhileDisconnected?.Invoke();
            return;
        }
        GetCursorPos(out var p);
        var (s, e) = _layout.CrossingSegment;
        double along = Math.Clamp(_layout.Along(p.X, p.Y), s, Math.Max(s, e));
        EnterIpad(_handoff.Enter(along, InputThread.Now), InputThread.Now);
    }

    private void EnterIpad(EntryPlan plan, double now)
    {
        if (_onIpad) return;
        _onIpad = true;
        _keys.Reset();
        _mouseButtons = _gestureButtons = MouseButtons.None;
        _consumerDown = 0;
        _scheduler.Clear();
        _scheduler.ResetButtons();
        _scheduler.Accepting = true;
        foreach (var (dx, dy) in plan.Moves) _scheduler.AddMouseMotionExact(dx, dy, separateReport: true);

        _native.Reset();
        if (!_nativeActive && _lastTouch != null && now - _lastTouchTime < 0.1) _gestures.Adopt(_lastTouch);
        else _gestures.Reset(releaseButtons: false);

        // Park the (now invisible) Windows cursor in the middle of the monitor.
        var m = _layout?.PcMonitor ?? new RectD(0, 0, 1920, 1080);
        _parkX = (int)(m.X + m.Width / 2);
        _parkY = (int)(m.Y + m.Height / 2);
        _overlays.ShowCapture();
        SetCursorPos(_parkX, _parkY);
        _overlays.SetGlow(0);
        if (_settings.ShowEdgeGlow) _overlays.Flash(now);
        _input.SetHighResolutionTimer(true);
        _hookEventsAtWatchdog = _input.HookEvents;
        Log.Info($"→ iPad (entered at {plan.IpadPosition.X:0},{plan.IpadPosition.Y:0} pt)");
        RaiseStatus();
    }

    private void FlushPendingReturn()
    {
        if (_pendingReturn is PointD p)
        {
            _pendingReturn = null;
            ReturnToPc(p);
        }
    }

    /// <param name="cursor">Where to put the Windows cursor; null keeps it where it is.</param>
    private void ReturnToPc(PointD? cursor)
    {
        if (!_onIpad) return;
        _onIpad = false;
        _handoff.Abort();   // no-op if Return() already ran
        ReleaseEverythingOnIpad();
        _scheduler.Accepting = false;
        _overlays.HideCapture();
        if (cursor is PointD c) SetCursorPos((int)Math.Round(c.X), (int)Math.Round(c.Y));
        _input.SetHighResolutionTimer(false);
        _forwardUserInput = true;
        Log.Info("← PC");
        RaiseStatus();
    }

    private void ReleaseEverythingOnIpad()
    {
        _keyScratch.Clear();
        _keys.ReleaseAll(_keyScratch);
        foreach (var r in _keyScratch) _scheduler.EnqueueKeyboard(r);
        if (_consumerDown != 0) { _scheduler.EnqueueConsumer(0); _consumerDown = 0; }
        ReleaseTouchState();
        _mouseButtons = MouseButtons.None;
        _scheduler.SetMouseButtons(MouseButtons.None);
    }

    private void ReleaseTouchState()
    {
        _gestures.Reset(releaseButtons: true);
        _gestureButtons = MouseButtons.None;
        var lift = _native.LiftAll(InputThread.Now);
        if (lift != null) _scheduler.SubmitTouch(lift);
    }

    // ==================================================================
    // Timers, watchdogs, system events
    // ==================================================================
    private double OnTick(double now)
    {
        double next = 0.25;
        try
        {
            if (_onIpad)
            {
                if (_gestures.NeedsTick) { _gestures.Tick(now); next = 0.008; }

                // A finger lift that never arrived (device glitch): end the touch.
                if (_gestures.FingerCount > 0 && now - _lastTouchTime > 0.2)
                {
                    _gestures.Process(TouchFrame.Empty(now));
                    _prevContacts.Clear();
                    foreach (var t in _input.Touchpads) t.ResetAssembly();
                }

                if (now - _lastWatchdog > 0.5)
                {
                    _lastWatchdog = now;
                    if (SystemInfo.SecureDesktopActive())
                    {
                        Log.Info("Secure desktop active → back to PC");
                        ReturnToPc(null);
                    }
                    else
                    {
                        GetCursorPos(out var p);
                        if (p.X != _parkX || p.Y != _parkY)
                        {
                            if (_input.HookEvents == _hookEventsAtWatchdog) _input.ReinstallHooks();
                            SetCursorPos(_parkX, _parkY);
                        }
                        _hookEventsAtWatchdog = _input.HookEvents;
                    }
                }
                FlushPendingReturn();
            }

            if (_overlays.Animate(now)) next = Math.Min(next, 0.016);
            if (_lastPushGlow > 0 && now - _lastPushGlow > 0.25)
            {
                _lastPushGlow = 0;
                _overlays.SetGlow(0);
            }
            else if (_lastPushGlow > 0) next = Math.Min(next, 0.05);

            if (now - _lastBattery > 60)
            {
                _lastBattery = now;
                if (SystemInfo.BatteryPercent() is int pct) _transport.SetBatteryLevel(pct);
            }
        }
        catch (Exception ex) { Log.Error("Tick", ex); }
        return next;
    }

    private void OnTransportStateChanged()
    {
        UpdateHandoffEnabled();
        UpdateNativeActive();
        if (_transport.State != TransportState.Connected)
        {
            if (_onIpad) { Log.Info("iPad link lost → back to PC"); ReturnToPc(_handoff.Return()); }
            _scheduler.HiResScroll = false;
            _scheduler.Clear();
            _scheduler.ResetButtons();
        }
        RaiseStatus();
    }

    private void OnHostWrite(HostReportWrite w)
    {
        if (w.Type == HidReportType.Feature && w.ReportId == ReportIds.MouseResolution && w.Data.Length > 0)
        {
            _scheduler.HiResScroll = (w.Data[0] & 0x01) != 0;
            Log.Info($"iPad high-resolution scrolling: {_scheduler.HiResScroll}");
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => _input.Post(ApplySettingsOnInputThread);

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) =>
        _input.Post(() => { if (_onIpad) ReturnToPc(null); });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _input.Post(() => { if (_onIpad) ReturnToPc(null); });
    }

    private void RaiseStatus()
    {
        try { StatusChanged?.Invoke(); } catch (Exception ex) { Log.Error("Status handler", ex); }
    }

    // ==================================================================
    // Commands from the UI
    // ==================================================================
    public void SwitchToIpad() => _input.Post(() => { if (!_onIpad) ToggleDevice(); });
    public void SwitchToPc() => _input.Post(() => { if (_onIpad) ReturnToPc(_handoff.Return()); });

    /// <summary>Host interface for the calibration runner (all calls are marshalled to the input thread).</summary>
    public CalibrationHost CreateCalibrationHost() => new(this);

    internal sealed class CalibrationHost
    {
        private readonly AppController _c;
        public CalibrationHost(AppController c) => _c = c;

        public bool Connected => _c._transport.State == TransportState.Connected;

        public Task BeginAsync() => Run(() =>
        {
            if (!_c._onIpad)
            {
                var l = _c._layout!;
                var (s, e) = l.CrossingSegment;
                _c.EnterIpad(_c._handoff.Enter((s + e) / 2, InputThread.Now), InputThread.Now);
            }
            _c._forwardUserInput = false;
        });

        public void Move(int dx, int dy) => _c._input.Post(() => _c._scheduler.AddMouseMotionExact(dx, dy, separateReport: true));

        public Task EndAsync(bool returnToPc) => Run(() =>
        {
            _c._forwardUserInput = true;
            if (returnToPc && _c._onIpad) _c.ReturnToPc(_c._handoff.Return());
        });

        private Task Run(Action a)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _c._input.Post(() => { try { a(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } });
            return tcs.Task;
        }
    }

    public void SaveCalibration(GainCurve mouseCurve)
    {
        UpdateSettings(s =>
        {
            s.Calibration.MousePoints = CalibrationData.FromCurve(mouseCurve);
            s.Calibration.CalibratedAt = DateTime.Now;
            s.Calibration.IpadModel = s.Arrangement.IpadModel;
        });
    }

    // ==================================================================
    // Diagnostics
    // ==================================================================
    public string Diagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"iPad Share {typeof(AppController).Assembly.GetName().Version}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine(_transport.Describe());
        sb.AppendLine($"Scheduler: sent {_scheduler.Sent}, failed {_scheduler.Failed}, avg queue latency {_scheduler.AverageQueueLatencyMs:0.0} ms, hi-res scroll {_scheduler.HiResScroll}");
        sb.AppendLine($"Mode: {(_onIpad ? "controlling iPad" : "controlling PC")}, native trackpad active: {_nativeActive}");
        sb.AppendLine($"Hooks installed: {_input.HooksInstalled}, hook events {_input.HookEvents}, raw events {_input.RawEvents}");
        sb.AppendLine();
        sb.AppendLine("Touchpads:");
        if (_input.Touchpads.Count == 0) sb.AppendLine("  none" + (_input.TouchpadError != null ? $" ({_input.TouchpadError})" : ""));
        foreach (var t in _input.Touchpads) sb.AppendLine("  " + t.Describe());
        var prefs = SystemInfo.ReadTouchpadPrefs();
        sb.AppendLine($"  Windows prefs: taps={prefs.Taps}, 2-finger tap={prefs.TwoFingerTap}, right zone={prefs.RightClickZone}, tap-drag={prefs.TapAndDrag}, 3-finger slide={prefs.ThreeFingerSlide}");
        sb.AppendLine();
        sb.AppendLine("Monitors:");
        foreach (var m in SystemInfo.Monitors())
            sb.AppendLine($"  {m.Device}{(m.Primary ? " (primary)" : "")}: {m.Bounds.Width}x{m.Bounds.Height} at {m.Bounds.X},{m.Bounds.Y}, {m.PhysicalMm.Width:0}x{m.PhysicalMm.Height:0} mm ({(m.PhysicalKnown ? "EDID" : "assumed")}), {m.Dpi} dpi");
        if (_layout != null)
        {
            var (s, e) = _layout.CrossingSegment;
            sb.AppendLine($"Layout: iPad on the {_layout.Side} of {_layout.PcMonitor}, edge {_layout.IpadStart:0}–{_layout.IpadEnd:0}, crossing {s:0}–{e:0}, iPad {_layout.IpadPoints.Width}x{_layout.IpadPoints.Height} pt");
        }
        sb.AppendLine($"Cursor model: {_model.Position.X:0},{_model.Position.Y:0} pt, calibrated: {_settings.Calibration.HasMouse}");
        sb.AppendLine();
        sb.AppendLine("Settings:");
        sb.AppendLine(_settings.ToJson());
        sb.AppendLine();
        sb.AppendLine("Recent log:");
        sb.Append(Log.Tail());
        return sb.ToString();
    }
}
