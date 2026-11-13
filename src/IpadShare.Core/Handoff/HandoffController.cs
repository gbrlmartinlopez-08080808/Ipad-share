namespace IpadShare.Core.Handoff;

public sealed class HandoffSettings
{
    /// <summary>Outward push (in pixel-equivalents) needed at the edge before the pointer crosses.</summary>
    public double PushThreshold { get; set; } = 60;
    /// <summary>A push pause longer than this (s) resets the resistance.</summary>
    public double PushTimeout { get; set; } = 0.15;
    /// <summary>Push past the iPad's shared edge (points) needed to come back, when calibrated.</summary>
    public double ReturnThresholdCalibrated { get; set; } = 30;
    /// <summary>Same, without calibration (larger so the return is never early).</summary>
    public double ReturnThresholdUncalibrated { get; set; } = 90;
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
        if (OnIpad || !_s.Enabled || !Layout.HasOverlap) { _push = 0; return null; }
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

    /// <summary>Cross now (hotkey or push). <paramref name="pcAlong"/> is the along-edge pixel to enter at.</summary>
    public EntryPlan Enter(double pcAlong, double t)
    {
        _push = 0;
        OnIpad = true;
        _enteredAt = t;

        var moves = new List<(int, int)>();
        var shared = Layout.IpadSharedEdge;
        double targetAlong = Layout.PcToIpadAlong(pcAlong);
        var p = Model.Position;

        if (_s.SnapOnEntry && Layout.Side.IsHorizontalNeighbour())
        {
            // A few huge reports toward the shared edge: iPadOS clamps, so X becomes exact.
            int dir = shared == EdgeSide.Left ? -1 : 1;
            for (int i = 0; i < 4; i++) moves.Add((dir * 3000, 0));
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
        Model.ClearOvershoot();
        return new EntryPlan { Moves = moves, IpadPosition = Model.Position, PcAlong = pcAlong };
    }

    /// <summary>Converts a point delta into moderate mouse reports using the gain at a steady reference speed.</summary>
    private void AddEstimatedMove(List<(int, int)> moves, double dxPts, double dyPts)
    {
        const int perReport = 40;
        const double referenceSpeed = 40 / 0.0125;  // 40 counts per ~12.5 ms report
        double g = Model.MouseCurve.Evaluate(referenceSpeed);
        double cx = dxPts / g, cy = dyPts / g;
        int steps = (int)Math.Ceiling(Math.Max(Math.Abs(cx), Math.Abs(cy)) / perReport);
        if (steps == 0) return;
        double ax = 0, ay = 0;
        int sx = 0, sy = 0;
        for (int i = 1; i <= steps; i++)
        {
            ax = cx * i / steps; ay = cy * i / steps;
            int nx = (int)Math.Round(ax) - sx, ny = (int)Math.Round(ay) - sy;
            sx += nx; sy += ny;
            if (nx != 0 || ny != 0) moves.Add((nx, ny));
        }
    }

    // ---------------- iPad side ----------------

    /// <summary>Feed every mouse motion sent to the iPad. Returns the Windows cursor position when control should come back.</summary>
    public PointD? OnIpadMouse(double dx, double dy, double t, bool buttonsDown)
    {
        if (!OnIpad) return null;
        Model.ApplyMouse(dx, dy, t);
        return CheckReturn(t, buttonsDown);
    }

    /// <summary>Same for single-finger trackpad motion in iPad-native mode (mm).</summary>
    public PointD? OnIpadTouch(double dxMm, double dyMm, double t, bool buttonsDown)
    {
        if (!OnIpad) return null;
        Model.ApplyTouchMm(dxMm, dyMm, t);
        return CheckReturn(t, buttonsDown);
    }

    private PointD? CheckReturn(double t, bool buttonsDown)
    {
        if (buttonsDown || t - _enteredAt < _s.EntryGrace) return null;
        double threshold = _s.Calibrated ? _s.ReturnThresholdCalibrated : _s.ReturnThresholdUncalibrated;
        if (Model.Overshoot(Layout.IpadSharedEdge) < threshold) return null;
        return Return();
    }

    /// <summary>Come back to the PC now (hotkey or push). Returns where to put the Windows cursor.</summary>
    public PointD Return()
    {
        OnIpad = false;
        _push = 0;
        var p = Model.Position;
        double along = Layout.Side.IsHorizontalNeighbour() ? p.Y : p.X;
        Model.PinTo(Layout.IpadSharedEdge);
        return Layout.ReturnPoint(along);
    }

    /// <summary>Drop to PC mode without computing a return point (lock screen, link lost).</summary>
    public void Abort()
    {
        OnIpad = false;
        _push = 0;
    }
}
