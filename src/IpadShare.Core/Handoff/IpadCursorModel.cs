namespace IpadShare.Core.Handoff;

/// <summary>
/// Dead-reckoning model of the iPad pointer. The laptop can't read the iPad's pointer position,
/// so it integrates every motion it sends through a model of iPadOS acceleration, clamps to the
/// screen like iPadOS does, and tracks how far the user keeps pushing past each edge
/// ("overshoot"), which is what triggers the return to Windows.
/// </summary>
public sealed class IpadCursorModel
{
    private double _speedEma;
    private double _lastTime = double.NaN;
    private readonly double[] _overshoot = new double[4];
    private readonly double[] _lastPush = new double[4];

    public IpadCursorModel(SizeD screenPoints, GainCurve mouseCurve, GainCurve touchCurve)
    {
        Screen = screenPoints;
        MouseCurve = mouseCurve;
        TouchCurve = touchCurve;
        Position = new PointD(screenPoints.Width / 2, screenPoints.Height / 2);
    }

    public SizeD Screen { get; set; }
    public GainCurve MouseCurve { get; set; }
    public GainCurve TouchCurve { get; set; }
    public PointD Position { get; private set; }
    /// <summary>
    /// Overshoot older than this (s) without further pushing is forgotten. Long enough to lift and
    /// re-place a finger on the trackpad between swipes.
    /// </summary>
    public double PushMemory { get; set; } = 1.2;

    public void Place(PointD p)
    {
        Position = new PointD(Math.Clamp(p.X, 0, Screen.Width), Math.Clamp(p.Y, 0, Screen.Height));
        Array.Clear(_overshoot);
    }

    /// <summary>The pointer was pushed hard against an edge: its coordinate on that axis is now exact.</summary>
    public void PinTo(EdgeSide edge)
    {
        Position = edge switch
        {
            EdgeSide.Left => Position with { X = 0 },
            EdgeSide.Right => Position with { X = Screen.Width },
            EdgeSide.Top => Position with { Y = 0 },
            _ => Position with { Y = Screen.Height },
        };
        Array.Clear(_overshoot);
    }

    public double Overshoot(EdgeSide edge) => _overshoot[(int)edge];

    public void ClearOvershoot() => Array.Clear(_overshoot);

    /// <summary>Apply relative mouse counts sent at time t (seconds). Returns the point delta applied.</summary>
    public PointD ApplyMouse(double dx, double dy, double t) => ApplyScaled(dx, dy, t, MouseCurve);

    /// <summary>Apply trackpad finger motion (mm) for iPad-native mode.</summary>
    public PointD ApplyTouchMm(double dxMm, double dyMm, double t) => ApplyScaled(dxMm, dyMm, t, TouchCurve);

    /// <summary>Apply a motion already expressed in iPad points (e.g. a planned move).</summary>
    public void ApplyPoints(double dxPts, double dyPts, double t) => Integrate(dxPts, dyPts, t);

    private PointD ApplyScaled(double dx, double dy, double t, GainCurve curve)
    {
        double mag = Math.Sqrt(dx * dx + dy * dy);
        if (mag == 0) return default;
        double dt = double.IsNaN(_lastTime) ? 0.008 : Math.Clamp(t - _lastTime, 0.001, 0.05);
        double speed = mag / dt;
        _speedEma = double.IsNaN(_lastTime) || t - _lastTime > 0.1 ? speed : 0.5 * _speedEma + 0.5 * speed;
        double g = curve.Evaluate(_speedEma);
        Integrate(dx * g, dy * g, t);
        return new PointD(dx * g, dy * g);
    }

    private void Integrate(double dxPts, double dyPts, double t)
    {
        _lastTime = t;
        double nx = Position.X + dxPts, ny = Position.Y + dyPts;

        Track(EdgeSide.Left, nx < 0 ? -nx : 0, dxPts > 0 ? dxPts : 0, t);
        Track(EdgeSide.Right, nx > Screen.Width ? nx - Screen.Width : 0, dxPts < 0 ? -dxPts : 0, t);
        Track(EdgeSide.Top, ny < 0 ? -ny : 0, dyPts > 0 ? dyPts : 0, t);
        Track(EdgeSide.Bottom, ny > Screen.Height ? ny - Screen.Height : 0, dyPts < 0 ? -dyPts : 0, t);

        Position = new PointD(Math.Clamp(nx, 0, Screen.Width), Math.Clamp(ny, 0, Screen.Height));
    }

    private void Track(EdgeSide e, double beyond, double away, double t)
    {
        int i = (int)e;
        if (t - _lastPush[i] > PushMemory) _overshoot[i] = 0;
        if (beyond > 0)
        {
            _overshoot[i] += beyond;
            _lastPush[i] = t;
        }
        else if (away > 0)
        {
            _overshoot[i] = Math.Max(0, _overshoot[i] - away * 2);
        }
    }
}
