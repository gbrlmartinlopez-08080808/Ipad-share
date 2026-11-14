using System.Diagnostics;
using System.Text.Json;
using IpadShare.App.App;
using IpadShare.Core.Calibration;
using IpadShare.Core.Handoff;

namespace IpadShare.App.Calibration;

/// <summary>
/// Runs the pointer calibration: finds the pointer on the Safari page, centres it with closed-loop
/// moves, then sends controlled strokes at increasing speeds and records where iPadOS actually moved
/// the pointer. Survives short Bluetooth or page drop-outs (waits and resumes), retries a bad
/// measurement once, and measures report timing from what really reached the radio.
/// </summary>
internal sealed class CalibrationRunner
{
    private readonly AppController.CalibrationHost _host;
    private readonly CalibrationServer _server;
    private readonly object _lock = new();
    private double _x = double.NaN, _y = double.NaN, _vw, _vh;
    private long _updates;
    private readonly List<(int counts, double t)> _delivered = new();

    public CalibrationRunner(AppController.CalibrationHost host, CalibrationServer server)
    {
        _host = host;
        _server = server;
        server.Message += OnMessage;
    }

    /// <summary>(message, 0…1)</summary>
    public event Action<string, double>? Progress;

    public bool PageReady { get { lock (_lock) return _vw > 0; } }

    private void OnMessage(JsonElement m)
    {
        if (!m.TryGetProperty("t", out var t)) return;
        lock (_lock)
        {
            switch (t.GetString())
            {
                case "hello":
                case "vp":
                    _vw = m.GetProperty("vw").GetDouble();
                    _vh = m.GetProperty("vh").GetDouble();
                    break;
                case "p":
                    _x = m.GetProperty("x").GetDouble();
                    _y = m.GetProperty("y").GetDouble();
                    _updates++;
                    break;
            }
        }
    }

    private void OnDelivered(int counts, double t)
    {
        lock (_delivered) _delivered.Add((counts, t));
    }

    private (double x, double y, long n) Pos() { lock (_lock) return (_x, _y, _updates); }

    private async Task Report(string message, double fraction)
    {
        Progress?.Invoke(message, fraction);
        await _server.SendAsync(new { t = "progress", v = fraction, msg = message });
    }

    public async Task<GainCurve?> RunAsync(CancellationToken ct)
    {
        if (!_host.Connected && !await WaitForLink(ct)) throw new InvalidOperationException("The iPad isn't connected.");
        _host.Delivered += OnDelivered;
        await _host.BeginAsync();
        try
        {
            await Report("Finding the pointer…", 0.02);
            if (!await AcquirePointer(ct)) throw new InvalidOperationException("The pointer didn't appear on the calibration page. Keep the page open in Safari, full screen.");

            double vw, vh;
            lock (_lock) { vw = _vw; vh = _vh; }
            var strokes = CalibrationPlanner.MouseStrokes(vw, GainCurve.DefaultMouse);
            var samples = new List<CalibrationSample>();
            double gainGuess = 0.5;

            for (int i = 0; i < strokes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = strokes[i];
                await Report($"Measuring speed {i / 2 + 1} of {strokes.Count / 2}…", 0.08 + 0.88 * i / strokes.Count);

                CalibrationSample? sample = null;
                for (int attempt = 0; attempt < 2 && sample == null; attempt++)
                {
                    if (!await EnsureReady(ct)) throw new InvalidOperationException("Lost the connection to the iPad for too long. Try again.");
                    gainGuess = await Center(vw, vh, gainGuess, ct);
                    var start = await Settled(ct);
                    var (counts, interval) = await SendStroke(s, ct);
                    var end = await Settled(ct);
                    sample = Measure(s, counts, interval, start.x, end.x, vw);
                    Log.Info($"Calibration stroke {s.UnitsPerReport}×{s.Reports} dir {s.Direction} (attempt {attempt + 1}): " +
                             $"{start.x:0.0}→{end.x:0.0} pt, {counts} counts every {interval * 1000:0.0} ms " +
                             (sample != null ? $"→ gain {sample.Gain:0.000} @ {sample.Speed:0} c/s" : "(discarded)"));
                }
                if (sample != null) samples.Add(sample);
            }

            var curve = CalibrationPlanner.Fit(samples);
            if (curve == null) throw new InvalidOperationException("Not enough clean measurements. Keep Safari in front and the iPad unlocked, then try again.");
            await Report("Done!", 1);
            await _server.SendAsync(new { t = "done", ok = true, msg = "Calibration complete. You can close this page." });
            return curve;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _server.SendAsync(new { t = "done", ok = false, msg = ex.Message });
            throw;
        }
        finally
        {
            _host.Delivered -= OnDelivered;
            await _host.EndAsync(returnToPc: true);
        }
    }

    /// <summary>Gain from what moved on screen; speed from when the reports actually went out.</summary>
    private static CalibrationSample? Measure(CalibrationStroke s, int counts, double interval, double startX, double endX, double vw)
    {
        double moved = (endX - startX) * s.Direction;
        if (counts == 0 || moved <= 0.5 || endX <= 1 || endX >= vw - 1) return null;
        double perReport = Math.Abs(counts) / (double)s.Reports;
        return new CalibrationSample(perReport / Math.Max(interval, 0.004), moved / Math.Abs(counts));
    }

    /// <summary>Wait out a short Bluetooth or Safari drop-out; re-find the pointer afterwards.</summary>
    private async Task<bool> EnsureReady(CancellationToken ct)
    {
        bool hadDropout = false;
        if (!_host.Connected)
        {
            hadDropout = true;
            await Report("Waiting for the iPad to reconnect…", -1);
            if (!await WaitForLink(ct)) return false;
            await _host.BeginAsync();          // back onto the iPad after the drop-out
        }
        if (!_server.ClientConnected)
        {
            hadDropout = true;
            Progress?.Invoke("Waiting for the Safari page… (keep it open on the iPad)", -1);
            var sw = Stopwatch.StartNew();
            while (!_server.ClientConnected && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(250, ct);
            if (!_server.ClientConnected) return false;
        }
        if (hadDropout && !await AcquirePointer(ct)) return false;
        return true;
    }

    private async Task<bool> WaitForLink(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!_host.Connected && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(250, ct);
        return _host.Connected;
    }

    private async Task<bool> AcquirePointer(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long n0 = Pos().n;
        int phase = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            ct.ThrowIfCancellationRequested();
            if (Pos().n > n0 + 2) return true;
            // Wiggle in place; if the pointer is outside the page, sweep toward the middle.
            switch (phase++ % 8)
            {
                case 0: _host.Move(4, 0); break;
                case 1: _host.Move(-4, 0); break;
                case 2 when sw.Elapsed > TimeSpan.FromSeconds(1.5): _host.Move(0, 60); break;   // out of the status/tool bar
                case 4 when sw.Elapsed > TimeSpan.FromSeconds(3): _host.Move(80, 0); break;
                default: _host.Move(phase % 2 == 0 ? 3 : -3, 0); break;
            }
            await Task.Delay(40, ct);
        }
        return Pos().n > n0 + 2;
    }

    /// <summary>Closed-loop centring; returns an updated gain estimate.</summary>
    private async Task<double> Center(double vw, double vh, double gain, CancellationToken ct)
    {
        for (int i = 0; i < 12; i++)
        {
            var p = await Settled(ct);
            double ex = vw / 2 - p.x, ey = vh / 2 - p.y;
            if (Math.Abs(ex) < vw * 0.12 && Math.Abs(ey) < vh * 0.2) return gain;
            double cx = ex / gain, cy = Math.Abs(ey) > vh * 0.2 ? ey / gain : 0;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(cx), Math.Abs(cy)) / 10);
            for (int k = 0; k < steps; k++)
            {
                _host.Move((int)Math.Round(cx / steps), (int)Math.Round(cy / steps));
                await Task.Delay(15, ct);
            }
            var q = await Settled(ct);
            double moved = Math.Abs(q.x - p.x);
            if (Math.Abs(cx) > 20 && moved > 5) gain = Math.Clamp(moved / Math.Abs(cx), 0.05, 5);
        }
        return gain;
    }

    /// <summary>Wait until no pointer updates arrive for a moment, then return the position.</summary>
    private async Task<(double x, double y)> Settled(CancellationToken ct)
    {
        long last = Pos().n;
        var quiet = Stopwatch.StartNew();
        var total = Stopwatch.StartNew();
        while (quiet.ElapsedMilliseconds < 160 && total.ElapsedMilliseconds < 1500)
        {
            await Task.Delay(20, ct);
            long n = Pos().n;
            if (n != last) { last = n; quiet.Restart(); }
        }
        var p = Pos();
        return (p.x, p.y);
    }

    /// <summary>Sends the stroke; returns the counts that reached the radio and their real spacing.</summary>
    private async Task<(int counts, double interval)> SendStroke(CalibrationStroke s, CancellationToken ct)
    {
        lock (_delivered) _delivered.Clear();
        double spacing = Math.Clamp(_host.ReportInterval, 0.012, 0.045);
        int perReport = (int)Math.Round(s.UnitsPerReport) * s.Direction;
        await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < s.Reports; i++)
            {
                ct.ThrowIfCancellationRequested();
                var due = TimeSpan.FromSeconds(i * spacing);
                while (sw.Elapsed < due) Thread.Sleep(1);
                _host.Move(perReport, 0);
            }
        }, ct);
        // let the last reports reach the radio
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 600)
        {
            lock (_delivered) if (_delivered.Count >= s.Reports) break;
            await Task.Delay(10, ct);
        }
        lock (_delivered)
        {
            int counts = _delivered.Sum(d => d.counts);
            double interval = _delivered.Count >= 2
                ? (_delivered[^1].t - _delivered[0].t) / (_delivered.Count - 1)
                : spacing;
            return (counts, interval);
        }
    }
}
