using IpadShare.Core.Handoff;

namespace IpadShare.Core.Calibration;

/// <summary>One controlled stroke: <see cref="Reports"/> reports of <see cref="UnitsPerReport"/>, spaced by the plan interval.</summary>
public sealed record CalibrationStroke(double UnitsPerReport, int Reports, int Direction)
{
    public double TotalUnits => UnitsPerReport * Reports;
}

public sealed record CalibrationSample(double Speed, double Gain);

/// <summary>
/// Plans and evaluates the one-time pointer calibration. The laptop sends controlled
/// horizontal strokes at increasing speeds while a Safari page on the iPad reports where the
/// pointer actually went; the fitted curve replaces the default iPadOS acceleration guess.
/// </summary>
public static class CalibrationPlanner
{
    /// <summary>Time between reports during a stroke (s).</summary>
    public const double ReportInterval = 0.012;

    /// <summary>Mouse strokes: counts/report from slow to fast, each in both directions.</summary>
    public static IReadOnlyList<CalibrationStroke> MouseStrokes(double viewportWidthPts, GainCurve prior)
    {
        var strokes = new List<CalibrationStroke>();
        double[] perReport = { 1, 2, 3, 5, 8, 12, 18, 27, 40, 60 };
        double budget = viewportWidthPts * 0.35;                // stay well inside the page
        foreach (var c in perReport)
        {
            double speed = c / ReportInterval;
            double expectedPerReport = c * prior.Evaluate(speed);
            int reports = (int)Math.Clamp(Math.Floor(budget / Math.Max(expectedPerReport, 0.01)), 3, 60);
            strokes.Add(new CalibrationStroke(c, reports, +1));
            strokes.Add(new CalibrationStroke(c, reports, -1));
        }
        return strokes;
    }

    /// <summary>Trackpad strokes for iPad-native mode: mm/report.</summary>
    public static IReadOnlyList<CalibrationStroke> TouchStrokes(double viewportWidthPts, GainCurve prior)
    {
        var strokes = new List<CalibrationStroke>();
        double[] mmPerReport = { 0.05, 0.1, 0.2, 0.4, 0.8, 1.4, 2.2 };
        double budget = viewportWidthPts * 0.35;
        foreach (var mm in mmPerReport)
        {
            double speed = mm / ReportInterval;
            double expected = mm * prior.Evaluate(speed);
            int reports = (int)Math.Clamp(Math.Floor(budget / Math.Max(expected, 0.01)), 3, 60);
            strokes.Add(new CalibrationStroke(mm, reports, +1));
            strokes.Add(new CalibrationStroke(mm, reports, -1));
        }
        return strokes;
    }

    /// <summary>Turns a measured displacement into a sample; null if the measurement is unusable.</summary>
    public static CalibrationSample? Evaluate(CalibrationStroke stroke, double startX, double endX, double viewportWidth)
    {
        double moved = (endX - startX) * stroke.Direction;
        if (moved <= 0.5) return null;                         // no movement seen (or wrong way)
        if (endX <= 1 || endX >= viewportWidth - 1) return null; // hit the page edge: clamped
        double gain = moved / stroke.TotalUnits;
        double speed = stroke.UnitsPerReport / ReportInterval;
        return new CalibrationSample(speed, gain);
    }

    /// <summary>Fit the final curve. Requires samples covering at least 4 distinct speeds.</summary>
    public static GainCurve? Fit(IEnumerable<CalibrationSample> samples)
    {
        var list = samples.ToList();
        if (list.Select(s => Math.Round(s.Speed)).Distinct().Count() < 4) return null;
        return GainCurve.FitMonotone(list.Select(s => (s.Speed, s.Gain)));
    }
}
