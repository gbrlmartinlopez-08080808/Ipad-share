using IpadShare.Core.Hid;
using IpadShare.Core.Input;
using IpadShare.Core.Settings;
using IpadShare.Core.Touch;
using IpadShare.Core.Transport;

namespace IpadShare.Core.Tests;

internal sealed class FakeTransport : IHidTransport
{
    public readonly List<(byte id, byte[] data)> Sent = new();
    public TaskCompletionSource? Gate;
    public string Name => "fake";
    public TransportState State => TransportState.Connected;
    public string? StatusDetail => null;
    public string? PeerName => "iPad";
    public int MaxReportPayload => 182;
    public event EventHandler? StateChanged { add { } remove { } }
    public event EventHandler<HostReportWrite>? HostWrite { add { } remove { } }
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public async Task<bool> SendInputReportAsync(byte reportId, ReadOnlyMemory<byte> data)
    {
        if (Gate != null) await Gate.Task;
        lock (Sent) Sent.Add((reportId, data.ToArray()));
        return true;
    }
    public void SetBatteryLevel(int percent) { }
    public string Describe() => "fake";
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class ReportSchedulerTests
{
    private static List<(byte id, byte[] data)> Drain(ReportScheduler s)
    {
        var list = new List<(byte, byte[])>();
        while (s.TryTake(out var id, out var data, out _)) list.Add((id, data));
        return list;
    }

    private static MouseReport Mouse(byte[] d) => new((MouseButtons)d[0],
        BitConverter.ToInt16(d, 1), BitConverter.ToInt16(d, 3), BitConverter.ToInt16(d, 5), BitConverter.ToInt16(d, 7));

    [Fact]
    public void Motion_coalesces_while_the_radio_is_busy()
    {
        var s = new ReportScheduler(new FakeTransport());
        for (int i = 0; i < 100; i++) s.AddMouseMotion(1.5, -0.5);
        var r = Drain(s);
        Assert.Single(r);
        var m = Mouse(r[0].data);
        Assert.Equal(150, m.Dx);
        Assert.Equal(-50, m.Dy);
    }

    [Fact]
    public void Fractions_carry_over()
    {
        var s = new ReportScheduler(new FakeTransport());
        for (int i = 0; i < 10; i++) s.AddMouseMotion(0.3, 0);
        Assert.Equal(3, Mouse(Drain(s)[0].data).Dx);
    }

    [Fact]
    public void Clicks_keep_their_position_in_the_motion_stream()
    {
        var s = new ReportScheduler(new FakeTransport());
        s.AddMouseMotion(10, 0);
        s.SetMouseButtons(MouseButtons.Left);
        s.AddMouseMotion(5, 0);
        s.SetMouseButtons(MouseButtons.None);
        s.AddMouseMotion(7, 0);
        var r = Drain(s).Select(x => Mouse(x.data)).ToList();
        Assert.Equal(new[]
        {
            new MouseReport(MouseButtons.None, 10, 0, 0, 0),
            new MouseReport(MouseButtons.Left, 5, 0, 0, 0),
            new MouseReport(MouseButtons.None, 7, 0, 0, 0),
        }, r);
    }

    [Fact]
    public void Keys_and_clicks_stay_in_order()
    {
        var s = new ReportScheduler(new FakeTransport());
        s.SetMouseButtons(MouseButtons.Left);
        s.SetMouseButtons(MouseButtons.None);
        s.EnqueueKeyboard(new KeyboardReport(Mods.None, new[] { Keys.A }));
        s.EnqueueKeyboard(KeyboardReport.Empty);
        var ids = Drain(s).Select(x => x.id).ToArray();
        Assert.Equal(new byte[] { ReportIds.Mouse, ReportIds.Mouse, ReportIds.Keyboard, ReportIds.Keyboard }, ids);
    }

    [Fact]
    public void Large_motion_is_split_into_int16_reports()
    {
        var s = new ReportScheduler(new FakeTransport());
        s.AddMouseMotionExact(-80000, 0);
        var r = Drain(s).Select(x => Mouse(x.data)).ToList();
        Assert.Equal(-80000, r.Sum(m => (long)m.Dx));
        Assert.All(r, m => Assert.InRange(m.Dx, -32767, 32767));
    }

    [Fact]
    public void Scroll_uses_hi_res_units_when_enabled()
    {
        var s = new ReportScheduler(new FakeTransport());
        s.AddScroll(0.5, 0);
        Assert.Empty(Drain(s));                      // half a notch: nothing yet in notch mode
        s.AddScroll(0.6, 0);
        Assert.Equal(1, Mouse(Drain(s)[0].data).Wheel);

        var h = new ReportScheduler(new FakeTransport()) { HiResScroll = true };
        h.AddScroll(0.5, -0.25);
        var m = Mouse(Drain(h)[0].data);
        Assert.Equal(60, m.Wheel);
        Assert.Equal(-30, m.Pan);
    }

    [Fact]
    public void Touch_frames_are_latest_wins_but_lifts_survive()
    {
        var s = new ReportScheduler(new FakeTransport());
        s.SubmitTouch(new TouchpadReport(new[] { new TouchContact(0, false, true, 10, 10), new TouchContact(1, true, true, 20, 20) }, 1, false));
        s.SubmitTouch(new TouchpadReport(new[] { new TouchContact(1, true, true, 25, 25) }, 2, false));
        var r = Drain(s);
        Assert.Single(r);
        var bytes = r[0].data;
        Assert.Equal(2, bytes[ReportMap.TouchContacts * 5 + 2]);   // contact count includes the carried lift
    }

    [Fact]
    public void Not_accepting_drops_input()
    {
        var s = new ReportScheduler(new FakeTransport()) { Accepting = false };
        s.AddMouseMotion(10, 10);
        s.EnqueueKeyboard(KeyboardReport.Empty);
        Assert.Empty(Drain(s));
    }

    [Fact]
    public async Task Background_loop_delivers_everything_in_order()
    {
        var t = new FakeTransport();
        await using var s = new ReportScheduler(t, maxInFlight: 1);
        s.Start();
        for (int i = 0; i < 20; i++)
        {
            s.EnqueueKeyboard(new KeyboardReport(Mods.None, new[] { (byte)(Keys.A + i) }));
            s.EnqueueKeyboard(KeyboardReport.Empty);
        }
        for (int i = 0; i < 100 && s.Sent < 40; i++) await Task.Delay(10);
        lock (t.Sent)
        {
            Assert.Equal(40, t.Sent.Count);
            for (int i = 0; i < 20; i++) Assert.Equal(Keys.A + i, t.Sent[i * 2].data[2]);
        }
    }
}

public class BridgeProtocolTests
{
    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 0, 0, 0 })]
    [InlineData(new byte[] { 1, 2, 0, 3 })]
    public void Cobs_round_trips(byte[] data)
    {
        var enc = BridgeProtocol.CobsEncode(data);
        Assert.DoesNotContain((byte)0, enc);
        Assert.Equal(data, BridgeProtocol.CobsDecode(enc));
    }

    [Fact]
    public void Cobs_round_trips_long_runs()
    {
        var rnd = new Random(42);
        foreach (int len in new[] { 253, 254, 255, 256, 600 })
        {
            var data = new byte[len];
            rnd.NextBytes(data);
            for (int i = 0; i < len; i += 37) data[i] = 0;
            var enc = BridgeProtocol.CobsEncode(data);
            Assert.DoesNotContain((byte)0, enc);
            Assert.Equal(data, BridgeProtocol.CobsDecode(enc));
            var noZero = data.Select(b => b == 0 ? (byte)1 : b).ToArray();
            Assert.Equal(noZero, BridgeProtocol.CobsDecode(BridgeProtocol.CobsEncode(noZero)));
        }
    }

    [Fact]
    public void Frames_survive_stream_splitting_and_reject_corruption()
    {
        var a = BridgeProtocol.Encode(BridgeMessage.InputReport, new byte[] { 3, 0, 0, 5, 0 });
        var b = BridgeProtocol.Encode(BridgeMessage.Status, new byte[] { 1, 185, 0, 12, 0, 1 });
        var stream = a.Concat(b).ToArray();
        var dec = new BridgeFrameDecoder();
        var frames = new List<BridgeFrame>();
        foreach (var chunk in stream.Chunk(3)) frames.AddRange(dec.Push(chunk));
        Assert.Equal(2, frames.Count);
        Assert.Equal(BridgeMessage.InputReport, frames[0].Type);
        Assert.Equal(new byte[] { 3, 0, 0, 5, 0 }, frames[0].Payload);
        Assert.Equal(BridgeMessage.Status, frames[1].Type);

        var bad = (byte[])a.Clone();
        bad[2] ^= 0x10;
        Assert.Empty(dec.Push(bad));
        Assert.Equal(1, dec.BadFrames);
        Assert.Single(dec.Push(b));                // resynchronises
    }

    [Fact]
    public void Crc_values_match_reference_vectors()
    {
        var check = "123456789"u8.ToArray();
        Assert.Equal(0xF4, BridgeProtocol.Crc8(check));          // CRC-8/SMBUS
        Assert.Equal(0x29B1, BridgeProtocol.Crc16(check));       // CRC-16/CCITT-FALSE
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_trips_and_normalises()
    {
        var s = new AppSettings { PointerSpeed = 99, KeyboardStyle = KeyboardStyle.Mac, TrackpadMode = TrackpadMode.Native };
        s.GestureActions.Remove(GestureKind.PinchIn);
        var back = AppSettings.FromJson(s.ToJson());
        Assert.Equal(3, back.PointerSpeed);
        Assert.Equal(KeyboardStyle.Mac, back.KeyboardStyle);
        Assert.Equal(TrackpadMode.Native, back.TrackpadMode);
        Assert.Equal("Cmd+Minus", back.GestureActions[GestureKind.PinchIn]);
        Assert.Contains("\"Native\"", s.ToJson());
    }

    [Fact]
    public void Store_survives_corrupt_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipadshare-test-" + Guid.NewGuid());
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(store.Path, "{ not json");
        var s = store.Load();
        Assert.False(s.FirstRunCompleted);
        s.FirstRunCompleted = true;
        store.Save(s);
        Assert.True(store.Load().FirstRunCompleted);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Default_gesture_actions_all_parse()
    {
        foreach (var kv in AppSettings.DefaultGestureActions())
            Assert.True(ChordParser.TryParse(kv.Value, out _), kv.Value);
    }

    [Theory]
    [InlineData("Cmd+H", Mods.Cmd, Keys.H)]
    [InlineData("Globe+Up", Mods.Globe, Keys.Up)]
    [InlineData("Option+Cmd+D", Mods.Option | Mods.Cmd, Keys.D)]
    [InlineData("cmd+shift+3", Mods.Cmd | Mods.LShift, Keys.D3)]
    [InlineData("Cmd+Space", Mods.Cmd, Keys.Space)]
    public void Chords_parse(string text, Mods mods, byte key)
    {
        Assert.True(ChordParser.TryParse(text, out var c));
        Assert.Equal(mods, c.Mods);
        Assert.Equal(key, c.Key);
        Assert.True(ChordParser.TryParse(ChordParser.Format(c), out var again));
        Assert.Equal(c, again);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("")]
    [InlineData("Cmd")]
    [InlineData("Cmd+H+J")]
    [InlineData("Cmd+Nope")]
    public void Bad_chords_are_rejected(string text) => Assert.False(ChordParser.TryParse(text, out _));
}
