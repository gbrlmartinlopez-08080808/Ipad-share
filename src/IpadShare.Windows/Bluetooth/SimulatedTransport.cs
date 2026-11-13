using IpadShare.App.App;
using IpadShare.Core.Hid;
using IpadShare.Core.Transport;

namespace IpadShare.App.Bluetooth;

/// <summary>
/// `iPadShare.exe --simulate-ipad`: pretends an iPad is connected and logs what it would receive.
/// Lets you try switching, gestures and shortcuts without an iPad, and is used for automated testing.
/// </summary>
internal sealed class SimulatedTransport : IHidTransport
{
    private TransportState _state = TransportState.Stopped;
    private long _mouseReports, _dx, _dy, _wheel;
    private DateTime _lastSummary = DateTime.UtcNow;

    public string Name => "Simulated iPad";
    public TransportState State => _state;
    public string? StatusDetail => null;
    public string? PeerName => _state == TransportState.Connected ? "Simulated iPad" : null;
    public int MaxReportPayload => 182;

    public event EventHandler? StateChanged;
    public event EventHandler<HostReportWrite>? HostWrite;

    public async Task StartAsync(CancellationToken ct)
    {
        _state = TransportState.Advertising;
        StateChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(800, ct);
        _state = TransportState.Connected;
        Log.Info("SIM connected");
        StateChanged?.Invoke(this, EventArgs.Empty);
        HostWrite?.Invoke(this, new HostReportWrite(ReportIds.MouseResolution, HidReportType.Feature, new byte[] { 1 }));
    }

    public Task StopAsync()
    {
        _state = TransportState.Stopped;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<bool> SendInputReportAsync(byte reportId, ReadOnlyMemory<byte> data)
    {
        var d = data.Span;
        switch (reportId)
        {
            case ReportIds.Keyboard:
                Log.Info($"SIM key mods={d[0]:X2} globe={d[1]} keys={Convert.ToHexString(d[2..])}");
                break;
            case ReportIds.Consumer:
                Log.Info($"SIM consumer {BitConverter.ToUInt16(d):X4}");
                break;
            case ReportIds.Mouse:
                _mouseReports++;
                _dx += BitConverter.ToInt16(d[1..]); _dy += BitConverter.ToInt16(d[3..]); _wheel += BitConverter.ToInt16(d[5..]);
                if (d[0] != 0 || (DateTime.UtcNow - _lastSummary).TotalMilliseconds > 400)
                {
                    Log.Info($"SIM mouse buttons={d[0]} reports={_mouseReports} sum dx={_dx} dy={_dy} wheel={_wheel}");
                    _lastSummary = DateTime.UtcNow;
                }
                break;
            case ReportIds.Touchpad:
                Log.Info($"SIM touch contacts={d[ReportMap.TouchContacts * 5 + 2]} button={d[ReportMap.TouchContacts * 5 + 3]}");
                break;
        }
        return Task.FromResult(_state == TransportState.Connected);
    }

    public void SetBatteryLevel(int percent) => Log.Info($"SIM battery {percent}%");
    public string Describe() => $"Transport: {Name}\n  State: {_state}, mouse reports {_mouseReports}, dx {_dx}, dy {_dy}";
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
