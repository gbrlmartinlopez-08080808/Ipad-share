using System.Diagnostics;
using IpadShare.Core.Hid;

namespace IpadShare.Core.Transport;

/// <summary>A mouse report that the transport accepted, for the iPad pointer model.</summary>
/// <param name="Dx">Counts actually sent.</param>
/// <param name="Dy">Counts actually sent.</param>
/// <param name="Buttons">Button state in that report.</param>
/// <param name="Time">When it was handed to the radio (seconds, Stopwatch clock).</param>
/// <param name="Tracked">False for synthetic moves (entry snap, calibration) the model already accounts for.</param>
public readonly record struct MouseDelivery(int Dx, int Dy, MouseButtons Buttons, double Time, bool Tracked);

/// <summary>
/// Delivers reports to the transport smoothly, with minimal latency and in the right order.
/// <list type="bullet">
/// <item>Discrete events (keys, media keys, button changes) keep their exact relative order, are never
/// dropped and are never delayed.</item>
/// <item>Pointer motion, scroll and trackpad frames are coalesced and <b>paced</b>: sent at a steady
/// rhythm that the radio link can sustain. The rhythm adapts to how long each send takes, so the
/// radio's buffers never fill with stale motion (which shows up as lag followed by a jump).</item>
/// <item>Trackpad frames are latest-wins, but finger lifts are never lost.</item>
/// <item>Every mouse report that was really handed to the radio is reported via
/// <see cref="MouseDelivered"/>, so the iPad pointer model counts only motion the iPad received.</item>
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
        public bool ButtonEvent;      // created by a button change: first report is not paced
        public bool Tracked = true;   // false for entry-snap / calibration moves
        public bool Critical;         // entry snap: retried until delivered
    }
    private sealed class TouchItem : Item { public TouchpadReport Report = null!; }

    /// <summary>A report ready to send.</summary>
    internal readonly record struct Outgoing(byte Id, byte[] Data, long EnqueuedTicks, bool Paced,
        bool IsMouse, int Dx, int Dy, MouseButtons Buttons, bool Tracked, bool Critical = false);

    private readonly IHidTransport _transport;
    private readonly object _lock = new();
    private readonly LinkedList<Item> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _inFlight;
    private readonly int _maxInFlight;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private MouseButtons _buttons;
    private double _fracX, _fracY, _fracWheel, _fracPan;

    // Pacing
    private double _lastPacedSend = double.NegativeInfinity;
    private double _sendLatencyEma = 0.004;
    private double _floor;

    // Stats
    private long _sent, _failed, _latencyTicksTotal, _latencySamples;
    private long _windowStartTicks = Stopwatch.GetTimestamp();
    private int _windowCount;
    private double _reportsPerSecond;

    public ReportScheduler(IHidTransport transport, int maxInFlight = 2)
    {
        _transport = transport;
        _maxInFlight = Math.Max(1, maxInFlight);
        _inFlight = new SemaphoreSlim(_maxInFlight);
        _floor = MinMotionInterval;
    }

    /// <summary>True when the iPad has enabled high-resolution scrolling (1/120 notch units).</summary>
    public bool HiResScroll { get; set; }
    /// <summary>When false, producers' calls are ignored (no iPad connected / not controlling it).</summary>
    public bool Accepting { get; set; } = true;
    public MouseButtons Buttons { get { lock (_lock) return _buttons; } }

    /// <summary>Shortest interval between paced (motion) reports.</summary>
    public double MinMotionInterval { get; set; } = 0.0100;
    /// <summary>Longest interval the pacer backs off to on a congested link.</summary>
    public double MaxMotionInterval { get; set; } = 0.0450;
    /// <summary>Current interval between paced reports (seconds).</summary>
    public double MotionInterval { get; private set; } = 0.0150;

    /// <summary>Raised on a background thread for every mouse report the transport accepted.</summary>
    public event Action<MouseDelivery>? MouseDelivered;

    public long Sent => Interlocked.Read(ref _sent);
    public long Failed => Interlocked.Read(ref _failed);
    public double AverageQueueLatencyMs =>
        _latencySamples == 0 ? 0 : (double)_latencyTicksTotal / _latencySamples * 1000.0 / Stopwatch.Frequency;
    public double SendLatencyMs => _sendLatencyEma * 1000;
    public double ReportsPerSecond => _reportsPerSecond;
    public int QueueLength { get { lock (_lock) return _queue.Count; } }

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>
    /// Tell the pacer the radio's connection interval (ms), when known. Up to two reports per
    /// connection event are allowed; the latency feedback loop does the rest.
    /// </summary>
    public void SetLinkIntervalHint(double? connectionIntervalMs)
    {
        _floor = connectionIntervalMs is double ms && ms > 0
            ? Math.Clamp(ms / 1000 * 0.5, MinMotionInterval, MaxMotionInterval)
            : MinMotionInterval;
        MotionInterval = Math.Clamp(Math.Max(MotionInterval, _floor), _floor, MaxMotionInterval);
    }

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
            var seg = Segment();
            seg.Dx += ix;
            seg.Dy += iy;
        }
        _signal.Release();
    }

    /// <summary>
    /// Exact integer motion (entry snap, calibration strokes): bypasses fractional accumulation.
    /// </summary>
    /// <param name="separateReport">Always send as its own report.</param>
    /// <param name="tracked">False when the pointer model already accounts for this motion.</param>
    /// <param name="critical">Retry until delivered (entry snap).</param>
    public void AddMouseMotionExact(int dx, int dy, bool separateReport = false, bool tracked = true, bool critical = false)
    {
        if (!Accepting) return;
        lock (_lock)
        {
            var seg = separateReport || !tracked ? NewSegment() : Segment();
            seg.Tracked = tracked;
            seg.Critical = critical;
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
            NewSegment().ButtonEvent = true;     // a button change always gets its own report, in order, immediately
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
        if (_queue.Last?.Value is MouseItem m && m.Buttons == _buttons && m.Tracked) return m;
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
            while (true)
            {
                // Continuous motion alone in the queue waits for its slot in the rhythm (and keeps
                // accumulating meanwhile). Anything discrete behind it flushes it immediately.
                double wait = PacingDelay(Now);
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
                    continue;
                }
                await _inFlight.WaitAsync(ct).ConfigureAwait(false);
                if (!TryTake(out var r)) { _inFlight.Release(); break; }
                long latency = Stopwatch.GetTimestamp() - r.EnqueuedTicks;
                Interlocked.Add(ref _latencyTicksTotal, latency);
                Interlocked.Increment(ref _latencySamples);
                if (r.Critical)
                {
                    // Keys, button changes and the entry snap must arrive, and in order: wait for
                    // everything in flight, then send alone, retrying through short link blips.
                    for (int i = 1; i < _maxInFlight; i++) await _inFlight.WaitAsync(ct).ConfigureAwait(false);
                    try { await SendCritical(r, ct).ConfigureAwait(false); }
                    finally { _inFlight.Release(_maxInFlight); }
                    continue;
                }
                if (r.Paced) _lastPacedSend = Now;
                _ = SendOne(r);
            }
        }
    }

    /// <summary>Seconds to wait before the head of the queue may be sent (0 = now).</summary>
    internal double PacingDelay(double now)
    {
        lock (_lock)
        {
            var head = _queue.First;
            if (head == null || head.Next != null) return 0;
            if (!IsPaced(head.Value)) return 0;
            if (head.Value is MouseItem m && m.SentOnce && m.Dx == 0 && m.Dy == 0 && m.Wheel == 0 && m.Pan == 0) return 0;
            double due = _lastPacedSend + MotionInterval;
            return due > now ? due - now : 0;
        }
    }

    /// <summary>How long a critical report is retried while the link still reports connected.</summary>
    public double CriticalRetryWindow { get; set; } = 1.5;

    private async Task SendCritical(Outgoing r, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int attempts = 0;
        while (true)
        {
            attempts++;
            bool ok;
            try { ok = await _transport.SendInputReportAsync(r.Id, r.Data).ConfigureAwait(false); }
            catch { ok = false; }
            if (ok)
            {
                Interlocked.Increment(ref _sent);
                if (attempts > 1) Interlocked.Increment(ref _criticalRetried);
                CountRate();
                if (r.IsMouse && (r.Dx != 0 || r.Dy != 0))
                {
                    try { MouseDelivered?.Invoke(new MouseDelivery(r.Dx, r.Dy, r.Buttons, Now, r.Tracked)); } catch { }
                }
                return;
            }
            if (_transport.State != TransportState.Connected || sw.Elapsed.TotalSeconds > CriticalRetryWindow)
            {
                Interlocked.Increment(ref _failed);
                Interlocked.Increment(ref _criticalLost);
                return;
            }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private long _criticalRetried, _criticalLost;
    /// <summary>Critical reports that needed a retry / that were given up on.</summary>
    public long CriticalRetried => Interlocked.Read(ref _criticalRetried);
    public long CriticalLost => Interlocked.Read(ref _criticalLost);

    private static bool IsPaced(Item item) => item switch
    {
        MouseItem m => !(m.ButtonEvent && !m.SentOnce) && m.Tracked,
        TouchItem => true,
        _ => false,
    };

    private async Task SendOne(Outgoing r)
    {
        long start = Stopwatch.GetTimestamp();
        bool ok = false;
        try
        {
            ok = await _transport.SendInputReportAsync(r.Id, r.Data).ConfigureAwait(false);
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

        double duration = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        if (r.Paced) AdaptPacing(duration);
        CountRate();
        if (ok && r.IsMouse && (r.Dx != 0 || r.Dy != 0))
        {
            try { MouseDelivered?.Invoke(new MouseDelivery(r.Dx, r.Dy, r.Buttons, start / (double)Stopwatch.Frequency, r.Tracked)); }
            catch { /* a faulty listener must not stop delivery */ }
        }
    }

    /// <summary>
    /// Latency feedback: if handing a report to the radio starts taking long, its buffers are filling
    /// up, so slow the rhythm down; when sends complete quickly again, speed back up.
    /// </summary>
    internal void AdaptPacing(double sendDuration)
    {
        _sendLatencyEma = 0.8 * _sendLatencyEma + 0.2 * sendDuration;
        double interval = MotionInterval;
        if (_sendLatencyEma > Math.Max(0.012, 1.5 * interval))
            interval *= 1.15;
        else if (_sendLatencyEma < 0.4 * interval)
            interval *= 0.97;
        MotionInterval = Math.Clamp(interval, _floor, MaxMotionInterval);
    }

    private void CountRate()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _windowCount++;
            double elapsed = (now - _windowStartTicks) / (double)Stopwatch.Frequency;
            if (elapsed >= 1)
            {
                _reportsPerSecond = _windowCount / elapsed;
                _windowCount = 0;
                _windowStartTicks = now;
            }
        }
    }

    /// <summary>Produces the next report from the head of the queue. Exposed for tests.</summary>
    internal bool TryTake(out byte reportId, out byte[] data, out long enqueuedTicks)
    {
        bool ok = TryTake(out var r);
        reportId = r.Id; data = r.Data ?? Array.Empty<byte>(); enqueuedTicks = r.EnqueuedTicks;
        return ok;
    }

    internal bool TryTake(out Outgoing report)
    {
        lock (_lock)
        {
            while (_queue.First is { } node)
            {
                var item = node.Value;
                switch (item)
                {
                    case KeyItem k:
                        _queue.RemoveFirst();
                        report = new Outgoing(ReportIds.Keyboard, k.Data, k.EnqueuedTicks, false, false, 0, 0, default, true, Critical: true);
                        return true;
                    case ConsumerItem c:
                        _queue.RemoveFirst();
                        report = new Outgoing(ReportIds.Consumer, ConsumerReport.ToBytes(c.Usage), c.EnqueuedTicks, false, false, 0, 0, default, true, Critical: true);
                        return true;
                    case TouchItem t:
                        _queue.RemoveFirst();
                        report = new Outgoing(ReportIds.Touchpad, t.Report.ToBytes(), t.EnqueuedTicks, true, false, 0, 0, default, true);
                        return true;
                    case MouseItem m:
                        bool hasMotion = m.Dx != 0 || m.Dy != 0 || m.Wheel != 0 || m.Pan != 0;
                        if (!hasMotion && m.SentOnce) { _queue.RemoveFirst(); continue; }
                        bool paced = IsPaced(m);
                        bool critical = (m.ButtonEvent && !m.SentOnce) || m.Critical;
                        short dx = Take(ref m.Dx), dy = Take(ref m.Dy), w = Take(ref m.Wheel), p = Take(ref m.Pan);
                        m.SentOnce = true;
                        bool done = m.Dx == 0 && m.Dy == 0 && m.Wheel == 0 && m.Pan == 0;
                        // Keep the segment while it is the newest one: more motion may merge into it.
                        if (done && (node.Next != null || !m.Tracked)) _queue.RemoveFirst();
                        report = new Outgoing(ReportIds.Mouse, new MouseReport(m.Buttons, dx, dy, w, p).ToBytes(),
                            m.EnqueuedTicks, paced, true, dx, dy, m.Buttons, m.Tracked, critical);
                        return true;
                }
                _queue.RemoveFirst();
            }
            report = default;
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
