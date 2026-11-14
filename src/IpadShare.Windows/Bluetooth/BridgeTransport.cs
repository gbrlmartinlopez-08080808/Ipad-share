using System.IO.Ports;
using Microsoft.Win32;
using IpadShare.App.App;
using IpadShare.Core.Hid;
using IpadShare.Core.Transport;

namespace IpadShare.App.Bluetooth;

/// <summary>
/// Sends HID reports over USB serial to the ESP32-S3 bridge, which is the Bluetooth device the
/// iPad pairs with. Used when the laptop's own adapter can't act as a peripheral, or when chosen.
/// </summary>
internal sealed class BridgeTransport : IHidTransport
{
    public const string EspressifVid = "303A";

    private readonly string? _preferredPort;
    private SerialPort? _port;
    private Thread? _reader;
    private readonly BridgeFrameDecoder _decoder = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TaskCompletionSource<BridgeFrame>? _helloWait;
    private TransportState _state = TransportState.Stopped;
    private string? _detail;
    private int _mtu = 23;
    private int _intervalUnits;
    private volatile bool _closing;

    public BridgeTransport(string? preferredPort) => _preferredPort = preferredPort;

    public string Name => "USB bridge";
    public TransportState State => _state;
    public string? StatusDetail => _detail;
    public string? PeerName { get; private set; }
    public int MaxReportPayload => Math.Max(20, _mtu - 3);
    public double? ConnectionIntervalMs => _intervalUnits > 0 ? _intervalUnits * 1.25 : null;
    public string? PortName => _port?.PortName;
    public string? FirmwareVersion { get; private set; }
    public bool DescriptorMatches { get; private set; } = true;

    public event EventHandler? StateChanged;
    public event EventHandler<HostReportWrite>? HostWrite;

    /// <summary>COM ports belonging to Espressif USB devices (ESP32-S3 native USB).</summary>
    public static List<string> FindBridgePorts()
    {
        var ports = new List<string>();
        var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return ports;
            foreach (var dev in usb.GetSubKeyNames().Where(n => n.StartsWith("VID_" + EspressifVid, StringComparison.OrdinalIgnoreCase)))
            {
                using var dk = usb.OpenSubKey(dev);
                if (dk == null) continue;
                foreach (var inst in dk.GetSubKeyNames())
                {
                    using var pk = dk.OpenSubKey(inst + @"\Device Parameters");
                    if (pk?.GetValue("PortName") is string name && present.Contains(name) && !ports.Contains(name))
                        ports.Add(name);
                }
            }
        }
        catch (Exception ex) { Log.Warn("Bridge port scan: " + ex.Message); }
        return ports;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        SetState(TransportState.Starting, null);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(_preferredPort)) candidates.Add(_preferredPort);
        candidates.AddRange(FindBridgePorts().Where(p => !candidates.Contains(p)));
        if (candidates.Count == 0) { SetState(TransportState.Unavailable, "No USB bridge is plugged in."); return; }

        foreach (var name in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await TryOpenAsync(name, ct)) return;
        }
        SetState(TransportState.Unavailable, "The USB bridge didn't answer. Is the iPad Share firmware flashed?");
    }

    private async Task<bool> TryOpenAsync(string name, CancellationToken ct)
    {
        try
        {
            var port = new SerialPort(name, 921600)
            {
                DtrEnable = true,     // ESP32-S3 USB-CDC treats DTR as "host connected"
                RtsEnable = true,
                ReadTimeout = 500,
                WriteTimeout = 500,
            };
            port.Open();
            _port = port;
            _closing = false;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "iPadShare bridge" };
            _reader.Start();

            _helloWait = new TaskCompletionSource<BridgeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WriteFrameAsync(BridgeMessage.Hello, new[] { BridgeProtocol.ProtocolVersion });
            var done = await Task.WhenAny(_helloWait.Task, Task.Delay(1500, ct));
            if (done != _helloWait.Task) { Close(); return false; }

            var ack = _helloWait.Task.Result.Payload;
            FirmwareVersion = ack.Length >= 3 ? $"{ack[1]}.{ack[2]}" : "?";
            ushort crc = ack.Length >= 5 ? (ushort)(ack[3] | ack[4] << 8) : (ushort)0;
            DescriptorMatches = crc == BridgeProtocol.Crc16(ReportMap.Descriptor);
            if (!DescriptorMatches) Log.Warn($"Bridge firmware report map differs (crc {crc:X4}); please update the firmware");
            Log.Info($"USB bridge on {name}, firmware {FirmwareVersion}");
            await WriteFrameAsync(BridgeMessage.Advertise, new byte[] { 1 });
            SetState(TransportState.Advertising, DescriptorMatches ? null : "Bridge firmware is outdated — update it for full features.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Bridge {name}: {ex.Message}");
            Close();
            return false;
        }
    }

    private void ReadLoop()
    {
        var buf = new byte[512];
        while (!_closing)
        {
            try
            {
                var port = _port;
                if (port == null || !port.IsOpen) break;
                int n = port.Read(buf, 0, buf.Length);
                foreach (var f in _decoder.Push(buf.AsMemory(0, n))) Handle(f);
            }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                if (_closing) break;
                Log.Warn("Bridge read: " + ex.Message);
                SetState(TransportState.Error, "The USB bridge was unplugged.");
                Close();
                break;
            }
        }
    }

    private void Handle(BridgeFrame f)
    {
        switch (f.Type)
        {
            case BridgeMessage.HelloAck:
                _helloWait?.TrySetResult(f);
                break;
            case BridgeMessage.Status when f.Payload.Length >= 6:
                bool connected = f.Payload[0] != 0;
                _mtu = f.Payload[1] | f.Payload[2] << 8;
                _intervalUnits = f.Payload[3] | f.Payload[4] << 8;
                PeerName = connected ? "iPad" : null;
                SetState(connected ? TransportState.Connected : TransportState.Advertising, _detail);
                Log.Info($"Bridge status: connected={connected} mtu={_mtu} interval={_intervalUnits * 1.25:0.##} ms bonds={f.Payload[5]}");
                break;
            case BridgeMessage.HostWrite when f.Payload.Length >= 2:
                HostWrite?.Invoke(this, new HostReportWrite(f.Payload[0], (HidReportType)f.Payload[1], f.Payload[2..]));
                break;
            case BridgeMessage.Log:
                Log.Info("Bridge: " + f.PayloadText);
                break;
        }
    }

    private async Task WriteFrameAsync(BridgeMessage type, byte[] payload)
    {
        var port = _port ?? throw new InvalidOperationException("closed");
        var frame = BridgeProtocol.Encode(type, payload);
        await _writeLock.WaitAsync();
        try { await port.BaseStream.WriteAsync(frame); }
        finally { _writeLock.Release(); }
    }

    public async Task<bool> SendInputReportAsync(byte reportId, ReadOnlyMemory<byte> data)
    {
        if (_state != TransportState.Connected || _port == null) return false;
        var payload = new byte[data.Length + 1];
        payload[0] = reportId;
        data.Span.CopyTo(payload.AsSpan(1));
        try { await WriteFrameAsync(BridgeMessage.InputReport, payload); return true; }
        catch (Exception ex) { Log.Warn("Bridge write: " + ex.Message); return false; }
    }

    public void SetBatteryLevel(int percent)
    {
        if (_port == null) return;
        _ = WriteFrameAsync(BridgeMessage.Battery, new[] { (byte)Math.Clamp(percent, 0, 100) }).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public async Task ForgetBondsAsync()
    {
        if (_port != null) await WriteFrameAsync(BridgeMessage.ForgetBonds, Array.Empty<byte>());
    }

    public Task StopAsync()
    {
        Close();
        SetState(TransportState.Stopped, null);
        return Task.CompletedTask;
    }

    private void Close()
    {
        _closing = true;
        try { _port?.Close(); } catch { }
        try { _port?.Dispose(); } catch { }
        _port = null;
        PeerName = null;
    }

    private void SetState(TransportState s, string? detail)
    {
        bool changed = s != _state || detail != _detail;
        _state = s;
        _detail = detail;
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Describe() =>
        $"Transport: {Name}\n  State: {_state}{(_detail != null ? " — " + _detail : "")}\n" +
        $"  Port: {PortName ?? "-"}, firmware {FirmwareVersion ?? "?"}, report map match: {DescriptorMatches}\n" +
        $"  ATT MTU {_mtu}, interval {_intervalUnits * 1.25:0.##} ms, decoder errors {_decoder.BadFrames}";

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
