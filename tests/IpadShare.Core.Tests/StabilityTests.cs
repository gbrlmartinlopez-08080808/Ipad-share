using IpadShare.Core.Hid;
using IpadShare.Core.Transport;

namespace IpadShare.Core.Tests;

public class LinkStateTrackerTests
{
    [Fact]
    public void Connects_immediately_and_survives_short_subscription_gaps()
    {
        var l = new LinkStateTracker { Grace = 3 };
        Assert.True(l.Update(true, false, 0));
        Assert.True(l.Connected);
        // iOS drops and re-adds subscriptions for a moment: no disconnect
        Assert.False(l.Update(false, false, 1.0));
        Assert.True(l.Wavering);
        Assert.False(l.Update(false, false, 2.5));
        Assert.False(l.Update(true, false, 2.9));
        Assert.True(l.Connected);
        Assert.Equal(1, l.AbsorbedBlips);
        Assert.Equal(1, l.Connects);
        Assert.Equal(0, l.Disconnects);
    }

    [Fact]
    public void Disconnects_only_after_the_grace_period()
    {
        var l = new LinkStateTracker { Grace = 3 };
        l.Update(true, false, 0);
        Assert.False(l.Update(false, false, 10));
        Assert.False(l.Update(false, false, 12.9));
        Assert.True(l.Update(false, false, 13.0));
        Assert.False(l.Connected);
        Assert.Equal(1, l.Disconnects);
    }

    [Fact]
    public void Active_session_alone_keeps_the_link()
    {
        var l = new LinkStateTracker { Grace = 1 };
        l.Update(true, true, 0);
        for (double t = 0.5; t < 10; t += 0.5) Assert.False(l.Update(false, true, t));
        Assert.True(l.Connected);
    }

    [Fact]
    public void Force_disconnect_is_immediate()
    {
        var l = new LinkStateTracker();
        l.Update(true, false, 0);
        Assert.True(l.ForceDisconnected());
        Assert.False(l.Connected);
    }
}

public class PacingTests
{
    private static ReportScheduler S(out FakeTransport t)
    {
        t = new FakeTransport();
        return new ReportScheduler(t);
    }

    [Fact]
    public void Continuous_motion_waits_for_its_slot_but_buttons_and_keys_never_wait()
    {
        var s = S(out _);
        s.AddMouseMotion(10, 0);
        Assert.Equal(0, s.PacingDelay(100.0));           // nothing sent yet: go
        Assert.True(s.TryTake(out var first));
        Assert.True(first.Paced);

        // simulate that a paced report was just sent at t=100
        typeof(ReportScheduler).GetField("_lastPacedSend", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(s, 100.0);
        s.AddMouseMotion(5, 0);
        Assert.True(s.PacingDelay(100.002) > 0.005);      // motion alone: wait
        s.SetMouseButtons(MouseButtons.Left);             // a click behind it…
        Assert.Equal(0, s.PacingDelay(100.002));          // …flushes it now
        Assert.True(s.TryTake(out var motion));
        Assert.Equal(5, motion.Dx);
        Assert.True(s.TryTake(out var click));
        Assert.False(click.Paced);
        Assert.Equal(MouseButtons.Left, click.Buttons);

        s.EnqueueKeyboard(KeyboardReport.Empty);
        Assert.Equal(0, s.PacingDelay(100.003));
    }

    [Fact]
    public void Pacing_backs_off_when_the_radio_is_slow_and_recovers()
    {
        var s = S(out _);
        double start = s.MotionInterval;
        for (int i = 0; i < 40; i++) s.AdaptPacing(0.060);   // sends taking 60 ms: congested
        Assert.True(s.MotionInterval > start * 1.8, $"{s.MotionInterval}");
        Assert.True(s.MotionInterval <= s.MaxMotionInterval);
        for (int i = 0; i < 400; i++) s.AdaptPacing(0.001);  // fast again
        Assert.True(s.MotionInterval < 0.0125, $"{s.MotionInterval}");
        Assert.True(s.MotionInterval >= s.MinMotionInterval);
    }

    [Fact]
    public void Link_interval_hint_sets_the_floor()
    {
        var s = S(out _);
        s.SetLinkIntervalHint(30);                           // 30 ms connection interval → ≥ 15 ms
        for (int i = 0; i < 400; i++) s.AdaptPacing(0.0005);
        Assert.Equal(0.015, s.MotionInterval, 4);
    }

    [Fact]
    public async Task Only_delivered_motion_is_reported_and_synthetic_moves_are_flagged()
    {
        var t = new FakeTransport();
        await using var s = new ReportScheduler(t);
        var got = new List<MouseDelivery>();
        s.MouseDelivered += d => { lock (got) got.Add(d); };
        s.Start();
        s.AddMouseMotionExact(-3000, 0, separateReport: true, tracked: false);
        s.AddMouseMotion(12, -3);
        for (int i = 0; i < 100; i++) { lock (got) if (got.Count >= 2) break; await Task.Delay(10); }
        lock (got)
        {
            Assert.Contains(got, d => d.Dx == -3000 && !d.Tracked);
            Assert.Contains(got, d => d.Dx == 12 && d.Dy == -3 && d.Tracked);
        }
    }

    [Fact]
    public void Synthetic_moves_never_merge_with_user_motion()
    {
        var s = S(out _);
        s.AddMouseMotionExact(-3000, 0, separateReport: true, tracked: false);
        s.AddMouseMotion(7, 0);
        Assert.True(s.TryTake(out var snap));
        Assert.Equal(-3000, snap.Dx);
        Assert.False(snap.Tracked);
        Assert.True(s.TryTake(out var user));
        Assert.Equal(7, user.Dx);
        Assert.True(user.Tracked);
    }
}

internal sealed class FlakyTransport : IHidTransport
{
    public readonly List<(byte id, byte[] data)> Delivered = new();
    private int _calls;
    public int FailEvery = 3;          // every 3rd send fails (iOS subscription blip)
    public string Name => "flaky";
    public TransportState State => TransportState.Connected;
    public string? StatusDetail => null;
    public string? PeerName => "iPad";
    public int MaxReportPayload => 182;
    public double? ConnectionIntervalMs => 15;
    public event EventHandler? StateChanged { add { } remove { } }
    public event EventHandler<HostReportWrite>? HostWrite { add { } remove { } }
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public async Task<bool> SendInputReportAsync(byte reportId, ReadOnlyMemory<byte> data)
    {
        await Task.Yield();
        if (Interlocked.Increment(ref _calls) % FailEvery == 0) return false;
        lock (Delivered) Delivered.Add((reportId, data.ToArray()));
        return true;
    }
    public void SetBatteryLevel(int percent) { }
    public string Describe() => "flaky";
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class CriticalDeliveryTests
{
    [Fact]
    public async Task Keys_survive_a_flaky_link_in_order_so_nothing_sticks()
    {
        var t = new FlakyTransport();
        await using var s = new ReportScheduler(t);
        s.Start();
        for (int i = 0; i < 15; i++)
        {
            s.EnqueueKeyboard(new KeyboardReport(Mods.None, new[] { (byte)(Keys.A + i) }));
            s.AddMouseMotion(5, 0);                       // motion interleaved, may be lost
            s.EnqueueKeyboard(KeyboardReport.Empty);      // the key-up must never be lost
        }
        for (int i = 0; i < 300 && s.CriticalRetried == 0 || i < 300 && KeyCount(t) < 30; i++) await Task.Delay(10);
        List<byte[]> keys;
        lock (t.Delivered) keys = t.Delivered.Where(d => d.id == ReportIds.Keyboard).Select(d => d.data).ToList();
        Assert.Equal(30, keys.Count);
        for (int i = 0; i < 15; i++)
        {
            Assert.Equal(Keys.A + i, keys[i * 2][2]);   // down, in order
            Assert.Equal(0, keys[i * 2 + 1][2]);        // followed by its up
        }
        Assert.Equal(0, s.CriticalLost);
        Assert.True(s.CriticalRetried > 0);
    }

    private static int KeyCount(FlakyTransport t)
    {
        lock (t.Delivered) return t.Delivered.Count(d => d.id == ReportIds.Keyboard);
    }
}
