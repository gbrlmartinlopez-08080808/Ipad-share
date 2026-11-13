using IpadShare.Core.Calibration;
using IpadShare.Core.Handoff;

namespace IpadShare.Core.Tests;

public class HandoffTests
{
    // Laptop 1920x1200 (physical px), 301.6 x 188.5 mm panel; iPad Air landscape on the right, centred.
    private static readonly RectD Monitor = new(0, 0, 1920, 1200);
    private static readonly SizeD MonitorMm = new(301.6, 188.5);

    private static ScreenLayout RightLayout(double? offset = null) =>
        ScreenLayout.FromArrangement(Monitor, MonitorMm, EdgeSide.Right,
            offset ?? ScreenLayout.CenteredOffset(Monitor, MonitorMm, EdgeSide.Right, IpadModel.IpadAirM1, false),
            IpadModel.IpadAirM1, portrait: false);

    private static HandoffController Controller(ScreenLayout l, HandoffSettings? s = null)
    {
        var model = new IpadCursorModel(l.IpadPoints, GainCurve.DefaultMouse, GainCurve.DefaultTouch);
        return new HandoffController(l, s ?? new HandoffSettings(), model);
    }

    [Fact]
    public void Ipad_is_drawn_at_physical_size()
    {
        var l = RightLayout();
        Assert.Equal(158 / 188.5 * 1200, l.IpadLengthPx, 1);
        var (s, e) = l.Overlap;
        Assert.Equal(l.IpadStart, s, 6);
        Assert.Equal(l.IpadEnd, e, 6);
        Assert.Equal(600, (l.IpadStart + l.IpadEnd) / 2, 1);   // centred
    }

    [Fact]
    public void Crossing_requires_a_deliberate_push()
    {
        var c = Controller(RightLayout());
        double t = 0;
        // Reaching the edge alone doesn't cross.
        Assert.Null(c.OnPcPointer(1919, 600, 10, 0, t, false));
        for (int i = 0; i < 4; i++) Assert.Null(c.OnPcPointer(1919, 600, 10, 0, t += 0.008, false));
        Assert.InRange(c.PushProgress, 0.6, 0.99);
        var plan = c.OnPcPointer(1919, 600, 15, 0, t += 0.008, false);
        Assert.NotNull(plan);
        Assert.True(c.OnIpad);
    }

    [Fact]
    public void Push_resets_after_a_pause_and_off_edge()
    {
        var c = Controller(RightLayout());
        c.OnPcPointer(1919, 600, 50, 0, 0, false);
        Assert.Null(c.OnPcPointer(1919, 600, 20, 0, 0.5, false));   // paused too long: starts again
        Assert.InRange(c.PushProgress, 0.2, 0.5);
        c.OnPcPointer(1800, 600, 20, 0, 0.51, false);                // left the edge
        Assert.Equal(0, c.PushProgress);
    }

    [Fact]
    public void Corners_buttons_and_outside_segment_never_cross()
    {
        var c = Controller(RightLayout());
        for (int i = 0; i < 20; i++)
        {
            Assert.Null(c.OnPcPointer(1919, 5, 30, 0, i * 0.008, false));        // corner guard / outside iPad
            Assert.Null(c.OnPcPointer(1919, 600, 30, 0, i * 0.008, true));       // dragging
            Assert.Null(c.OnPcPointer(1919, 1195, 30, 0, i * 0.008, false));
        }
        Assert.False(c.OnIpad);
    }

    [Fact]
    public void Disabled_sharing_never_crosses()
    {
        var c = Controller(RightLayout(), new HandoffSettings { Enabled = false });
        for (int i = 0; i < 20; i++) Assert.Null(c.OnPcPointer(1919, 600, 30, 0, i * 0.008, false));
    }

    [Fact]
    public void Entry_snaps_to_the_shared_edge_and_lines_up_vertically()
    {
        var l = RightLayout();
        var c = Controller(l);
        var plan = c.Enter(l.IpadStart + l.IpadLengthPx * 0.25, 0);
        Assert.True(plan.Moves.Take(4).All(m => m.dx <= -3000 && m.dy == 0));
        Assert.Equal(0, plan.IpadPosition.X, 6);
        Assert.Equal(820 * 0.25, plan.IpadPosition.Y, 1);
        // Remaining moves go up (model started centred at 410)
        Assert.True(plan.Moves.Skip(4).Sum(m => m.dy) < 0);
        Assert.All(plan.Moves.Skip(4), m => Assert.InRange(Math.Abs(m.dy), 0, 40));
    }

    [Fact]
    public void Return_needs_push_past_shared_edge_and_lands_at_matching_height()
    {
        var l = RightLayout();
        var c = Controller(l);
        c.Enter(600, 0);
        double t = 1.0;
        // Move right into the iPad, then back left to the edge.
        for (int i = 0; i < 30; i++) Assert.Null(c.OnIpadMouse(20, 0, t += 0.01, false));
        for (int i = 0; i < 33; i++) Assert.Null(c.OnIpadMouse(-20, 0, t += 0.01, false));
        Assert.Equal(0, c.Model.Position.X, 6);     // pinned at the shared edge, not yet returned
        Assert.True(c.OnIpad);
        PointD? back = null;
        for (int i = 0; i < 100 && back == null; i++) back = c.OnIpadMouse(-20, 0, t += 0.01, false);
        Assert.NotNull(back);
        Assert.False(c.OnIpad);
        Assert.Equal(1918, back!.Value.X, 6);
        Assert.Equal(600, back.Value.Y, 2);
    }

    [Fact]
    public void Return_is_blocked_while_dragging_and_during_entry_grace()
    {
        var c = Controller(RightLayout());
        c.Enter(600, 0);
        for (int i = 0; i < 50; i++) Assert.Null(c.OnIpadMouse(-50, 0, 0.001 * i, false)); // grace period
        for (int i = 0; i < 50; i++) Assert.Null(c.OnIpadMouse(-50, 0, 1 + 0.01 * i, true)); // dragging
        Assert.True(c.OnIpad);
    }

    [Fact]
    public void Model_clamps_like_ipados_and_overshoot_decays()
    {
        var m = new IpadCursorModel(new SizeD(1180, 820), new GainCurve(new[] { (1.0, 1.0) }), GainCurve.DefaultTouch);
        m.Place(new PointD(10, 10));
        m.ApplyMouse(-30, 0, 0.01);
        Assert.Equal(0, m.Position.X, 6);
        Assert.Equal(20, m.Overshoot(EdgeSide.Left), 6);
        m.ApplyMouse(-5, 0, 0.02);
        Assert.Equal(25, m.Overshoot(EdgeSide.Left), 6);
        m.ApplyMouse(-1, 0, 1.0);                     // long pause: old push forgotten
        Assert.Equal(1, m.Overshoot(EdgeSide.Left), 6);
        m.ApplyMouse(10, 0, 1.01);                    // moving away cancels
        Assert.Equal(0, m.Overshoot(EdgeSide.Left), 6);
    }

    [Fact]
    public void Gain_curve_interpolates_and_clamps()
    {
        var g = new GainCurve(new[] { (100.0, 1.0), (300.0, 2.0) });
        Assert.Equal(1.0, g.Evaluate(10));
        Assert.Equal(1.5, g.Evaluate(200), 6);
        Assert.Equal(2.0, g.Evaluate(1000));
    }

    [Fact]
    public void Monotone_fit_removes_noise_inversions()
    {
        var fit = GainCurve.FitMonotone(new[] { (100.0, 1.0), (200.0, 1.4), (300.0, 1.2), (400.0, 2.0) });
        var pts = fit.Points;
        for (int i = 1; i < pts.Count; i++) Assert.True(pts[i].Gain >= pts[i - 1].Gain);
        Assert.Equal(1.3, fit.Evaluate(250), 6);
    }

    [Fact]
    public void Calibration_recovers_a_known_acceleration_curve()
    {
        // Pretend iPadOS uses this curve; simulate the measurement exactly.
        var truth = new GainCurve(new[] { (80.0, 0.25), (500.0, 0.55), (2000.0, 1.2), (5000.0, 1.9) });
        var strokes = CalibrationPlanner.MouseStrokes(1180, GainCurve.DefaultMouse);
        var samples = new List<CalibrationSample>();
        foreach (var s in strokes)
        {
            double speed = s.UnitsPerReport / CalibrationPlanner.ReportInterval;
            double moved = s.TotalUnits * truth.Evaluate(speed);
            double start = 590;
            var sample = CalibrationPlanner.Evaluate(s, start, start + moved * s.Direction, 1180);
            Assert.NotNull(sample);
            samples.Add(sample!);
        }
        var fit = CalibrationPlanner.Fit(samples)!;
        foreach (var speed in new[] { 100.0, 400.0, 1500.0, 4000.0 })
            Assert.Equal(truth.Evaluate(speed), fit.Evaluate(speed), 2);
    }

    [Fact]
    public void Calibration_rejects_clamped_or_missing_measurements()
    {
        var s = new CalibrationStroke(5, 10, +1);
        Assert.Null(CalibrationPlanner.Evaluate(s, 500, 500, 1180));
        Assert.Null(CalibrationPlanner.Evaluate(s, 500, 1180, 1180));
        Assert.Null(CalibrationPlanner.Evaluate(s, 500, 400, 1180));
        Assert.Null(CalibrationPlanner.Fit(new[] { new CalibrationSample(100, 1), new CalibrationSample(200, 1) }));
    }

    [Theory]
    [InlineData(EdgeSide.Left)]
    [InlineData(EdgeSide.Top)]
    [InlineData(EdgeSide.Bottom)]
    public void Other_sides_cross_and_return(EdgeSide side)
    {
        var l = ScreenLayout.FromArrangement(Monitor, MonitorMm, side,
            ScreenLayout.CenteredOffset(Monitor, MonitorMm, side, IpadModel.IpadAirM1, false), IpadModel.IpadAirM1, false);
        var c = Controller(l);
        double x = side switch { EdgeSide.Left => 0, EdgeSide.Right => 1919, _ => 960 };
        double y = side switch { EdgeSide.Top => 0, EdgeSide.Bottom => 1199, _ => 600 };
        double dx = side == EdgeSide.Left ? -20 : side == EdgeSide.Right ? 20 : 0;
        double dy = side == EdgeSide.Top ? -20 : side == EdgeSide.Bottom ? 20 : 0;
        EntryPlan? plan = null;
        double t = 0;
        for (int i = 0; i < 10 && plan == null; i++) plan = c.OnPcPointer(x, y, dx, dy, t += 0.008, false);
        Assert.NotNull(plan);
        if (!side.IsHorizontalNeighbour()) Assert.DoesNotContain(plan!.Moves, m => Math.Abs(m.dx) >= 3000 || Math.Abs(m.dy) >= 3000);

        PointD? back = null;
        t = 2;
        for (int i = 0; i < 400 && back == null; i++) back = c.OnIpadMouse(-dx, -dy, t += 0.01, false);
        Assert.NotNull(back);
        Assert.True(Monitor.Contains(back!.Value.X, back.Value.Y));
    }

    [Fact]
    public void Ipad_taller_than_overlap_returns_clamped_onto_the_monitor()
    {
        var l = RightLayout(offset: 0.8);   // iPad mostly below the laptop screen
        var c = Controller(l);
        c.Enter(l.CrossingSegment.Start + 1, 0);
        c.Model.Place(new PointD(0, 800));  // pointer near the iPad bottom (faces nothing)
        var p = c.Return();
        Assert.InRange(p.Y, 0, 1199);
    }
}
