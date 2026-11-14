using IpadShare.Core.Handoff;
using IpadShare.Core.Touch;

namespace IpadShare.Core.Tests;

public class PointerShaperTests
{
    [Fact]
    public void Inverts_the_ipad_curve_so_points_match_intent()
    {
        var sh = new PointerShaper();
        foreach (var (mm, dt) in new[] { (0.1, 0.008), (0.5, 0.008), (2.0, 0.008), (6.0, 0.01) })
        {
            var (cx, _) = sh.Shape(mm, 0, dt);
            double moved = cx * GainCurve.DefaultMouse.Evaluate(cx / dt);
            Assert.Equal(sh.IntendedPoints(mm, dt), moved, 2);
        }
    }

    [Fact]
    public void Slow_is_precise_and_a_flick_crosses_the_ipad()
    {
        var sh = new PointerShaper();
        double slow = 0, fast = 0;
        for (int i = 0; i < 100; i++) slow += sh.IntendedPoints(0.04, 0.008);  // 4 mm at 5 mm/s
        for (int i = 0; i < 25; i++) fast += sh.IntendedPoints(2.4, 0.008);    // 60 mm at 300 mm/s
        Assert.InRange(slow, 8, 20);         // ~3 pt per mm: pixel-precise
        Assert.True(fast > 1180, $"flick moved {fast} pt");  // one swipe crosses the iPad Air
    }

    [Fact]
    public void Direction_is_preserved()
    {
        var (x, y) = new PointerShaper().Shape(3, -4, 0.008);
        Assert.True(x > 0 && y < 0);
        Assert.Equal(-4.0 / 3.0, y / x, 6);
    }

    [Fact]
    public void Engine_uses_the_shaper_when_set()
    {
        var sink = new RecordingSink();
        var e = new GestureEngine(new GestureSettings { Shaper = new PointerShaper() }, sink);
        double t = 1;
        for (int i = 0; i <= 10; i++, t += 0.008)
            e.Process(new TouchFrame(t, new[] { new TouchPoint(1, 20 + i * 2.0, 30, true, true) }, false));
        Assert.True(sink.Dx > 0);
        Assert.NotEqual(20 * 20.0, sink.Dx, 0);   // not the old linear 20 counts/mm
    }
}

public class ShakeDetectorTests
{
    private static bool Shake(ShakeDetector d, double t0, int strokes, double length, double strokeTime)
    {
        double t = t0;
        int dir = 1;
        bool fired = false;
        for (int s = 0; s < strokes; s++)
        {
            for (int i = 0; i < 5; i++) { t += strokeTime / 5; fired |= d.Add(dir * length / 5, 0, t); }
            dir = -dir;
        }
        fired |= d.Add(dir * 10, 0, t + 0.01);
        return fired;
    }

    [Fact]
    public void Quick_left_right_shake_fires()
    {
        Assert.True(Shake(new ShakeDetector(), 0, 4, 150, 0.08));
    }

    [Fact]
    public void Slow_or_small_movements_do_not_fire()
    {
        Assert.False(Shake(new ShakeDetector(), 0, 4, 150, 0.6));   // too slow
        Assert.False(Shake(new ShakeDetector(), 0, 4, 20, 0.05));   // too small
        Assert.False(Shake(new ShakeDetector(), 0, 2, 150, 0.08));  // not enough reversals
    }

    [Fact]
    public void Normal_pointing_does_not_fire()
    {
        var d = new ShakeDetector();
        double t = 0;
        bool fired = false;
        // move right across the screen, pause, come back, pause, go right again
        for (int i = 0; i < 60; i++) fired |= d.Add(15, 2, t += 0.008);
        t += 0.4;
        for (int i = 0; i < 60; i++) fired |= d.Add(-15, -1, t += 0.008);
        t += 0.4;
        for (int i = 0; i < 60; i++) fired |= d.Add(15, 0, t += 0.008);
        Assert.False(fired);
    }

    [Fact]
    public void Cooldown_prevents_double_switching()
    {
        var d = new ShakeDetector();
        Assert.True(Shake(d, 0, 4, 150, 0.08));
        Assert.False(Shake(d, 0.45, 4, 150, 0.08));
        Assert.True(Shake(d, 2.0, 4, 150, 0.08));
    }

    [Fact]
    public void Vertical_shake_is_ignored()
    {
        var d = new ShakeDetector();
        double t = 0; int dir = 1; bool fired = false;
        for (int s = 0; s < 5; s++) { for (int i = 0; i < 5; i++) fired |= d.Add(0, dir * 30, t += 0.016); dir = -dir; }
        Assert.False(fired);
    }
}
