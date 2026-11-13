namespace IpadShare.Core.Handoff;

/// <summary>
/// A monotone piecewise-linear curve: gain (iPad points per input unit) as a function of input
/// speed (units per second). Models iPadOS pointer acceleration.
/// </summary>
public sealed class GainCurve
{
    private readonly double[] _speed;
    private readonly double[] _gain;

    public GainCurve(IEnumerable<(double speed, double gain)> points)
    {
        var p = points.Where(x => x.speed >= 0 && x.gain > 0 && double.IsFinite(x.speed) && double.IsFinite(x.gain))
                      .OrderBy(x => x.speed).ToArray();
        if (p.Length == 0) throw new ArgumentException("curve needs at least one point");
        _speed = p.Select(x => x.speed).ToArray();
        _gain = p.Select(x => x.gain).ToArray();
    }

    public IReadOnlyList<(double Speed, double Gain)> Points => _speed.Zip(_gain).ToArray();

    public double Evaluate(double speed)
    {
        if (speed <= _speed[0]) return _gain[0];
        int last = _speed.Length - 1;
        if (speed >= _speed[last]) return _gain[last];
        int i = Array.BinarySearch(_speed, speed);
        if (i >= 0) return _gain[i];
        i = ~i;
        double t = (speed - _speed[i - 1]) / (_speed[i] - _speed[i - 1]);
        return _gain[i - 1] + t * (_gain[i] - _gain[i - 1]);
    }

    /// <summary>
    /// Uncalibrated guess for iPadOS mouse acceleration at the default tracking speed
    /// (points per count vs counts/s). Deliberately conservative; edge re-sync covers the error.
    /// </summary>
    public static GainCurve DefaultMouse { get; } = new(new[]
    {
        (50.0, 0.30), (200.0, 0.45), (600.0, 0.70), (1500.0, 1.05), (4000.0, 1.60), (10000.0, 2.20),
    });

    /// <summary>Uncalibrated guess for iPadOS trackpad acceleration (points per mm vs mm/s).</summary>
    public static GainCurve DefaultTouch { get; } = new(new[]
    {
        (5.0, 3.0), (30.0, 5.0), (100.0, 9.0), (250.0, 15.0), (600.0, 24.0),
    });

    /// <summary>Isotonic (non-decreasing) least-squares fit through measured samples (pool-adjacent-violators).</summary>
    public static GainCurve FitMonotone(IEnumerable<(double speed, double gain)> samples, int maxPoints = 12)
    {
        var s = samples.Where(x => x.gain > 0 && x.speed > 0 && double.IsFinite(x.gain)).OrderBy(x => x.speed).ToList();
        if (s.Count == 0) throw new ArgumentException("no samples");

        // PAV: blocks of (sumGain, weight, sumSpeed)
        var blocks = new List<(double g, double w, double sp)>();
        foreach (var (sp, g) in s)
        {
            blocks.Add((g, 1, sp));
            while (blocks.Count > 1 && blocks[^2].g / blocks[^2].w > blocks[^1].g / blocks[^1].w)
            {
                var b = blocks[^1]; var a = blocks[^2];
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (a.g + b.g, a.w + b.w, a.sp + b.sp);
            }
        }
        var pts = blocks.Select(b => (speed: b.sp / b.w, gain: b.g / b.w)).ToList();
        while (pts.Count > maxPoints)
        {
            // merge the closest pair in log-speed
            int best = 0; double bestGap = double.MaxValue;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double gap = Math.Log(pts[i + 1].speed) - Math.Log(pts[i].speed);
                if (gap < bestGap) { bestGap = gap; best = i; }
            }
            var m = ((pts[best].speed + pts[best + 1].speed) / 2, (pts[best].gain + pts[best + 1].gain) / 2);
            pts.RemoveAt(best + 1);
            pts[best] = m;
        }
        return new GainCurve(pts);
    }
}
