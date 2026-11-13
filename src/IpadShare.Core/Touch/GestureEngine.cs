using IpadShare.Core.Hid;

namespace IpadShare.Core.Touch;

/// <summary>Receives what the gesture engine wants the iPad to do.</summary>
public interface IGestureSink
{
    /// <summary>Relative pointer motion in mouse counts (fractions are accumulated by the sink).</summary>
    void Move(double dx, double dy);
    void SetButtons(MouseButtons buttons);
    /// <summary>Scroll in wheel notches. Vertical + = wheel up (fingers moving up), horizontal + = pan right.</summary>
    void Scroll(double vertical, double horizontal);
    void Gesture(GestureKind kind);
}

public sealed class GestureSettings
{
    /// <summary>Mouse counts per millimetre of finger travel at speed 1.0.</summary>
    public double PointerCountsPerMm { get; set; } = 20;
    /// <summary>User pointer speed multiplier (0.25 … 3).</summary>
    public double PointerSpeed { get; set; } = 1.0;
    /// <summary>Wheel notches per millimetre of two-finger travel at scroll speed 1.0.</summary>
    public double ScrollNotchesPerMm { get; set; } = 0.22;
    public double ScrollSpeed { get; set; } = 1.0;
    public bool ReverseScroll { get; set; }
    public bool Momentum { get; set; } = true;

    public bool TapToClick { get; set; } = true;
    public bool TwoFingerTapRightClick { get; set; } = true;
    public bool TapAndDrag { get; set; } = true;
    public bool RightClickZone { get; set; } = true;
    public bool PinchToZoom { get; set; } = true;
    public bool MultiFingerGestures { get; set; } = true;

    // Tuning (millimetres / seconds). Exposed for tests and power users.
    public double TapMaxDuration { get; set; } = 0.25;
    public double TapMaxTravelMm { get; set; } = 2.0;
    public double TapDragWindow { get; set; } = 0.18;
    public double DragStartMm { get; set; } = 0.8;
    public double ScrollStartMm { get; set; } = 1.2;
    public double PinchStartMm { get; set; } = 4.0;
    public double PinchStepRatio { get; set; } = 1.22;
    public double SwipeThresholdMm { get; set; } = 9.0;
    public double SwipeHoldTime { get; set; } = 0.35;
    public double MomentumTimeConstant { get; set; } = 0.42;
    public double MomentumMinStart { get; set; } = 2.5;   // notches/s
    public double MomentumStop { get; set; } = 0.35;      // notches/s
    public double MomentumMax { get; set; } = 150;        // notches/s
}

/// <summary>
/// A complete touchpad driver for Translated mode: turns raw finger contacts from the laptop's
/// Precision Touchpad into pointer motion, clicks, taps, tap-and-drag, smooth two-finger
/// scrolling with momentum, pinch and multi-finger swipes. All thresholds are in millimetres,
/// so it behaves the same on any touchpad size. Single-threaded; drive it with
/// <see cref="Process"/> for every frame and <see cref="Tick"/> while <see cref="NeedsTick"/>.
/// </summary>
public sealed class GestureEngine
{
    private enum Mode { Idle, Pointer, TwoUndecided, Scroll, Pinch, Swipe, ButtonDrag, Ignore }

    private sealed class Contact
    {
        public int Id;
        public double X, Y, DownX, DownY, Dx, Dy;
        public bool IsNew;
    }

    private readonly GestureSettings _s;
    private readonly IGestureSink _sink;
    private readonly Dictionary<int, Contact> _contacts = new();
    private readonly List<int> _liftScratch = new();

    private Mode _mode = Mode.Idle;
    private int _prevCount;
    private double _prevTime = double.NaN;

    // Session = first finger down → last finger up
    private double _sessionStart;
    private int _sessionMaxFingers;
    private double _sessionMaxTravel;
    private bool _sessionTapEligible;
    private bool _sessionActionFired;
    private bool _sessionAdopted;

    // Buttons
    private MouseButtons _emitted;
    private bool _physDown;
    private MouseButtons _physButton;
    private bool _tapDragging;

    // Tap-to-click
    private double? _pendingTapTime;
    private bool _tapDragCandidate;

    // Two-finger
    private double _twoStartCx, _twoStartCy, _twoStartDist;
    private double _twoCx, _twoCy, _twoDist;
    private bool _lockVertical, _lockHorizontal;
    private double _pinchBase;

    // Scroll velocity + momentum
    private readonly Queue<(double t, double v, double h)> _scrollSamples = new();
    private double _momV, _momH, _momLast;
    private bool _momentum;

    // Swipe
    private double _swX, _swY;
    private int _swFingers;
    private double? _swipeUpPendingSince;

    public GestureEngine(GestureSettings settings, IGestureSink sink)
    {
        _s = settings;
        _sink = sink;
    }

    public TouchpadGeometry Geometry { get; set; } = TouchpadGeometry.Default;
    public GestureSettings Settings => _s;
    public MouseButtons Buttons => _emitted;
    public int FingerCount => _contacts.Count;
    public bool MomentumActive => _momentum;

    /// <summary>True while fingers are down, a button is held, or momentum/pending work is running.</summary>
    public bool IsBusy => _contacts.Count > 0 || _emitted != MouseButtons.None || NeedsTick;

    public bool NeedsTick => _momentum || _pendingTapTime.HasValue || _swipeUpPendingSince.HasValue;

    // ==================================================================
    public void Process(TouchFrame frame)
    {
        double now = frame.Time;
        double dt = double.IsNaN(_prevTime) ? 0.008 : Math.Clamp(now - _prevTime, 0.001, 0.1);

        // ---- update contacts ----
        bool anyNew = false;
        var seen = new HashSet<int>();
        foreach (var p in frame.Contacts)
        {
            if (!p.Tip || !p.Confidence) continue;          // palms and lifted fingers are ignored
            if (!seen.Add(p.Id)) continue;
            if (_contacts.TryGetValue(p.Id, out var c))
            {
                c.Dx = p.X - c.X; c.Dy = p.Y - c.Y;
                c.X = p.X; c.Y = p.Y;
                c.IsNew = false;
                double travel = Math.Sqrt(Sq(c.X - c.DownX) + Sq(c.Y - c.DownY));
                if (travel > _sessionMaxTravel) _sessionMaxTravel = travel;
            }
            else
            {
                _contacts[p.Id] = new Contact { Id = p.Id, X = p.X, Y = p.Y, DownX = p.X, DownY = p.Y, IsNew = true };
                anyNew = true;
            }
        }
        _liftScratch.Clear();
        foreach (var id in _contacts.Keys) if (!seen.Contains(id)) _liftScratch.Add(id);
        foreach (var id in _liftScratch) _contacts.Remove(id);

        int n = _contacts.Count;
        if (anyNew && _momentum) StopMomentum();              // touching stops inertial scroll

        if (n > 0 && _prevCount == 0) BeginSession(now);
        if (n > _sessionMaxFingers) _sessionMaxFingers = n;

        // ---- physical clickpad button ----
        if (frame.Button && !_physDown) OnButtonDown(n);
        else if (!frame.Button && _physDown) OnButtonUp();

        // ---- motion ----
        if (n > 0) Update(n, now, dt);
        else if (_prevCount > 0) EndSession(now);

        _prevCount = n;
        _prevTime = now;
    }

    /// <summary>Advance timers (momentum, delayed tap, swipe-hold). Call every ~8 ms while <see cref="NeedsTick"/>.</summary>
    public void Tick(double now)
    {
        if (_momentum)
        {
            double dt = Math.Clamp(now - _momLast, 0, 0.05);
            _momLast = now;
            if (dt > 0)
            {
                _sink.Scroll(_momV * dt, _momH * dt);
                double k = Math.Exp(-dt / _s.MomentumTimeConstant);
                _momV *= k; _momH *= k;
                if (Math.Sqrt(Sq(_momV) + Sq(_momH)) < _s.MomentumStop) StopMomentum();
            }
        }

        if (_pendingTapTime is double t && !_tapDragCandidate && now - t >= _s.TapDragWindow)
            FlushPendingTap();

        if (_swipeUpPendingSince is double su && _contacts.Count > 0 && now - su >= _s.SwipeHoldTime)
        {
            _swipeUpPendingSince = null;
            _sink.Gesture(GestureKind.ThreeSwipeUpHold);
        }
    }

    /// <summary>
    /// Continue fingers that are already on the pad (e.g. the finger that just pushed the pointer
    /// across the screen edge) as plain pointer motion, without generating a tap.
    /// </summary>
    public void Adopt(TouchFrame frame)
    {
        Reset(releaseButtons: false);
        foreach (var p in frame.Contacts)
        {
            if (!p.Tip || !p.Confidence || _contacts.ContainsKey(p.Id)) continue;
            _contacts[p.Id] = new Contact { Id = p.Id, X = p.X, Y = p.Y, DownX = p.X, DownY = p.Y };
        }
        _prevCount = _contacts.Count;
        _prevTime = frame.Time;
        if (_contacts.Count > 0)
        {
            BeginSession(frame.Time);
            _sessionAdopted = true;
            _sessionTapEligible = false;
            _sessionMaxFingers = _contacts.Count;
            _mode = _contacts.Count == 1 ? Mode.Pointer : Mode.Ignore;
        }
        _physDown = frame.Button;  // a held click is owned by whoever saw it go down
    }

    /// <summary>Forget everything (stop momentum, cancel taps). Optionally releases held buttons on the iPad.</summary>
    public void Reset(bool releaseButtons = true)
    {
        StopMomentum();
        _pendingTapTime = null;
        _tapDragCandidate = false;
        _swipeUpPendingSince = null;
        _contacts.Clear();
        _mode = Mode.Idle;
        _prevCount = 0;
        _prevTime = double.NaN;
        _physDown = false;
        _tapDragging = false;
        if (releaseButtons && _emitted != MouseButtons.None)
        {
            _emitted = MouseButtons.None;
            _sink.SetButtons(MouseButtons.None);
        }
        else if (!releaseButtons) _emitted = MouseButtons.None;
    }

    // ==================================================================
    private void BeginSession(double now)
    {
        _sessionStart = now;
        _sessionMaxFingers = 0;
        _sessionMaxTravel = 0;
        _sessionTapEligible = true;
        _sessionActionFired = false;
        _sessionAdopted = false;
        _mode = Mode.Idle;
        _swipeUpPendingSince = null;
        _tapDragCandidate = _pendingTapTime.HasValue && _s.TapAndDrag;
    }

    private void Update(int n, double now, double dt)
    {
        if (_physDown) { SetMode(Mode.ButtonDrag); PointerFromMostMoving(); return; }
        if (_sessionActionFired) return;                      // a swipe already fired: ignore the rest

        // A thumb resting still at the bottom of a clickpad must not turn pointing into scrolling.
        Contact? resting = n == 2 ? RestingThumb() : null;
        if (resting != null) n = 1;

        switch (n)
        {
            case 1:
                if (_mode is Mode.Scroll or Mode.Pinch or Mode.TwoUndecided or Mode.Swipe) EndTwoFinger(now);
                if (_mode == Mode.Ignore && _sessionAdopted) return;
                SetMode(Mode.Pointer);
                var c = FirstExcept(resting);
                if (_tapDragCandidate && _sessionMaxTravel > _s.DragStartMm)
                {
                    // tap … touch-and-move → drag with the left button held
                    _pendingTapTime = null;
                    _tapDragCandidate = false;
                    _tapDragging = true;
                    Press(MouseButtons.Left);
                }
                Move(c.Dx, c.Dy);
                if (_momentum && Math.Abs(c.Dx) + Math.Abs(c.Dy) > 0.5) StopMomentum();
                break;

            case 2:
                if (_mode is not (Mode.TwoUndecided or Mode.Scroll or Mode.Pinch))
                {
                    FlushPendingTap();
                    StartTwoFinger();
                    SetMode(Mode.TwoUndecided);
                }
                UpdateTwoFinger(now, dt);
                break;

            case 3:
            case 4:
                if (!_s.MultiFingerGestures) { SetMode(Mode.Ignore); return; }
                if (_mode != Mode.Swipe || _swFingers != n)
                {
                    if (_mode is Mode.Scroll or Mode.Pinch or Mode.TwoUndecided) EndTwoFinger(now, allowMomentum: false);
                    FlushPendingTap();
                    _swX = _swY = 0;
                    _swFingers = n;
                    SetMode(Mode.Swipe);
                }
                UpdateSwipe(n, now);
                break;

            default:
                SetMode(Mode.Ignore);
                break;
        }
    }

    private void SetMode(Mode m)
    {
        if (_mode == m) return;
        // Swipe mode alone doesn't cancel a tap: a 3-finger tap passes through it until the swipe fires.
        if (m is Mode.Scroll or Mode.Pinch or Mode.Ignore or Mode.ButtonDrag) _sessionTapEligible = false;
        _mode = m;
    }

    /// <summary>The stationary contact in the bottom 20 % of the pad when exactly one of two fingers moves.</summary>
    private Contact? RestingThumb()
    {
        if (_mode is Mode.Scroll or Mode.Pinch) return null;
        Contact? rest = null; int moving = 0;
        double zone = Geometry.HeightMm * 0.8;
        foreach (var c in _contacts.Values)
        {
            double travel = Math.Sqrt(Sq(c.X - c.DownX) + Sq(c.Y - c.DownY));
            if (c.DownY >= zone && travel < 1.5) { if (rest == null) rest = c; else return null; }
            else moving++;
        }
        return rest != null && moving == 1 ? rest : null;
    }

    private void Move(double dxMm, double dyMm)
    {
        if (dxMm == 0 && dyMm == 0) return;
        double k = _s.PointerCountsPerMm * _s.PointerSpeed;
        _sink.Move(dxMm * k, dyMm * k);
    }

    private void PointerFromMostMoving()
    {
        Contact? best = null;
        double bestD = 0;
        foreach (var c in _contacts.Values)
        {
            double d = Math.Abs(c.Dx) + Math.Abs(c.Dy);
            if (d > bestD) { bestD = d; best = c; }
        }
        if (best != null) Move(best.Dx, best.Dy);
    }

    // ---------------- two fingers: scroll or pinch ----------------
    private void StartTwoFinger()
    {
        var (cx, cy, d) = TwoGeometry();
        _twoStartCx = _twoCx = cx;
        _twoStartCy = _twoCy = cy;
        _twoStartDist = _twoDist = d;
        _lockVertical = _lockHorizontal = false;
        _scrollSamples.Clear();
    }

    private (double cx, double cy, double dist) TwoGeometry()
    {
        Contact? a = null, b = null;
        foreach (var c in _contacts.Values) { if (a == null) a = c; else { b = c; break; } }
        if (a == null || b == null) return (0, 0, 0);
        return ((a.X + b.X) / 2, (a.Y + b.Y) / 2, Math.Sqrt(Sq(a.X - b.X) + Sq(a.Y - b.Y)));
    }

    private void UpdateTwoFinger(double now, double dt)
    {
        var (cx, cy, dist) = TwoGeometry();
        bool newContact = false;
        foreach (var c in _contacts.Values) if (c.IsNew) newContact = true;
        if (newContact)
        {
            // re-base on a finger change so the centroid doesn't jump
            _twoCx = cx; _twoCy = cy; _twoDist = dist;
            if (_mode == Mode.TwoUndecided) { _twoStartCx = cx; _twoStartCy = cy; _twoStartDist = dist; }
            if (_mode == Mode.Pinch) _pinchBase = dist;
            return;
        }

        double fdx = cx - _twoCx, fdy = cy - _twoCy;
        _twoCx = cx; _twoCy = cy; _twoDist = dist;

        if (_mode == Mode.TwoUndecided)
        {
            double travel = Math.Sqrt(Sq(cx - _twoStartCx) + Sq(cy - _twoStartCy));
            double spread = Math.Abs(dist - _twoStartDist);
            // Scrolling moves both fingers the same way (spread ≈ 0). Pinching changes their
            // distance; with one finger anchored, spread ≈ 2 × centroid travel.
            bool pinchLike = _s.PinchToZoom && spread >= 1.2 * travel && spread > 0.6;
            if (pinchLike)
            {
                if (spread <= _s.PinchStartMm) return;            // keep watching
                SetMode(Mode.Pinch);
                _pinchBase = _twoStartDist;                       // the opening spread counts toward the first step
                UpdatePinch(dist);
                return;
            }
            if (travel > _s.ScrollStartMm)
            {
                SetMode(Mode.Scroll);
                double tx = Math.Abs(cx - _twoStartCx), ty = Math.Abs(cy - _twoStartCy);
                if (ty > 2.0 * tx) _lockVertical = true;
                else if (tx > 2.0 * ty) _lockHorizontal = true;
                // include the motion that triggered scrolling so it starts without a dead zone
                EmitScroll(cx - _twoStartCx, cy - _twoStartCy, now, dt, accelerate: false);
                return;
            }
            else return;
        }

        if (_mode == Mode.Scroll) EmitScroll(fdx, fdy, now, dt);
        else if (_mode == Mode.Pinch) UpdatePinch(dist);
    }

    private void EmitScroll(double dxMm, double dyMm, double now, double dt, bool accelerate = true)
    {
        if (_lockVertical) dxMm = 0;
        if (_lockHorizontal) dyMm = 0;
        if (dxMm == 0 && dyMm == 0) return;

        double speedMm = Math.Sqrt(Sq(dxMm) + Sq(dyMm)) / dt;          // mm/s
        double accel = accelerate ? 1 + Math.Clamp((speedMm - 15) / 60, 0, 2.5) : 1; // gentle, Mac-like
        double k = _s.ScrollNotchesPerMm * _s.ScrollSpeed * accel;
        double sign = _s.ReverseScroll ? -1 : 1;
        double v = -dyMm * k * sign;   // fingers up (dy < 0) → wheel up (+)
        double h = dxMm * k * sign;    // fingers right → pan right (+)
        _sink.Scroll(v, h);

        _scrollSamples.Enqueue((now, v, h));
        while (_scrollSamples.Count > 0 && now - _scrollSamples.Peek().t > 0.08) _scrollSamples.Dequeue();
    }

    private void UpdatePinch(double dist)
    {
        if (_pinchBase <= 0) { _pinchBase = dist; return; }
        double r = dist / _pinchBase;
        if (r >= _s.PinchStepRatio) { _sink.Gesture(GestureKind.PinchOut); _pinchBase = dist; }
        else if (r <= 1.0 / _s.PinchStepRatio) { _sink.Gesture(GestureKind.PinchIn); _pinchBase = dist; }
    }

    private void EndTwoFinger(double now, bool allowMomentum = true)
    {
        if (_mode == Mode.Scroll && allowMomentum) StartMomentum(now);
        _scrollSamples.Clear();
    }

    private void StartMomentum(double now)
    {
        if (!_s.Momentum || _scrollSamples.Count == 0) return;
        var last = _scrollSamples.Last();
        if (now - last.t > 0.05) return;                  // fingers had stopped before lifting
        double first = _scrollSamples.Peek().t;
        double span = Math.Max(now - first, 0.016);
        double sv = 0, sh = 0;
        foreach (var s in _scrollSamples) { sv += s.v; sh += s.h; }
        double v = sv / span, h = sh / span;
        double mag = Math.Sqrt(Sq(v) + Sq(h));
        if (mag < _s.MomentumMinStart) return;
        if (mag > _s.MomentumMax) { v *= _s.MomentumMax / mag; h *= _s.MomentumMax / mag; }
        _momV = v; _momH = h;
        _momLast = now;
        _momentum = true;
    }

    private void StopMomentum()
    {
        _momentum = false;
        _momV = _momH = 0;
    }

    // ---------------- 3/4 finger swipes ----------------
    private void UpdateSwipe(int n, double now)
    {
        double sx = 0, sy = 0; int m = 0;
        foreach (var c in _contacts.Values) { if (c.IsNew) continue; sx += c.Dx; sy += c.Dy; m++; }
        if (m == 0) return;
        _swX += sx / m; _swY += sy / m;

        double ax = Math.Abs(_swX), ay = Math.Abs(_swY);
        if (Math.Max(ax, ay) < _s.SwipeThresholdMm) return;

        _sessionActionFired = true;
        _sessionTapEligible = false;
        GestureKind kind;
        if (ay >= ax)
        {
            if (_swY < 0)
            {
                if (n == 3) { _swipeUpPendingSince = now; return; } // Home on lift, App Switcher on hold
                kind = GestureKind.FourSwipeUp;
            }
            else kind = n == 3 ? GestureKind.ThreeSwipeDown : GestureKind.FourSwipeDown;
        }
        else kind = _swX < 0
            ? (n == 3 ? GestureKind.ThreeSwipeLeft : GestureKind.FourSwipeLeft)
            : (n == 3 ? GestureKind.ThreeSwipeRight : GestureKind.FourSwipeRight);
        _sink.Gesture(kind);
    }

    // ---------------- buttons and taps ----------------
    private void OnButtonDown(int n)
    {
        FlushPendingTap();
        _tapDragCandidate = false;
        _physDown = true;
        _sessionTapEligible = false;
        MouseButtons b = MouseButtons.Left;
        if (n >= 2) b = MouseButtons.Right;                          // two-finger click
        else if (n == 1 && _s.RightClickZone)
        {
            var c = First();
            var g = Geometry;
            if (c.X >= g.WidthMm * 0.6 && c.Y >= g.HeightMm * 0.75) b = MouseButtons.Right;
        }
        _physButton = b;
        Press(b);
    }

    private void OnButtonUp()
    {
        _physDown = false;
        Release(_physButton);
        if (_mode == Mode.ButtonDrag) _mode = _contacts.Count == 1 ? Mode.Pointer : Mode.Ignore;
    }

    private void EndSession(double now)
    {
        if (_mode is Mode.Scroll or Mode.Pinch or Mode.TwoUndecided) EndTwoFinger(now);

        if (_swipeUpPendingSince.HasValue)
        {
            _swipeUpPendingSince = null;
            _sink.Gesture(GestureKind.ThreeSwipeUp);                 // quick 3-finger swipe up → Home
        }

        if (_tapDragging)
        {
            _tapDragging = false;
            Release(MouseButtons.Left);
        }

        bool isTap = _sessionTapEligible && !_sessionAdopted
                     && now - _sessionStart <= _s.TapMaxDuration
                     && _sessionMaxTravel <= _s.TapMaxTravelMm;

        if (isTap)
        {
            switch (_sessionMaxFingers)
            {
                case 1 when _s.TapToClick:
                    if (_tapDragCandidate)
                    {
                        // second tap of a double-tap: deliver both clicks now
                        _pendingTapTime = null;
                        _tapDragCandidate = false;
                        Click(MouseButtons.Left);
                        Click(MouseButtons.Left);
                    }
                    else if (_s.TapAndDrag) _pendingTapTime = now;      // wait briefly for tap-and-drag
                    else Click(MouseButtons.Left);
                    break;
                case 2 when _s.TwoFingerTapRightClick:
                    FlushPendingTap();
                    Click(MouseButtons.Right);
                    break;
                case 3 when _s.MultiFingerGestures:
                    FlushPendingTap();
                    _sink.Gesture(GestureKind.ThreeFingerTap);
                    break;
                case 4 when _s.MultiFingerGestures:
                    FlushPendingTap();
                    _sink.Gesture(GestureKind.FourFingerTap);
                    break;
            }
        }
        else if (_tapDragCandidate)
        {
            // touched again after a tap but neither dragged nor tapped: deliver the original click
            _tapDragCandidate = false;
            FlushPendingTap();
        }

        _tapDragCandidate = false;
        _mode = Mode.Idle;
    }

    private void FlushPendingTap()
    {
        if (_pendingTapTime == null) return;
        _pendingTapTime = null;
        _tapDragCandidate = false;
        Click(MouseButtons.Left);
    }

    private void Click(MouseButtons b)
    {
        Press(b);
        Release(b);
    }

    private void Press(MouseButtons b)
    {
        if ((_emitted & b) == b) return;
        _emitted |= b;
        _sink.SetButtons(_emitted);
    }

    private void Release(MouseButtons b)
    {
        if ((_emitted & b) == 0) return;
        _emitted &= ~b;
        _sink.SetButtons(_emitted);
    }

    private Contact First() => FirstExcept(null);

    private Contact FirstExcept(Contact? skip)
    {
        foreach (var c in _contacts.Values) if (!ReferenceEquals(c, skip)) return c;
        throw new InvalidOperationException("no contacts");
    }

    private static double Sq(double v) => v * v;
}
