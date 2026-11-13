using IpadShare.Core.Hid;
using IpadShare.Core.Touch;

namespace IpadShare.Core.Tests;

internal sealed class RecordingSink : IGestureSink
{
    public double Dx, Dy, ScrollV, ScrollH;
    public int ScrollEvents;
    public readonly List<MouseButtons> ButtonStates = new();
    public readonly List<GestureKind> Gestures = new();
    public void Move(double dx, double dy) { Dx += dx; Dy += dy; }
    public void SetButtons(MouseButtons b) => ButtonStates.Add(b);
    public void Scroll(double v, double h) { ScrollV += v; ScrollH += h; ScrollEvents++; }
    public void Gesture(GestureKind k) => Gestures.Add(k);

    public int Clicks(MouseButtons b)
    {
        int n = 0;
        var prev = MouseButtons.None;
        foreach (var s in ButtonStates)
        {
            if ((prev & b) != 0 && (s & b) == 0) n++;
            prev = s;
        }
        return n;
    }
}

/// <summary>Scripted touchpad: produces frames at 125 Hz.</summary>
internal sealed class Pad
{
    public readonly GestureEngine Engine;
    public readonly RecordingSink Sink = new();
    public double T = 10.0;
    private readonly Dictionary<int, (double x, double y)> _f = new();
    public bool Button;
    public const double Dt = 0.008;

    public Pad(GestureSettings? s = null)
    {
        Engine = new GestureEngine(s ?? new GestureSettings(), Sink) { Geometry = new TouchpadGeometry(110, 70) };
    }

    public Pad Down(int id, double x, double y) { _f[id] = (x, y); return Frame(); }
    public Pad Up(int id) { _f.Remove(id); return Frame(); }
    public Pad UpAll() { _f.Clear(); return Frame(); }

    public Pad Frame()
    {
        var list = _f.Select(kv => new TouchPoint(kv.Key, kv.Value.x, kv.Value.y, true, true)).ToList();
        Engine.Process(new TouchFrame(T, list, Button));
        T += Dt;
        if (Engine.NeedsTick) Engine.Tick(T);
        return this;
    }

    /// <summary>Move the given fingers by (dx,dy) mm in total over `frames` frames.</summary>
    public Pad Move(double dx, double dy, int frames, params int[] ids)
    {
        for (int i = 0; i < frames; i++)
        {
            foreach (var id in ids) { var p = _f[id]; _f[id] = (p.x + dx / frames, p.y + dy / frames); }
            Frame();
        }
        return this;
    }

    public Pad Wait(double seconds)
    {
        double end = T + seconds;
        while (T < end)
        {
            if (_f.Count > 0) Frame();
            else { T += Dt; Engine.Tick(T); }
        }
        return this;
    }
}

public class GestureEngineTests
{
    [Fact]
    public void One_finger_moves_pointer_proportionally_to_millimetres()
    {
        var p = new Pad();
        p.Down(1, 50, 30).Move(10, -5, 10, 1).UpAll();
        Assert.Equal(10 * 20, p.Sink.Dx, 3);
        Assert.Equal(-5 * 20, p.Sink.Dy, 3);
        Assert.Empty(p.Sink.ButtonStates); // a move is not a tap
    }

    [Fact]
    public void Tap_clicks_after_the_drag_window()
    {
        var p = new Pad();
        p.Down(1, 50, 30).Frame().UpAll();
        Assert.Equal(0, p.Sink.Clicks(MouseButtons.Left));        // waits for possible tap-and-drag
        p.Wait(0.25);
        Assert.Equal(1, p.Sink.Clicks(MouseButtons.Left));
    }

    [Fact]
    public void Tap_without_tap_and_drag_clicks_immediately()
    {
        var p = new Pad(new GestureSettings { TapAndDrag = false });
        p.Down(1, 50, 30).Frame().UpAll();
        Assert.Equal(1, p.Sink.Clicks(MouseButtons.Left));
    }

    [Fact]
    public void Double_tap_gives_two_clicks()
    {
        var p = new Pad();
        p.Down(1, 50, 30).Frame().UpAll().Wait(0.05).Down(1, 50, 30).Frame().UpAll();
        Assert.Equal(2, p.Sink.Clicks(MouseButtons.Left));
        p.Wait(0.5);
        Assert.Equal(2, p.Sink.Clicks(MouseButtons.Left));
    }

    [Fact]
    public void Tap_then_touch_and_move_drags()
    {
        var p = new Pad();
        p.Down(1, 50, 30).Frame().UpAll().Wait(0.05).Down(1, 50, 30).Move(20, 0, 10, 1);
        Assert.Equal(MouseButtons.Left, p.Engine.Buttons);           // held during the drag
        p.UpAll();
        Assert.Equal(MouseButtons.None, p.Engine.Buttons);
        Assert.Equal(1, p.Sink.Clicks(MouseButtons.Left));           // one press…release, not a double click
    }

    [Fact]
    public void Two_finger_tap_is_right_click()
    {
        var p = new Pad();
        p.Down(1, 40, 30).Down(2, 60, 30).Frame().UpAll();
        Assert.Equal(1, p.Sink.Clicks(MouseButtons.Right));
        Assert.Equal(0, p.Sink.Clicks(MouseButtons.Left));
    }

    [Fact]
    public void Staggered_three_finger_tap_is_recognised()
    {
        var p = new Pad();
        p.Down(1, 40, 30).Down(2, 50, 30).Down(3, 60, 30).Frame().Up(3).Up(2).Up(1);
        Assert.Equal(new[] { GestureKind.ThreeFingerTap }, p.Sink.Gestures);
    }

    [Fact]
    public void Long_press_is_not_a_tap()
    {
        var p = new Pad();
        p.Down(1, 50, 30).Wait(0.5).UpAll().Wait(0.5);
        Assert.Empty(p.Sink.ButtonStates);
    }

    [Fact]
    public void Two_finger_scroll_follows_fingers_with_natural_sign()
    {
        var p = new Pad(new GestureSettings { Momentum = false });
        p.Down(1, 40, 50).Down(2, 60, 50).Move(0, -20, 20, 1, 2).UpAll();
        Assert.True(p.Sink.ScrollV > 3, $"scroll {p.Sink.ScrollV}");  // fingers up → wheel up
        Assert.Equal(0, p.Sink.ScrollH, 6);                          // axis-locked vertical
        Assert.Equal(0, p.Sink.Dx, 6);
        Assert.Empty(p.Sink.ButtonStates);
    }

    [Fact]
    public void Reverse_scroll_flips_direction()
    {
        var p = new Pad(new GestureSettings { Momentum = false, ReverseScroll = true });
        p.Down(1, 40, 50).Down(2, 60, 50).Move(0, -20, 20, 1, 2).UpAll();
        Assert.True(p.Sink.ScrollV < -3);
    }

    [Fact]
    public void Horizontal_scroll_uses_pan()
    {
        var p = new Pad(new GestureSettings { Momentum = false });
        p.Down(1, 40, 30).Down(2, 40, 45).Move(25, 0, 20, 1, 2).UpAll();
        Assert.True(p.Sink.ScrollH > 3);
        Assert.Equal(0, p.Sink.ScrollV, 6);
    }

    [Fact]
    public void Flick_continues_with_decaying_momentum_and_a_touch_stops_it()
    {
        var p = new Pad();
        p.Down(1, 40, 60).Down(2, 60, 60).Move(0, -30, 6, 1, 2).UpAll();
        double atLift = p.Sink.ScrollV;
        Assert.True(p.Engine.MomentumActive);
        p.Wait(0.2);
        double after = p.Sink.ScrollV;
        Assert.True(after > atLift + 1, $"momentum added {after - atLift}");
        p.Down(1, 50, 30);                                          // touch stops it
        Assert.False(p.Engine.MomentumActive);
        double stopped = p.Sink.ScrollV;
        p.Wait(0.2);
        Assert.Equal(stopped, p.Sink.ScrollV, 6);
    }

    [Fact]
    public void Momentum_eventually_stops_by_itself()
    {
        var p = new Pad();
        p.Down(1, 40, 60).Down(2, 60, 60).Move(0, -30, 6, 1, 2).UpAll();
        p.Wait(5);
        Assert.False(p.Engine.MomentumActive);
        Assert.False(p.Engine.NeedsTick);
    }

    [Fact]
    public void Slow_scroll_ending_at_rest_has_no_momentum()
    {
        var p = new Pad();
        p.Down(1, 40, 60).Down(2, 60, 60).Move(0, -10, 30, 1, 2).Wait(0.15).UpAll();
        Assert.False(p.Engine.MomentumActive);
    }

    [Fact]
    public void Pinch_out_and_in_zoom()
    {
        var p = new Pad();
        p.Down(1, 45, 35).Down(2, 55, 35).Move(-10, 0, 10, 1).Move(10, 0, 10, 2).UpAll();
        Assert.Contains(GestureKind.PinchOut, p.Sink.Gestures);
        Assert.DoesNotContain(GestureKind.PinchIn, p.Sink.Gestures);
        Assert.Equal(0, p.Sink.ScrollEvents);

        var q = new Pad();
        q.Down(1, 30, 35).Down(2, 70, 35).Move(12, 0, 10, 1).Move(-12, 0, 10, 2).UpAll();
        Assert.Contains(GestureKind.PinchIn, q.Sink.Gestures);
    }

    [Fact]
    public void Quick_three_finger_swipe_up_is_home_and_hold_is_app_switcher()
    {
        var p = new Pad();
        p.Down(1, 40, 50).Down(2, 50, 50).Down(3, 60, 50).Move(0, -15, 8, 1, 2, 3).UpAll();
        Assert.Equal(new[] { GestureKind.ThreeSwipeUp }, p.Sink.Gestures);

        var q = new Pad();
        q.Down(1, 40, 50).Down(2, 50, 50).Down(3, 60, 50).Move(0, -15, 8, 1, 2, 3).Wait(0.5).UpAll();
        Assert.Equal(new[] { GestureKind.ThreeSwipeUpHold }, q.Sink.Gestures);
    }

    [Theory]
    [InlineData(-15, 0, 3, GestureKind.ThreeSwipeLeft)]
    [InlineData(15, 0, 3, GestureKind.ThreeSwipeRight)]
    [InlineData(0, 15, 3, GestureKind.ThreeSwipeDown)]
    [InlineData(0, -15, 4, GestureKind.FourSwipeUp)]
    [InlineData(0, 15, 4, GestureKind.FourSwipeDown)]
    [InlineData(15, 0, 4, GestureKind.FourSwipeRight)]
    public void Swipes_fire_exactly_once(double dx, double dy, int fingers, GestureKind expected)
    {
        var p = new Pad();
        var ids = Enumerable.Range(1, fingers).ToArray();
        foreach (var id in ids) p.Down(id, 30 + id * 10, 40);
        p.Move(dx, dy, 8, ids).Move(dx, dy, 8, ids).UpAll();
        Assert.Equal(new[] { expected }, p.Sink.Gestures);
        Assert.Equal(0, p.Sink.ScrollEvents);
        Assert.Equal(0, p.Sink.Dx, 6);
    }

    [Fact]
    public void Physical_click_left_and_two_finger_click_right()
    {
        var p = new Pad();
        p.Down(1, 30, 30); p.Button = true; p.Frame(); p.Button = false; p.Frame(); p.UpAll().Wait(0.3);
        Assert.Equal(1, p.Sink.Clicks(MouseButtons.Left));

        var q = new Pad();
        q.Down(1, 30, 30).Down(2, 50, 30); q.Button = true; q.Frame(); q.Button = false; q.Frame(); q.UpAll().Wait(0.3);
        Assert.Equal(1, q.Sink.Clicks(MouseButtons.Right));
        Assert.Equal(0, q.Sink.Clicks(MouseButtons.Left));
    }

    [Fact]
    public void Click_in_bottom_right_zone_is_right_click()
    {
        var p = new Pad();
        p.Down(1, 100, 65); p.Button = true; p.Frame(); p.Button = false; p.Frame(); p.UpAll().Wait(0.3);
        Assert.Equal(1, p.Sink.Clicks(MouseButtons.Right));
    }

    [Fact]
    public void Click_and_drag_moves_with_button_held()
    {
        var p = new Pad();
        p.Down(1, 30, 30); p.Button = true; p.Frame();
        p.Move(20, 0, 10, 1);
        Assert.Equal(MouseButtons.Left, p.Engine.Buttons);
        Assert.True(p.Sink.Dx > 300);
        p.Button = false; p.Frame(); p.UpAll();
        Assert.Equal(MouseButtons.None, p.Engine.Buttons);
    }

    [Fact]
    public void Resting_thumb_does_not_turn_pointing_into_scrolling()
    {
        var p = new Pad();
        p.Down(9, 30, 65);                       // thumb resting in the bottom zone
        p.Down(1, 50, 30).Move(15, 0, 10, 1).UpAll();
        Assert.Equal(0, p.Sink.ScrollEvents);
        Assert.True(p.Sink.Dx > 250, $"pointer moved {p.Sink.Dx}");
    }

    [Fact]
    public void Palm_contacts_are_ignored()
    {
        var sink = new RecordingSink();
        var e = new GestureEngine(new GestureSettings(), sink);
        double t = 1;
        for (int i = 0; i < 10; i++, t += 0.008)
            e.Process(new TouchFrame(t, new[] { new TouchPoint(1, 50 + i, 30, true, false) }, false));
        e.Process(TouchFrame.Empty(t));
        e.Tick(t + 1);
        Assert.Equal(0, sink.Dx, 6);
        Assert.Empty(sink.ButtonStates);
    }

    [Fact]
    public void Adopted_finger_keeps_moving_without_tapping()
    {
        var sink = new RecordingSink();
        var e = new GestureEngine(new GestureSettings(), sink);
        e.Adopt(new TouchFrame(1.0, new[] { new TouchPoint(1, 50, 30, true, true) }, false));
        e.Process(new TouchFrame(1.008, new[] { new TouchPoint(1, 51, 30, true, true) }, false));
        e.Process(TouchFrame.Empty(1.016));
        e.Tick(2);
        Assert.Equal(20, sink.Dx, 3);
        Assert.Empty(sink.ButtonStates);
    }

    [Fact]
    public void Reset_releases_held_buttons()
    {
        var p = new Pad();
        p.Down(1, 30, 30); p.Button = true; p.Frame();
        p.Engine.Reset();
        Assert.Equal(MouseButtons.None, p.Sink.ButtonStates[^1]);
        Assert.Equal(MouseButtons.None, p.Engine.Buttons);
    }

    [Fact]
    public void Native_mapper_preserves_millimetres_and_reports_lifts_once()
    {
        var m = new NativeTouchMapper { Geometry = new TouchpadGeometry(110, 70) };
        var r1 = m.Map(new TouchFrame(1, new[] { new TouchPoint(42, 0, 0, true, true), new TouchPoint(7, 110, 70, true, false) }, false))!;
        Assert.Equal(2, r1.Contacts.Length);
        Assert.Equal((ushort)((150 - 110) / 2 * 20), r1.Contacts[0].X);   // centred
        Assert.Equal((ushort)((150 - 70) / 2 * 20), r1.Contacts[0].Y);
        Assert.Equal((ushort)((20 + 110) * 20), r1.Contacts[1].X);
        Assert.False(r1.Contacts[1].Confidence);                         // palm flag forwarded
        Assert.NotEqual(r1.Contacts[0].Id, r1.Contacts[1].Id);

        var r2 = m.Map(new TouchFrame(1.008, new[] { new TouchPoint(7, 100, 60, true, true) }, false))!;
        Assert.Equal(2, r2.Contacts.Length);
        Assert.Contains(r2.Contacts, c => !c.Tip && c.Id == r1.Contacts[0].Id);   // lift reported
        var r3 = m.Map(new TouchFrame(1.016, Array.Empty<TouchPoint>(), false))!;
        Assert.Single(r3.Contacts);
        Assert.False(r3.Contacts[0].Tip);
        Assert.Null(m.Map(new TouchFrame(1.024, Array.Empty<TouchPoint>(), false)));
    }

    [Fact]
    public void Native_mapper_lift_all()
    {
        var m = new NativeTouchMapper();
        m.Map(new TouchFrame(1, new[] { new TouchPoint(1, 10, 10, true, true), new TouchPoint(2, 20, 10, true, true) }, false));
        var r = m.LiftAll(1.1)!;
        Assert.Equal(2, r.Contacts.Length);
        Assert.All(r.Contacts, c => Assert.False(c.Tip));
        Assert.Null(m.LiftAll(1.2));
    }
}
