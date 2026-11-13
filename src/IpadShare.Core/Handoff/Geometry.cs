namespace IpadShare.Core.Handoff;

public readonly record struct PointD(double X, double Y);
public readonly record struct SizeD(double Width, double Height)
{
    public SizeD Rotated => new(Height, Width);
}

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>Which edge of a screen.</summary>
public enum EdgeSide { Left, Right, Top, Bottom }

public static class EdgeSideExtensions
{
    public static EdgeSide Opposite(this EdgeSide s) => s switch
    {
        EdgeSide.Left => EdgeSide.Right,
        EdgeSide.Right => EdgeSide.Left,
        EdgeSide.Top => EdgeSide.Bottom,
        _ => EdgeSide.Top,
    };

    public static bool IsHorizontalNeighbour(this EdgeSide s) => s is EdgeSide.Left or EdgeSide.Right;
}

/// <summary>An iPad model's screen: logical points (landscape) and physical size.</summary>
public sealed record IpadModel(string Name, SizeD LandscapePoints, SizeD LandscapeMm)
{
    public static readonly IpadModel IpadAirM1 = new("iPad Air (M1, 10.9\")", new SizeD(1180, 820), new SizeD(227, 158));

    public static readonly IReadOnlyList<IpadModel> Presets = new[]
    {
        IpadAirM1,
        new IpadModel("iPad (10th gen, 10.9\")", new SizeD(1180, 820), new SizeD(227, 158)),
        new IpadModel("iPad Air 11\" (M2/M3)", new SizeD(1180, 820), new SizeD(227, 158)),
        new IpadModel("iPad Air 13\" (M2/M3)", new SizeD(1366, 1024), new SizeD(262, 197)),
        new IpadModel("iPad Pro 11\"", new SizeD(1194, 834), new SizeD(229, 160)),
        new IpadModel("iPad Pro 13\" (M4)", new SizeD(1376, 1032), new SizeD(264, 198)),
        new IpadModel("iPad (9th gen, 10.2\")", new SizeD(1080, 810), new SizeD(207, 155)),
        new IpadModel("iPad mini (6th gen+)", new SizeD(1133, 744), new SizeD(178, 117)),
    };

    public static IpadModel ByName(string? name) => Presets.FirstOrDefault(p => p.Name == name) ?? IpadAirM1;
}
