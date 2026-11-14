namespace IpadShare.Core.Handoff;

public sealed class HandoffSettings
{
    /// <summary>Outward push (in pixel-equivalents) needed at the edge before the pointer crosses.</summary>
    public double PushThreshold { get; set; } = 15;
    /// <summary>A push pause longer than this (s) resets the resistance (long enough to re-place a finger).</summary>
    public double PushTimeout { get; set; } = 0.6;
    /// <summary>Push past the iPad's shared edge (points) needed to come back, when calibrated.</summary>
    public double ReturnThresholdCalibrated { get; set; } = 25;
    /// <summary>Same, without calibration (a little larger so the return is never early).</summary>
    public double ReturnThresholdUncalibrated { get; set; } = 45;
    /// <summary>No return during this time after entering (s).</summary>
    public double EntryGrace { get; set; } = 0.25;
    /// <summary>Slam the iPad pointer against its shared edge on entry so its position is exact.</summary>
    public bool SnapOnEntry { get; set; } = true;
    public bool Calibrated { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Mouse motion the app must send to the iPad right after crossing.</summary>
public sealed class EntryPlan
{
    public required IReadOnlyList<(int dx, int dy)> Moves { get; init; }
    public required PointD IpadPosition { get; init; }
    public required double PcAlong { get; init; }
}

/// <summary>
/// The Universal-Control-style switching brain: decides when the pointer crosses from the PC
/// to the iPad (deliberate push at a free edge) and when it comes back (push past the iPad's
/// shared edge, using <see cref="IpadCursorModel"/>). Pure logic; the Windows layer feeds it
/// cursor positions and motion and executes its decisions.
/// </summary>
public sealed class HandoffController
{
    private readonly HandoffSettings _s;
    private double _push;
    private double _lastPushTime = double.NegativeInfinity;
    private double _enteredAt;
    private double _graceUntil = double.NegativeInfinity;
    private double _crossBlockedUntil = double.NegativeInfinity;

    public HandoffController(ScreenLayout layout, HandoffSettings settings, IpadCursorModel model)
    {
        Layout = layout;
        _s = settings;
        Model = model;
    }

    public ScreenLayout Layout { get; private set; }
    public IpadCursorModel Model { get; }
    public HandoffSettings Settings => _s;
    public bool OnIpad { get; private set; }

    /// <summary>0…1: how close the current edge push is to crossing (for the edge glow).</summary>
    public double PushProgress => _s.PushThreshold <= 0 ? 0 : Math.Clamp(_push / _s.PushThreshold, 0, 1);

    public void SetLayout(ScreenLayout layout)
    {
        Layout = layout;
        Model.Screen = layout.IpadPoints;
        _push = 0;
    }

    // ---------------- PC side ----------------

    /// <summary>
    /// Feed every pointer event while on the PC.
    /// </summary>
    /// <param name="x">Cursor position (physical px).</param>
    /// <param name="y">Cursor position (physical px).</param>
    /// <param name="dx">Raw motion of this event (pixel-equivalents; may be 0 if unknown).</param>
    /// <param name="dy">Raw motion of this event.</param>
    /// <param name="t">Seconds.</param>
    /// <param name="buttonsDown">Any mouse button held (crossing is blocked).</param>
    /// <returns>An entry plan when the pointer should cross now; otherwise null.</returns>
    public EntryPlan? OnPcPointer(double x, double y, double dx, double dy, double t, bool buttonsDown)
    {
        if (OnIpad || !_s.Enabled || !Layout.HasOverlap || t < _crossBlockedUntil) { _push = 0; return null; }
        if (buttonsDown || !Layout.IsOnCrossingEdge(x, y)) { _push = 0; return null; }

        double outward = Layout.Outward(dx, dy);
        if (t - _lastPushTime > _s.PushTimeout) _push = 0;
        if (outward > 0)
        {
            _push += outward;
            _lastPushTime = t;
        }
        else if (outward < 0) _push = Math.Max(0, _push + outward * 2);

        if (_push < _s.PushThreshold) return null;
        return Enter(Layout.Along(x, y), t);
    }

    /// <summary>Cross now (hotkey, shake or push). <paramref name="pcAlong"/> is the along-edge pixel to enter at.</summary>
    /// <param name="toCenter">A jump (shake/hotkey) lands in the middle of the iPad instead of at the edge.</param>
    public EntryPlan Enter(double pcAlong, double t, bool toCenter = false)
    {
        _push = 0;
        OnIpad = true;
        _enteredAt = t;
        // After a jump, trailing hand motion must not immediately push back out.
        _graceUntil = t + (toCenter ? 0.9 : _s.EntryGrace);

        var moves = new List<(int, int)>();
        var shared = Layout.IpadSharedEdge;
        double targetAlong = Layout.PcToIpadAlong(pcAlong);
        var p = Model.Position;

        if (_s.SnapOnEntry && Layout.Side.IsHorizontalNeighbour())
        {
            // Two huge reports toward the shared edge: iPadOS clamps, so X becomes exact.
            // (Kept to two so the user's own motion isn't stuck behind a burst of reports.)
            int dir = shared == EdgeSide.Left ? -1 : 1;
            moves.Add((dir * 6000, 0));
            moves.Add((dir * 6000, 0));
            Model.PinTo(shared);
        }
        else
        {
            // Top/bottom: no slam (it could open the Dock / Notification Center); move by estimate.
            var target = shared switch
            {
                EdgeSide.Top => new PointD(p.X, 0),
                EdgeSide.Bottom => new PointD(p.X, Layout.IpadPoints.Height),
                EdgeSide.Left => new PointD(0, p.Y),
                _ => new PointD(Layout.IpadPoints.Width, p.Y),
            };
            AddEstimatedMove(moves, target.X - p.X, target.Y - p.Y);
            Model.Place(target);
        }

        // Move along the edge to line up with where the PC cursor left.
        p = Model.Position;
        if (Layout.Side.IsHorizontalNeighbour())
        {
            AddEstimatedMove(moves, 0, targetAlong - p.Y);
            Model.Place(new PointD(p.X, targetAlong));
        }
        else
        {
            AddEstimatedMove(moves, targetAlong - p.X, 0);
            Model.Place(new PointD(targetAlong, p.Y));
        }
        if (toCenter)
        {
            p = Model.Position;
            var c = new PointD(Layout.IpadPoints.Width / 2, Layout.IpadPoints.Height / 2);
            AddEstimatedMove(moves, c.X - p.X, c.Y - p.Y);
            Model.Place(c);
        }
        Model.ClearOvershoot();
        return new EntryPlan { Moves = moves, IpadPosition = Model.Position, PcAlong = pcAlong };
    }

    /// <summary>
    /// Converts a point delta into at most six mouse reports, using the gain the iPad applies at that
    /// per-report size (so the move lands close to the target without a long burst of reports).
    /// </summary>
    private void AddEstimatedMove(List<(int, int)> moves, double dxPts, double dyPts)
    {
        const int maxReports = 6;
        const double interval = 0.015;               // the scheduler's typical report spacing
        double dist = Math.Sqrt(dxPts * dxPts + dyPts * dyPts);
        if (dist < 1) return;
        // Pick the per-report size whose resulting motion covers the distance in ≤ maxReports.
        double perReportPts = Math.Max(dist / maxReports, 20);
        int steps = (int)Math.Ceiling(dist / perReportPts);
        double counts = 0;
        // c · G(c / interval) = perReportPts, solved by bisection (monotone)
        double lo = 0, hi = perReportPts / Math.Max(1e-4, Model.MouseCurve.Evaluate(0));
        for (int i = 0; i < 40; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid * Model.MouseCurve.Evaluate(mid / interval) < perReportPts) lo = mid; else hi = mid;
        }
        counts = 0.5 * (lo + hi);
        double ux = dxPts / dist, uy = dyPts / dist;
        double sx = 0, sy = 0;
        int ix = 0, iy = 0;
        for (int i = 1; i <= steps; i++)
        {
            sx += ux * counts; sy += uy * counts;
            int nx = (int)Math.Round(sx) - ix, ny = (int)Math.Round(sy) - iy;
            ix += nx; iy += ny;
            if (nx != 0 || ny != 0) moves.Add((nx, ny));
        }
    }

    // ---------------- iPad side ----------------

    /// <summary>Point delta applied to the model by the last iPad-side motion (for shake detection).</summary>
    public PointD LastDelta { get; private set; }

    /// <summary>
    /// Feed every mouse report the iPad actually received (use delivered reports, not produced
    /// motion, so dropped reports can't make the model drift). Returns the Windows cursor position
    /// when control should come back.
    /// </summary>
    public PointD? OnIpadMouse(double dx, double dy, double t, bool buttonsDown)
    {
        if (!OnIpad) return null;
        LastDelta = Model.ApplyMouse(dx, dy, t);
        return CheckReturn(t, buttonsDown);
    }

    /// <summary>Same for single-finger trackpad motion in iPad-native mode (mm).</summary>
    public PointD? OnIpadTouch(double dxMm, double dyMm, double t, bool buttonsDown)
    {
        if (!OnIpad) return null;
        LastDelta = Model.ApplyTouchMm(dxMm, dyMm, t);
        return CheckReturn(t, buttonsDown);
    }

    private PointD? CheckReturn(double t, bool buttonsDown)
    {
        if (buttonsDown || t < _graceUntil) return null;
        double threshold = _s.Calibrated ? _s.ReturnThresholdCalibrated : _s.ReturnThresholdUncalibrated;
        if (Model.Overshoot(Layout.IpadSharedEdge) < threshold) return null;
        return Return(t);
    }

    /// <summary>Come back to the PC now (push, hotkey or shake). Returns where to put the Windows cursor.</summary>
    /// <param name="t">Now (seconds); crossing back is briefly blocked so trailing motion can't bounce.</param>
    /// <param name="toCenter">A jump lands in the middle of the monitor instead of at the edge.</param>
    public PointD Return(double t = double.NegativeInfinity, bool toCenter = false)
    {
        OnIpad = false;
        _push = 0;
        _crossBlockedUntil = t + 0.5;
        var p = Model.Position;
        double along = Layout.Side.IsHorizontalNeighbour() ? p.Y : p.X;
        Model.PinTo(Layout.IpadSharedEdge);
        if (toCenter)
        {
            var m = Layout.PcMonitor;
            return new PointD(m.X + m.Width / 2, m.Y + m.Height / 2);
        }
        return Layout.ReturnPoint(along);
    }

    /// <summary>Drop to PC mode without computing a return point (lock screen, link lost).</summary>
    public void Abort()
    {
        OnIpad = false;
        _push = 0;
    }
}
