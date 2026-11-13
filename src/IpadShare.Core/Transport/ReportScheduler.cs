using System.Diagnostics;
using IpadShare.Core.Hid;

namespace IpadShare.Core.Transport;

/// <summary>
/// Delivers reports to the transport with minimal latency and in the right order.
/// <list type="bullet">
/// <item>Discrete events (keys, media keys, button changes) keep their exact relative order and are never dropped.</item>
/// <item>Pointer motion and scroll are coalesced into the most recent mouse segment while the radio is busy,
/// so the iPad always shows the latest position instead of replaying stale motion.</item>
/// <item>Trackpad frames are latest-wins, but finger lifts are never lost.</item>
/// </list>
/// Producers may call from any thread; one background loop sends.
/// </summary>
public sealed class ReportScheduler : IAsyncDisposable
{
    private abstract class Item { public long EnqueuedTicks = Stopwatch.GetTimestamp(); }
    private sealed class KeyItem : Item { public byte[] Data = Array.Empty<byte>(); }
    private sealed class ConsumerItem : Item { public ushort Usage; }
    private sealed class MouseItem : Item
    {
        public MouseButtons Buttons;
        public long Dx, Dy, Wheel, Pan;
        public bool SentOnce;
    }
    private sealed class TouchItem : Item { public TouchpadReport Report = null!; }

    private readonly IHidTransport _transport;
    private readonly object _lock = new();
    private readonly LinkedList<Item> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _inFlight;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private MouseButtons _buttons;
    private double _fracX, _fracY, _fracWheel, _fracPan;

    // Stats
    private long _sent, _failed, _latencyTicksTotal, _latencySamples;

    public ReportScheduler(IHidTransport transport, int maxInFlight = 2)
    {
        _transport = transport;
        _inFlight = new SemaphoreSlim(Math.Max(1, maxInFlight));
    }

    /// <summary>True when the iPad has enabled high-resolution scrolling (1/120 notch units).</summary>
    public bool HiResScroll { get; set; }
    /// <summary>When false, producers' calls are ignored (no iPad connected / not controlling it).</summary>
    public bool Accepting { get; set; } = true;
    public MouseButtons Buttons { get { lock (_lock) return _buttons; } }

    public long Sent => Interlocked.Read(ref _sent);
    public long Failed => Interlocked.Read(ref _failed);
    public double AverageQueueLatencyMs =>
        _latencySamples == 0 ? 0 : (double)_latencyTicksTotal / _latencySamples * 1000.0 / Stopwatch.Frequency;
    public int QueueLength { get { lock (_lock) return _queue.Count; } }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { if (_loop != null) await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Drop everything queued and forget fractional motion (link reset or leaving the iPad).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _fracX = _fracY = _fracWheel = _fracPan = 0;
        }
    }

    /// <summary>Forget button state (after the iPad link was lost, the iPad has nothing pressed).</summary>
    public void ResetButtons()
    {
        lock (_lock) _buttons = MouseButtons.None;
    }

    // ---------------- producers ----------------

    public void EnqueueKeyboard(KeyboardReport report)
    {
        if (!Accepting) return;
        lock (_lock) _queue.AddLast(new KeyItem { Data = report.ToBytes() });
        _signal.Release();
    }

    public void EnqueueConsumer(ushort usage)
    {
        if (!Accepting) return;
        lock (_lock) _queue.AddLast(new ConsumerItem { Usage = usage });
        _signal.Release();
    }

    /// <summary>Relative motion in mouse counts; fractions carry over.</summary>
    public void AddMouseMotion(double dx, double dy)
    {
        if (!Accepting) return;
        long ix, iy;
        lock (_lock)
        {
            _fracX += dx; _fracY += dy;
            ix = Whole(_fracX); iy = Whole(_fracY);
            _fracX -= ix; _fracY -= iy;
            if (ix == 0 && iy == 0) return;
            Segment().Dx += ix;
            Segment().Dy += iy;
        }
        _signal.Release();
    }

    /// <summary>Exact integer motion (entry snap, calibration strokes): bypasses fractional accumulation.</summary>
    public void AddMouseMotionExact(int dx, int dy, bool separateReport = false)
    {
        if (!Accepting) return;
        lock (_lock)
        {
            var seg = separateReport ? NewSegment() : Segment();
            seg.Dx += dx; seg.Dy += dy;
        }
        _signal.Release();
    }

    /// <summary>Scroll in notches (vertical + = up, horizontal + = right).</summary>
    public void AddScroll(double vertical, double horizontal)
    {
        if (!Accepting) return;
        double scale = HiResScroll ? ReportMap.HiResScrollMultiplier : 1;
        lock (_lock)
        {
            _fracWheel += vertical * scale;
            _fracPan += horizontal * scale;
            long w = Whole(_fracWheel), p = Whole(_fracPan);
            _fracWheel -= w; _fracPan -= p;
            if (w == 0 && p == 0) return;
            var seg = Segment();
            seg.Wheel += w; seg.Pan += p;
        }
        _signal.Release();
    }

    public void SetMouseButtons(MouseButtons buttons)
    {
        if (!Accepting) return;
        lock (_lock)
        {
            if (buttons == _buttons) return;
            _buttons = buttons;
            NewSegment();                    // a button change always gets its own report, in order
        }
        _signal.Release();
    }

    public void SubmitTouch(TouchpadReport report)
    {
        if (!Accepting) return;
        lock (_lock)
        {
            if (_queue.Last?.Value is TouchItem last)
                last.Report = MergeTouch(last.Report, report);
            else
                _queue.AddLast(new TouchItem { Report = report });
        }
        _signal.Release();
    }

    // Must hold _lock.
    private MouseItem Segment()
    {
        if (_queue.Last?.Value is MouseItem m && m.Buttons == _buttons) return m;
        return NewSegment();
    }

    private MouseItem NewSegment()
    {
        var m = new MouseItem { Buttons = _buttons };
        _queue.AddLast(m);
        return m;
    }

    /// <summary>Latest frame wins, but fingers lifted in the dropped frame are carried over as lifts.</summary>
    internal static TouchpadReport MergeTouch(TouchpadReport older, TouchpadReport newer)
    {
        var contacts = newer.Contacts.ToList();
        foreach (var c in older.Contacts)
        {
            if (c.Tip) continue;
            if (contacts.Any(n => n.Id == c.Id)) continue;
            if (contacts.Count < ReportMap.TouchContacts) contacts.Add(c);
        }
        return new TouchpadReport(contacts, newer.ScanTime, newer.Button);
    }

    // ---------------- sending ----------------

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(ct).ConfigureAwait(false);
            while (TryTake(out byte id, out byte[] data, out long enqueued))
            {
                await _inFlight.WaitAsync(ct).ConfigureAwait(false);
                long latency = Stopwatch.GetTimestamp() - enqueued;
                Interlocked.Add(ref _latencyTicksTotal, latency);
                Interlocked.Increment(ref _latencySamples);
                _ = SendOne(id, data);
            }
        }
    }

    private async Task SendOne(byte id, byte[] data)
    {
        try
        {
            bool ok = await _transport.SendInputReportAsync(id, data).ConfigureAwait(false);
            if (ok) Interlocked.Increment(ref _sent); else Interlocked.Increment(ref _failed);
        }
        catch
        {
            Interlocked.Increment(ref _failed);
        }
        finally
        {
            _inFlight.Release();
        }
    }

    /// <summary>Produces the next report from the head of the queue. Exposed for tests.</summary>
    internal bool TryTake(out byte reportId, out byte[] data, out long enqueuedTicks)
    {
        lock (_lock)
        {
            while (_queue.First is { } node)
            {
                var item = node.Value;
                enqueuedTicks = item.EnqueuedTicks;
                switch (item)
                {
                    case KeyItem k:
                        _queue.RemoveFirst();
                        reportId = ReportIds.Keyboard; data = k.Data;
                        return true;
                    case ConsumerItem c:
                        _queue.RemoveFirst();
                        reportId = ReportIds.Consumer; data = ConsumerReport.ToBytes(c.Usage);
                        return true;
                    case TouchItem t:
                        _queue.RemoveFirst();
                        reportId = ReportIds.Touchpad; data = t.Report.ToBytes();
                        return true;
                    case MouseItem m:
                        bool hasMotion = m.Dx != 0 || m.Dy != 0 || m.Wheel != 0 || m.Pan != 0;
                        if (!hasMotion && m.SentOnce) { _queue.RemoveFirst(); continue; }
                        short dx = Take(ref m.Dx), dy = Take(ref m.Dy), w = Take(ref m.Wheel), p = Take(ref m.Pan);
                        m.SentOnce = true;
                        bool done = m.Dx == 0 && m.Dy == 0 && m.Wheel == 0 && m.Pan == 0;
                        // Keep the segment while it is the newest one: more motion may merge into it.
                        if (done && node.Next != null) _queue.RemoveFirst();
                        else if (done && node.Next == null) { /* leave as merge target */ }
                        reportId = ReportIds.Mouse;
                        data = new MouseReport(m.Buttons, dx, dy, w, p).ToBytes();
                        return true;
                }
                _queue.RemoveFirst();
            }
            reportId = 0; data = Array.Empty<byte>(); enqueuedTicks = 0;
            return false;
        }
    }

    /// <summary>Whole units of an accumulator, tolerant of floating-point drift (0.3 × 10 = 3, not 2).</summary>
    private static long Whole(double v) => (long)Math.Truncate(v + Math.CopySign(1e-9, v));

    private static short Take(ref long v)
    {
        long t = Math.Clamp(v, -MouseReport.Limit, MouseReport.Limit);
        v -= t;
        return (short)t;
    }
}
