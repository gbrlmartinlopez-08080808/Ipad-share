using IpadShare.App.App;
using IpadShare.Core.Settings;
using IpadShare.Core.Transport;

namespace IpadShare.App.Bluetooth;

/// <summary>
/// Picks and supervises the link to the iPad: a plugged-in USB bridge is preferred (most
/// controllable), otherwise the laptop's built-in Bluetooth. Re-evaluates when the bridge is
/// plugged/unplugged or Bluetooth is toggled. Exposes itself as a single transport.
/// </summary>
internal sealed class TransportManager : IHidTransport
{
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private IHidTransport? _active;
    private TransportPreference _preference;
    private string? _bridgePort;
    private CancellationTokenSource? _cts;
    private Task? _monitor;

    public TransportManager(TransportPreference preference, string? bridgePort)
    {
        _preference = preference;
        _bridgePort = bridgePort;
    }

    /// <summary>Use a simulated iPad instead of real hardware (--simulate-ipad).</summary>
    public static bool Simulate { get; set; }

    public IHidTransport? Active => _active;
    public string Name => _active?.Name ?? "None";
    public TransportState State => _active?.State ?? TransportState.Stopped;
    public string? StatusDetail => _active?.StatusDetail;
    public string? PeerName => _active?.PeerName;
    public int MaxReportPayload => _active?.MaxReportPayload ?? 20;
    public double? ConnectionIntervalMs => _active?.ConnectionIntervalMs;

    public event EventHandler? StateChanged;
    public event EventHandler<HostReportWrite>? HostWrite;

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await SelectAsync(_cts.Token);
        _monitor = Task.Run(() => MonitorAsync(_cts.Token));
    }

    public async Task ReconfigureAsync(TransportPreference preference, string? bridgePort)
    {
        _preference = preference;
        _bridgePort = bridgePort;
        if (_cts != null) await SelectAsync(_cts.Token, force: true);
    }

    private async Task SelectAsync(CancellationToken ct, bool force = false)
    {
        await _switchLock.WaitAsync(ct);
        try
        {
            if (Simulate)
            {
                if (_active is not SimulatedTransport) await SwitchTo(new SimulatedTransport(), ct);
                return;
            }
            bool bridgePresent = !string.IsNullOrEmpty(_bridgePort) || BridgeTransport.FindBridgePorts().Count > 0;
            bool wantBridge = _preference == TransportPreference.UsbBridge
                              || (_preference == TransportPreference.Auto && bridgePresent);
            bool haveBridge = _active is BridgeTransport;
            if (!force && _active != null && wantBridge == haveBridge) return;   // same kind: retried by the monitor

            await SwitchTo(wantBridge ? new BridgeTransport(_bridgePort) : new BleHidPeripheral(), ct);

            // Auto: if the bridge didn't answer, fall back to built-in Bluetooth.
            if (wantBridge && _preference == TransportPreference.Auto && _active!.State == TransportState.Unavailable)
                await SwitchTo(new BleHidPeripheral(), ct);
        }
        finally { _switchLock.Release(); }
    }

    private async Task SwitchTo(IHidTransport next, CancellationToken ct)
    {
        var old = _active;
        if (old != null)
        {
            old.StateChanged -= OnChildState;
            old.HostWrite -= OnChildWrite;
            try { await old.DisposeAsync(); } catch (Exception ex) { Log.Warn("Transport dispose: " + ex.Message); }
        }
        _active = next;
        next.StateChanged += OnChildState;
        next.HostWrite += OnChildWrite;
        Log.Info($"Using transport: {next.Name}");
        try { await next.StartAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error("Transport start", ex); }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(3);
        var nextRetry = DateTime.UtcNow + backoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                var state = _active?.State;
                if (state is TransportState.Connected or TransportState.Advertising)
                {
                    backoff = TimeSpan.FromSeconds(3);
                    if (state == TransportState.Connected) continue;
                }
                await SelectAsync(ct);   // switches only if the bridge was plugged in or removed

                // Retry a transport that is unavailable/failed (Bluetooth switched back on, driver restarted…)
                // with exponential back-off so a PC without Bluetooth isn't hammered.
                if (_active?.State is TransportState.Unavailable or TransportState.Error && DateTime.UtcNow >= nextRetry)
                {
                    await _switchLock.WaitAsync(ct);
                    try
                    {
                        if (_active?.State is TransportState.Unavailable or TransportState.Error)
                            await _active.StartAsync(ct);
                    }
                    finally { _switchLock.Release(); }
                    backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
                    nextRetry = DateTime.UtcNow + backoff;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn("Transport monitor: " + ex.Message); }
        }
    }

    private void OnChildState(object? sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);
    private void OnChildWrite(object? sender, HostReportWrite w) => HostWrite?.Invoke(this, w);

    public Task<bool> SendInputReportAsync(byte reportId, ReadOnlyMemory<byte> data) =>
        _active?.SendInputReportAsync(reportId, data) ?? Task.FromResult(false);

    public void SetBatteryLevel(int percent) => _active?.SetBatteryLevel(percent);

    public string Describe() => _active?.Describe() ?? "Transport: none";

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_monitor != null) { try { await _monitor; } catch { } }
        if (_active != null) await _active.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_active != null) await _active.DisposeAsync();
    }
}
