namespace IpadShare.Core.Handoff;

/// <summary>
/// Where the iPad sits relative to one of the PC's monitors, in the PC's physical-pixel
/// virtual-screen coordinates. "Along" means the coordinate parallel to the shared edge
/// (Y for left/right placement, X for top/bottom).
/// </summary>
public sealed class ScreenLayout
{
    public ScreenLayout(RectD pcMonitor, EdgeSide side, double ipadStart, double ipadLengthPx, SizeD ipadPoints,
        double cornerGuardFraction = 0.025)
    {
        if (pcMonitor.Width <= 0 || pcMonitor.Height <= 0) throw new ArgumentException("empty monitor");
        if (ipadLengthPx <= 0) throw new ArgumentException("iPad length must be positive");
        PcMonitor = pcMonitor;
        Side = side;
        IpadStart = ipadStart;
        IpadLengthPx = ipadLengthPx;
        IpadPoints = ipadPoints;
        CornerGuardFraction = Math.Clamp(cornerGuardFraction, 0, 0.2);
    }

    public RectD PcMonitor { get; }
    /// <summary>Edge of the PC monitor that the iPad touches.</summary>
    public EdgeSide Side { get; }
    /// <summary>Along-edge PC pixel coordinate where the iPad's edge begins.</summary>
    public double IpadStart { get; }
    /// <summary>Length of the iPad's shared edge, in PC pixels (physical-size scaled).</summary>
    public double IpadLengthPx { get; }
    public double IpadEnd => IpadStart + IpadLengthPx;
    /// <summary>iPad screen size in points for the current orientation.</summary>
    public SizeD IpadPoints { get; }
    public double CornerGuardFraction { get; }

    /// <summary>The iPad edge that faces the PC.</summary>
    public EdgeSide IpadSharedEdge => Side.Opposite();

    private double MonitorAlongStart => Side.IsHorizontalNeighbour() ? PcMonitor.Top : PcMonitor.Left;
    private double MonitorAlongEnd => Side.IsHorizontalNeighbour() ? PcMonitor.Bottom : PcMonitor.Right;
    private double IpadAlongPoints => Side.IsHorizontalNeighbour() ? IpadPoints.Height : IpadPoints.Width;

    /// <summary>The overlap of both edges (where crossing is geometrically possible).</summary>
    public (double Start, double End) Overlap =>
        (Math.Max(MonitorAlongStart, IpadStart), Math.Min(MonitorAlongEnd, IpadEnd));

    public bool HasOverlap => Overlap.End - Overlap.Start >= 8;

    /// <summary>Overlap minus the corner guards at the monitor's corners.</summary>
    public (double Start, double End) CrossingSegment
    {
        get
        {
            var (s, e) = Overlap;
            double guard = (MonitorAlongEnd - MonitorAlongStart) * CornerGuardFraction;
            s = Math.Max(s, MonitorAlongStart + guard);
            e = Math.Min(e, MonitorAlongEnd - guard);
            return (s, Math.Max(s, e));
        }
    }

    /// <summary>The last pixel row/column on the shared PC edge.</summary>
    public double PcEdgeCoordinate => Side switch
    {
        EdgeSide.Right => PcMonitor.Right - 1,
        EdgeSide.Left => PcMonitor.Left,
        EdgeSide.Bottom => PcMonitor.Bottom - 1,
        _ => PcMonitor.Top,
    };

    /// <summary>Is the cursor touching the shared edge inside the crossing segment?</summary>
    public bool IsOnCrossingEdge(double x, double y)
    {
        double perp = Side.IsHorizontalNeighbour() ? x : y;
        double along = Side.IsHorizontalNeighbour() ? y : x;
        bool atEdge = Side is EdgeSide.Right or EdgeSide.Bottom ? perp >= PcEdgeCoordinate - 0.5 : perp <= PcEdgeCoordinate + 0.5;
        var (s, e) = CrossingSegment;
        return atEdge && along >= s && along <= e;
    }

    /// <summary>Outward component of a motion vector (positive = toward the iPad).</summary>
    public double Outward(double dx, double dy) => Side switch
    {
        EdgeSide.Right => dx,
        EdgeSide.Left => -dx,
        EdgeSide.Bottom => dy,
        _ => -dy,
    };

    public double Along(double x, double y) => Side.IsHorizontalNeighbour() ? y : x;

    /// <summary>PC along-pixel → iPad along-points (clamped to the iPad screen).</summary>
    public double PcToIpadAlong(double pcAlong) =>
        Math.Clamp((pcAlong - IpadStart) / IpadLengthPx * IpadAlongPoints, 0, IpadAlongPoints);

    /// <summary>iPad along-points → PC along-pixel, clamped into the overlap so the cursor lands on screen.</summary>
    public double IpadToPcAlong(double ipadAlong)
    {
        double px = IpadStart + ipadAlong / IpadAlongPoints * IpadLengthPx;
        var (s, e) = Overlap;
        return Math.Clamp(px, s, Math.Max(s, e - 1));
    }

    /// <summary>Where the Windows cursor reappears when control returns from the iPad.</summary>
    public PointD ReturnPoint(double ipadAlong)
    {
        double along = IpadToPcAlong(ipadAlong);
        return Side switch
        {
            EdgeSide.Right => new PointD(PcMonitor.Right - 2, along),
            EdgeSide.Left => new PointD(PcMonitor.Left + 1, along),
            EdgeSide.Bottom => new PointD(along, PcMonitor.Bottom - 2),
            _ => new PointD(along, PcMonitor.Top + 1),
        };
    }

    /// <summary>
    /// Builds a layout from user arrangement settings: the iPad is drawn at real physical size relative
    /// to the monitor (using the monitor's physical size when known).
    /// </summary>
    public static ScreenLayout FromArrangement(RectD monitor, SizeD monitorMm, EdgeSide side, double offsetFraction,
        IpadModel model, bool portrait, double cornerGuardFraction = 0.025)
    {
        var pts = portrait ? model.LandscapePoints.Rotated : model.LandscapePoints;
        var mm = portrait ? model.LandscapeMm.Rotated : model.LandscapeMm;
        bool horiz = side.IsHorizontalNeighbour();
        double monAlongPx = horiz ? monitor.Height : monitor.Width;
        double monAlongMm = horiz ? monitorMm.Height : monitorMm.Width;
        double ipadAlongMm = horiz ? mm.Height : mm.Width;
        double lengthPx = monAlongMm > 1 ? ipadAlongMm / monAlongMm * monAlongPx : monAlongPx * 0.84;
        lengthPx = Math.Clamp(lengthPx, monAlongPx * 0.2, monAlongPx * 3);
        double start = (horiz ? monitor.Top : monitor.Left) + offsetFraction * monAlongPx;
        return new ScreenLayout(monitor, side, start, lengthPx, pts, cornerGuardFraction);
    }

    /// <summary>Offset fraction that centres the iPad on the monitor edge.</summary>
    public static double CenteredOffset(RectD monitor, SizeD monitorMm, EdgeSide side, IpadModel model, bool portrait)
    {
        var l = FromArrangement(monitor, monitorMm, side, 0, model, portrait);
        double monAlong = side.IsHorizontalNeighbour() ? monitor.Height : monitor.Width;
        return (monAlong - l.IpadLengthPx) / 2 / monAlong;
    }
}
