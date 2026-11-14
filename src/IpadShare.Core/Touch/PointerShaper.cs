using IpadShare.Core.Handoff;

namespace IpadShare.Core.Touch;

/// <summary>
/// Turns finger motion on the laptop touchpad into mouse counts for the iPad so the pointer feels like
/// a Mac trackpad: precise when moving slowly, fast enough to cross the screen with one flick.
/// <para>
/// The desired motion is defined in iPad screen points (<see cref="TrackpadCurve"/>, points per mm vs.
/// finger speed). iPadOS then applies its own mouse acceleration to whatever counts we send, so the
/// counts are found by inverting that curve (<see cref="IpadMouseCurve"/>, points per count vs.
/// counts per second) — measured exactly by calibration, or a default estimate before that.
/// </para>
/// </summary>
public sealed class PointerShaper
{
    /// <summary>Mac-like trackpad response: iPad points per mm of finger travel vs. finger speed (mm/s).</summary>
    public static GainCurve DefaultTrackpadCurve { get; } = new(new[]
    {
        (5.0, 3.2), (25.0, 4.6), (60.0, 7.2), (120.0, 11.5), (250.0, 19.0), (500.0, 27.0), (900.0, 34.0),
    });

    public GainCurve TrackpadCurve { get; set; } = DefaultTrackpadCurve;
    public GainCurve IpadMouseCurve { get; set; } = GainCurve.DefaultMouse;
    /// <summary>User speed multiplier (0.25 … 3).</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>iPad points the user intends for this finger motion.</summary>
    public double IntendedPoints(double mm, double dt)
    {
        if (mm <= 0) return 0;
        double v = mm / Math.Clamp(dt, 0.002, 0.05);
        return mm * TrackpadCurve.Evaluate(v) * Speed;
    }

    /// <summary>Mouse counts (fractional) to send for a finger motion of (dx, dy) mm over dt seconds.</summary>
    public (double Dx, double Dy) Shape(double dxMm, double dyMm, double dt)
    {
        double mm = Math.Sqrt(dxMm * dxMm + dyMm * dyMm);
        if (mm == 0) return (0, 0);
        dt = Math.Clamp(dt, 0.002, 0.05);
        double counts = CountsFor(IntendedPoints(mm, dt), dt);
        double k = counts / mm;
        return (dxMm * k, dyMm * k);
    }

    /// <summary>
    /// Counts c such that iPadOS moves the pointer <paramref name="points"/>: c · G(c/dt) = points.
    /// The left side grows monotonically with c, so bisection converges reliably.
    /// </summary>
    public double CountsFor(double points, double dt)
    {
        if (points <= 0) return 0;
        double gMin = Math.Max(1e-4, IpadMouseCurve.Evaluate(0));
        double lo = 0, hi = points / gMin;
        for (int i = 0; i < 40; i++)
        {
            double mid = 0.5 * (lo + hi);
            double moved = mid * IpadMouseCurve.Evaluate(mid / dt);
            if (moved < points) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }
}
