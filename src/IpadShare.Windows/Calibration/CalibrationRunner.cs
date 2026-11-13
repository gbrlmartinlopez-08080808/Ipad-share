using System.Diagnostics;
using System.Text.Json;
using IpadShare.App.App;
using IpadShare.Core.Calibration;
using IpadShare.Core.Handoff;

namespace IpadShare.App.Calibration;

/// <summary>
/// Runs the pointer calibration: finds the pointer on the Safari page, centres it with closed-loop
/// moves, then sends controlled strokes at increasing speeds and records where iPadOS actually
/// moved the pointer. The fitted curve is iPadOS's real acceleration for this iPad's Tracking Speed.
/// </summary>
internal sealed class CalibrationRunner
{
    private readonly AppController.CalibrationHost _host;
    private readonly CalibrationServer _server;
    private readonly object _lock = new();
    private double _x = double.NaN, _y = double.NaN, _vw, _vh;
    private long _updates;

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

    private (double x, double y, long n) Pos() { lock (_lock) return (_x, _y, _updates); }

    private async Task Report(string message, double fraction)
    {
        Progress?.Invoke(message, fraction);
        await _server.SendAsync(new { t = "progress", v = fraction, msg = message });
    }

    public async Task<GainCurve?> RunAsync(CancellationToken ct)
    {
        if (!_host.Connected) throw new InvalidOperationException("The iPad isn't connected.");
        await _host.BeginAsync();
        try
        {
            await Report("Finding the pointer…", 0.02);
            if (!await AcquirePointer(ct)) throw new InvalidOperationException("The pointer didn't appear on the calibration page. Keep the page open in Safari, full screen.");

            double vw, vh;
            lock (_lock) { vw = _vw; vh = _vh; }
            var strokes = CalibrationPlanner.MouseStrokes(vw, GainCurve.DefaultMouse);
            var samples = new List<CalibrationSample>();
            double gainGuess = 0.6;

            for (int i = 0; i < strokes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = strokes[i];
                gainGuess = await CenterX(vw, vh, gainGuess, ct);
                await Report($"Measuring speed {i / 2 + 1} of {strokes.Count / 2}…", 0.08 + 0.88 * i / strokes.Count);

                var start = await Settled(ct);
                await SendStroke(s, ct);
                var end = await Settled(ct);
                var sample = CalibrationPlanner.Evaluate(s, start.x, end.x, vw);
                if (sample != null) samples.Add(sample);
                Log.Info($"Calibration stroke {s.UnitsPerReport}×{s.Reports} dir {s.Direction}: {start.x:0.0}→{end.x:0.0} pt " +
                         (sample != null ? $"gain {sample.Gain:0.000} @ {sample.Speed:0} c/s" : "(discarded)"));
            }

            var curve = CalibrationPlanner.Fit(samples);
            if (curve == null) throw new InvalidOperationException("Not enough clean measurements. Try again with the iPad unlocked and Safari in the foreground.");
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
            await _host.EndAsync(returnToPc: true);
        }
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

    /// <summary>Closed-loop centring of X (and Y once); returns an updated gain estimate.</summary>
    private async Task<double> CenterX(double vw, double vh, double gain, CancellationToken ct)
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
                await Task.Delay(12, ct);
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
        while (quiet.ElapsedMilliseconds < 140 && total.ElapsedMilliseconds < 1500)
        {
            await Task.Delay(20, ct);
            long n = Pos().n;
            if (n != last) { last = n; quiet.Restart(); }
        }
        var p = Pos();
        return (p.x, p.y);
    }

    private Task SendStroke(CalibrationStroke s, CancellationToken ct) => Task.Run(() =>
    {
        var sw = Stopwatch.StartNew();
        int counts = (int)Math.Round(s.UnitsPerReport) * s.Direction;
        for (int i = 0; i < s.Reports; i++)
        {
            ct.ThrowIfCancellationRequested();
            var due = TimeSpan.FromSeconds(i * CalibrationPlanner.ReportInterval);
            while (sw.Elapsed < due) Thread.Sleep(1);
            _host.Move(counts, 0);
        }
    }, ct);
}
