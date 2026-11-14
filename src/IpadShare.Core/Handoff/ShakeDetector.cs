namespace IpadShare.Core.Handoff;

/// <summary>
/// Detects a quick left-right shake of the pointer (used to jump between the laptop and the iPad).
/// A shake is <see cref="Reversals"/> direction changes between fast horizontal strokes, each at
/// least <see cref="MinStroke"/> long, all within <see cref="Window"/> seconds. Units are screen
/// pixels on Windows and points on the iPad — the thresholds work for both.
/// </summary>
public sealed class ShakeDetector
{
    private int _dir;
    private double _len, _strokeStart, _lastT = double.NegativeInfinity, _cooldownUntil = double.NegativeInfinity;
    private readonly List<double> _reversals = new();

    public double MinStroke { get; set; } = 60;
    public double MinStrokeSpeed { get; set; } = 600;
    public int Reversals { get; set; } = 3;
    public double Window { get; set; } = 0.8;
    public double Cooldown { get; set; } = 1.0;
    /// <summary>A pause longer than this breaks the shake.</summary>
    public double MaxPause { get; set; } = 0.25;
    public bool Enabled { get; set; } = true;

    public void Reset()
    {
        _dir = 0;
        _len = 0;
        _reversals.Clear();
    }

    /// <summary>Feed one motion event. Returns true exactly when a shake completes.</summary>
    public bool Add(double dx, double dy, double t)
    {
        if (!Enabled || t < _cooldownUntil) { _lastT = t; return false; }
        if (t - _lastT > MaxPause) Reset();
        _lastT = t;

        if (Math.Abs(dx) < Math.Abs(dy) * 0.7)
        {
            if (Math.Abs(dy) > MinStroke * 0.5) Reset();   // clearly vertical movement: not a shake
            return false;
        }
        if (dx == 0) return false;

        int d = Math.Sign(dx);
        if (_dir == 0 || d == _dir)
        {
            if (_dir == 0) { _dir = d; _strokeStart = t; _len = 0; }
            _len += Math.Abs(dx);
            return false;
        }

        // Direction changed: judge the stroke that just ended.
        double duration = Math.Max(t - _strokeStart, 1e-3);
        bool valid = _len >= MinStroke && _len / duration >= MinStrokeSpeed;
        if (valid)
        {
            _reversals.Add(t);
            _reversals.RemoveAll(r => t - r > Window);
            if (_reversals.Count >= Reversals)
            {
                Reset();
                _cooldownUntil = t + Cooldown;
                return true;
            }
        }
        else _reversals.Clear();

        _dir = d;
        _len = Math.Abs(dx);
        _strokeStart = t;
        return false;
    }
}
