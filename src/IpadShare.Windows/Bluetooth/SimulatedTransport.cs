using System.Diagnostics;
using IpadShare.App.App;
using IpadShare.Core.Handoff;
using IpadShare.Core.Hid;
using IpadShare.Core.Transport;

namespace IpadShare.App.Bluetooth;

/// <summary>
/// `iPadShare.exe --simulate-ipad`: pretends an iPad is connected and logs what it would receive.
/// It also simulates the iPad's pointer — with its own acceleration curve (deliberately different from
/// the app's estimate) and clamping at the screen edges — so switching can be tested end to end.
/// Environment variables for testing:
/// IPADSHARE_SIM_LATENCY_MS (radio send time), IPADSHARE_SIM_BLIPS=1 (iOS-style subscription gaps),
/// IPADSHARE_SIM_GAIN (true gain ÷ the app's default estimate, default 1.35).
/// </summary>
internal sealed class SimulatedTransport : IHidTransport
{
    private readonly LinkStateTracker _link = new() { Grace = 3 };
    private readonly int _latencyMs = Env("IPADSHARE_SIM_LATENCY_MS", 0);
    private readonly bool _blips = Environment.GetEnvironmentVariable("IPADSHARE_SIM_BLIPS") == "1";
    private readonly double _gainScale = Env("IPADSHARE_SIM_GAIN", 135) / 100.0;
    private static readonly bool s_verbose = Environment.GetEnvironmentVariable("IPADSHARE_SIM_VERBOSE") == "1";
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private bool _subscribed;
    private double _startedAt;
    private long _mouseReports, _dropped;
    private double _px = 590, _py = 410, _lastArrival = double.NaN, _speedEma;
    private double _lastPointerLog;
    private TransportState _reported = TransportState.Stopped;

    public string Name => "Simulated iPad";
    public TransportState State => _link.Connected ? TransportState.Connected : _reported == TransportState.Stopped ? TransportState.Stopped : TransportState.Advertising;
    public string? StatusDetail => null;
    public string? PeerName => _link.Connected ? "Simulated iPad" : null;
    public int MaxReportPayload => 182;
    public double? ConnectionIntervalMs => 15;

    public event EventHandler? StateChanged;
    public event EventHandler<HostReportWrite>? HostWrite;

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public async Task StartAsync(CancellationToken ct)
    {
        _reported = TransportState.Advertising;
        StateChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(800, ct);
        _startedAt = Now;
        _subscribed = true;
        Tick();
        HostWrite?.Invoke(this, new HostReportWrite(ReportIds.MouseResolution, HidReportType.Feature, new byte[] { 1 }));
        _timer = new System.Threading.Timer(_ => Tick(), null, 200, 200);
        Log.Info($"SIM started (latency {_latencyMs} ms, blips {_blips}, true gain ×{_gainScale})");
    }

    private void Tick()
    {
        double t = Now;
        if (_blips)
        {
            // every 6 s, iOS-style: subscriptions vanish for 1.2 s while the link stays up
            double phase = (t - _startedAt) % 6.0;
            bool sub = phase < 4.8;
            if (sub != _subscribed) { _subscribed = sub; Log.Info($"SIM subscriptions {(sub ? "restored" : "gone")}"); }
        }
        var before = State;
        if (_link.Update(_subscribed, false, t)) Log.Info($"SIM link {(_link.Connected ? "connected" : "DISCONNECTED")}");
        if (State != before) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync()
    {
        _timer?.Dispose();
        _link.ForceDisconnected();
        _reported = TransportState.Stopped;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task<bool> SendInputReportAsync(byte reportId, ReadOnlyMemory<byte> data)
    {
        var d = data.ToArray();
        if (_latencyMs > 0) await Task.Delay(_latencyMs);
        if (!_link.Connected || !_subscribed) { Interlocked.Increment(ref _dropped); return false; }
        switch (reportId)
        {
            case ReportIds.Keyboard:
                Log.Info($"SIM key mods={d[0]:X2} globe={d[1]} keys={Convert.ToHexString(d, 2, 6)}");
                break;
            case ReportIds.Consumer:
                Log.Info($"SIM consumer {BitConverter.ToUInt16(d, 0):X4}");
                break;
            case ReportIds.Mouse:
                MovePointer(d);
                break;
            case ReportIds.Touchpad:
                Log.Info($"SIM touch contacts={d[ReportMap.TouchContacts * 5 + 2]} button={d[ReportMap.TouchContacts * 5 + 3]}");
                break;
        }
        return true;
    }

    /// <summary>What an iPad would do with this mouse report: accelerate by its own curve, then clamp.</summary>
    private void MovePointer(byte[] d)
    {
        lock (_gate)
        {
            _mouseReports++;
            int dx = BitConverter.ToInt16(d, 1), dy = BitConverter.ToInt16(d, 3), wheel = BitConverter.ToInt16(d, 5);
            double t = Now;
            double dt = double.IsNaN(_lastArrival) ? 0.015 : Math.Clamp(t - _lastArrival, 0.001, 0.05);
            _lastArrival = t;
            double mag = Math.Sqrt(dx * (double)dx + dy * (double)dy);
            if (mag > 0)
            {
                double speed = mag / dt;
                _speedEma = 0.5 * _speedEma + 0.5 * speed;
                double g = GainCurve.DefaultMouse.Evaluate(_speedEma) * _gainScale;
                _px = Math.Clamp(_px + dx * g, 0, 1180);
                _py = Math.Clamp(_py + dy * g, 0, 820);
            }
            if (d[0] != 0 || wheel != 0 || s_verbose || t - _lastPointerLog > 0.3)
            {
                _lastPointerLog = t;
                Log.Info($"SIM pointer x={_px:0} y={_py:0} dx={dx} buttons={d[0]} wheel={wheel} reports={_mouseReports}");
            }
        }
    }

    public void SetBatteryLevel(int percent) => Log.Info($"SIM battery {percent}%");

    public string Describe() =>
        $"Transport: {Name}\n  State: {State}, mouse reports {_mouseReports}, dropped {_dropped}, pointer {_px:0},{_py:0}\n" +
        $"  Link: connects {_link.Connects}, disconnects {_link.Disconnects}, absorbed blips {_link.AbsorbedBlips}";

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
