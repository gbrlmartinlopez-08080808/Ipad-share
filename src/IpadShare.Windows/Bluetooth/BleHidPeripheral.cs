using System.Runtime.InteropServices.WindowsRuntime;
using IpadShare.App.App;
using IpadShare.Core.Hid;
using IpadShare.Core.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Foundation.Metadata;
using Windows.Storage.Streams;

namespace IpadShare.App.Bluetooth;

/// <summary>
/// Makes the laptop's own Bluetooth adapter a Bluetooth LE HID device (HID over GATT Profile)
/// through WinRT's GattServiceProvider. Requires an adapter that supports the LE peripheral role.
/// The iPad pairs with it like any keyboard/trackpad and reconnects automatically afterwards.
/// </summary>
internal sealed class BleHidPeripheral : IHidTransport
{
    private static readonly Guid HidServiceUuid = BluetoothUuidHelper.FromShortId(0x1812);
    private static readonly Guid BatteryServiceUuid = BluetoothUuidHelper.FromShortId(0x180F);
    private static readonly Guid HidInformationUuid = BluetoothUuidHelper.FromShortId(0x2A4A);
    private static readonly Guid ReportMapUuid = BluetoothUuidHelper.FromShortId(0x2A4B);
    private static readonly Guid HidControlPointUuid = BluetoothUuidHelper.FromShortId(0x2A4C);
    private static readonly Guid ReportUuid = BluetoothUuidHelper.FromShortId(0x2A4D);
    private static readonly Guid ProtocolModeUuid = BluetoothUuidHelper.FromShortId(0x2A4E);
    private static readonly Guid BatteryLevelUuid = BluetoothUuidHelper.FromShortId(0x2A19);
    private static readonly Guid ReportReferenceUuid = BluetoothUuidHelper.FromShortId(0x2908);

    private readonly object _gate = new();
    private GattServiceProvider? _hid, _battery;
    // Replaced atomically on (re)publish: the scheduler thread reads it while Bluetooth restarts.
    private volatile Dictionary<byte, GattLocalCharacteristic> _inputs = new();
    private readonly Dictionary<byte, byte[]> _features = new();
    private readonly Dictionary<byte, byte[]> _lastInput = new();
    private GattLocalCharacteristic? _batteryLevel;
    private BluetoothAdapter? _adapter;
    private Radio? _radio;
    private GattSession? _session;
    private byte _batteryPercent = 100;
    private TransportState _state = TransportState.Stopped;
    private string? _detail;
    private string? _peer;
    private int _mtu = 23;
    private bool _starting;
    private string? _lastError;
    private CancellationTokenSource? _restartCts;

    public string Name => "Built-in Bluetooth";
    public TransportState State => _state;
    public string? StatusDetail => _detail;
    public string? PeerName => _peer;
    public int MaxReportPayload => Math.Max(20, _mtu - 3);
    public string? AdapterName { get; private set; }
    public ulong AdapterAddress { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler<HostReportWrite>? HostWrite;

    /// <summary>Quick capability probe (no services published).</summary>
    public static async Task<(bool ok, string message)> ProbeAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null) return (false, "No Bluetooth adapter was found.");
            if (!adapter.IsLowEnergySupported) return (false, "This Bluetooth adapter doesn't support Bluetooth Low Energy.");
            if (!adapter.IsPeripheralRoleSupported)
                return (false, "This laptop's Bluetooth adapter can't act as a Bluetooth accessory (LE peripheral role). Use the USB bridge instead.");
            var radio = await adapter.GetRadioAsync();
            if (radio != null && radio.State != RadioState.On) return (false, "Bluetooth is turned off.");
            return (true, "Bluetooth adapter supports accessory mode.");
        }
        catch (Exception ex)
        {
            return (false, FriendlyError(ex));
        }
    }

    /// <summary>Turns WinRT/COM failures into something a person can act on.</summary>
    public static string FriendlyError(Exception ex) => ex switch
    {
        NotImplementedException or TypeLoadException or PlatformNotSupportedException =>
            "Windows Bluetooth isn't available on this PC (no compatible adapter or driver).",
        UnauthorizedAccessException =>
            "Windows blocked Bluetooth access. Check Settings › Privacy & security › Bluetooth.",
        System.Runtime.InteropServices.COMException c when (uint)c.HResult == 0x8007048F =>
            "The Bluetooth adapter is disabled or unplugged.",
        _ => "Bluetooth error: " + ex.Message,
    };

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_gate) { if (_starting) return; _starting = true; }
        try
        {
            SetState(TransportState.Starting, null);
            _adapter = await BluetoothAdapter.GetDefaultAsync();
            if (_adapter == null) { SetState(TransportState.Unavailable, "No Bluetooth adapter was found."); return; }
            AdapterAddress = _adapter.BluetoothAddress;
            if (!_adapter.IsLowEnergySupported) { SetState(TransportState.Unavailable, "The adapter doesn't support Bluetooth LE."); return; }
            if (!_adapter.IsPeripheralRoleSupported)
            {
                SetState(TransportState.Unavailable, "This Bluetooth adapter can't act as an accessory (no LE peripheral role).");
                return;
            }

            _radio = await _adapter.GetRadioAsync();
            if (_radio != null)
            {
                AdapterName = _radio.Name;
                _radio.StateChanged -= OnRadioStateChanged;
                _radio.StateChanged += OnRadioStateChanged;
                if (_radio.State != RadioState.On) { SetState(TransportState.Unavailable, "Bluetooth is turned off."); return; }
            }

            await PublishAsync();
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            string message = FriendlyError(ex);
            if (message != _lastError) Log.Error("BLE start failed", ex);   // don't repeat the same error on every retry
            _lastError = message;
            bool permanent = ex is NotImplementedException or TypeLoadException or PlatformNotSupportedException;
            SetState(permanent ? TransportState.Unavailable : TransportState.Error, message);
            await TearDownAsync();
        }
        finally
        {
            lock (_gate) _starting = false;
        }
    }

    private async Task PublishAsync()
    {
        await TearDownAsync();

        // ---- HID service ----
        var r = await GattServiceProvider.CreateAsync(HidServiceUuid);
        if (r.Error != BluetoothError.Success) throw new InvalidOperationException($"HID service: {r.Error}");
        _hid = r.ServiceProvider;
        var svc = _hid.Service;
        var inputs = new Dictionary<byte, GattLocalCharacteristic>();

        await AddStatic(svc, HidInformationUuid, ReportMap.HidInformation);
        await AddStatic(svc, ReportMapUuid, ReportMap.DescriptorCopy());

        var protocol = await Create(svc, ProtocolModeUuid, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
        });
        protocol.ReadRequested += (s, a) => Respond(a, new byte[] { 0x01 });   // report protocol
        protocol.WriteRequested += (s, a) => AcceptWrite(a, null);

        var control = await Create(svc, HidControlPointUuid, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
        });
        control.WriteRequested += (s, a) => AcceptWrite(a, v => Log.Info($"HID control point: {(v.Length > 0 && v[0] == 0 ? "suspend" : "exit suspend")}"));

        foreach (var info in ReportMap.Reports)
        {
            if (info.Type == HidReportType.Input)
            {
                var ch = await Create(svc, ReportUuid, new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                    ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                    WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
                });
                byte id = info.Id;
                int len = info.Length;
                ch.ReadRequested += (s, a) => Respond(a, LastInput(id, len));
                ch.SubscribedClientsChanged += OnSubscribedClientsChanged;
                await AddReportReference(ch, id, HidReportType.Input);
                inputs[id] = ch;
            }
            else if (info.Type == HidReportType.Feature)
            {
                var ch = await Create(svc, ReportUuid, new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Write,
                    ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                    WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
                });
                byte id = info.Id;
                _features[id] = id == ReportIds.TouchpadCaps ? new[] { ReportMap.TouchpadCapsValue } : new byte[info.Length];
                ch.ReadRequested += (s, a) =>
                {
                    Log.Info($"iPad read feature report {id}");
                    Respond(a, _features[id]);
                };
                ch.WriteRequested += (s, a) => AcceptWrite(a, v =>
                {
                    _features[id] = v;
                    Log.Info($"iPad wrote feature report {id}: {Convert.ToHexString(v)}");
                    HostWrite?.Invoke(this, new HostReportWrite(id, HidReportType.Feature, v));
                });
                await AddReportReference(ch, id, HidReportType.Feature);
            }
        }

        // ---- Battery service (shows the laptop in the iPad's Batteries widget) ----
        try
        {
            var b = await GattServiceProvider.CreateAsync(BatteryServiceUuid);
            if (b.Error == BluetoothError.Success)
            {
                _battery = b.ServiceProvider;
                _batteryLevel = await Create(_battery.Service, BatteryLevelUuid, new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                    ReadProtectionLevel = GattProtectionLevel.Plain,
                });
                _batteryLevel.ReadRequested += (s, a) => Respond(a, new[] { _batteryPercent });
            }
        }
        catch (Exception ex) { Log.Warn("Battery service unavailable: " + ex.Message); _battery = null; }

        _inputs = inputs;
        _hid.AdvertisementStatusChanged += OnAdvertisementStatusChanged;
        _hid.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });
        _battery?.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = false });
        _lastError = null;
        Log.Info($"BLE HID published ({ReportMap.Descriptor.Length}-byte report map), advertising as \"{AdapterName ?? Environment.MachineName}\"");
        SetState(TransportState.Advertising, null);
    }

    private static async Task<GattLocalCharacteristic> Create(GattLocalService svc, Guid uuid, GattLocalCharacteristicParameters p)
    {
        var r = await svc.CreateCharacteristicAsync(uuid, p);
        if (r.Error != BluetoothError.Success) throw new InvalidOperationException($"characteristic {uuid}: {r.Error}");
        return r.Characteristic;
    }

    private static async Task AddStatic(GattLocalService svc, Guid uuid, byte[] value)
    {
        await Create(svc, uuid, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            StaticValue = value.AsBuffer(),
        });
    }

    private static async Task AddReportReference(GattLocalCharacteristic ch, byte id, HidReportType type)
    {
        var r = await ch.CreateDescriptorAsync(ReportReferenceUuid, new GattLocalDescriptorParameters
        {
            StaticValue = new byte[] { id, (byte)type }.AsBuffer(),
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
        });
        if (r.Error != BluetoothError.Success) throw new InvalidOperationException($"report reference {id}: {r.Error}");
    }

    private byte[] LastInput(byte id, int len)
    {
        lock (_gate) return _lastInput.TryGetValue(id, out var v) ? v : new byte[len];
    }

    private static async void Respond(GattReadRequestedEventArgs args, byte[] value)
    {
        var deferral = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            if (req == null) return;
            int offset = (int)Math.Min(req.Offset, (uint)value.Length);
            req.RespondWithValue(value.AsBuffer(offset, value.Length - offset));
        }
        catch (Exception ex) { Log.Warn("GATT read: " + ex.Message); }
        finally { deferral.Complete(); }
    }

    private static async void AcceptWrite(GattWriteRequestedEventArgs args, Action<byte[]>? onValue)
    {
        var deferral = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            if (req == null) return;
            var reader = DataReader.FromBuffer(req.Value);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            onValue?.Invoke(bytes);
            if (req.Option == GattWriteOption.WriteWithResponse) req.Respond();
        }
        catch (Exception ex) { Log.Warn("GATT write: " + ex.Message); }
        finally { deferral.Complete(); }
    }

    // ---------------- connection tracking ----------------
    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args) => _ = UpdateConnectionAsync();

    private async Task UpdateConnectionAsync()
    {
        try
        {
            GattSubscribedClient? client = null;
            foreach (var ch in _inputs.Values)
            {
                if (ch.SubscribedClients.Count > 0) { client = ch.SubscribedClients[0]; break; }
            }

            if (client == null)
            {
                if (_state == TransportState.Connected)
                {
                    Log.Info("iPad disconnected");
                    _peer = null;
                    if (_session != null) _session.MaxPduSizeChanged -= OnMtuChanged;
                    _session = null;
                    SetState(TransportState.Advertising, null);
                }
                return;
            }

            if (_state == TransportState.Connected && _session != null && ReferenceEquals(_session, client.Session)) return;

            _session = client.Session;
            _session.MaxPduSizeChanged -= OnMtuChanged;
            _session.MaxPduSizeChanged += OnMtuChanged;
            _mtu = _session.MaxPduSize;
            string? name = null;
            try
            {
                using var dev = await BluetoothLEDevice.FromIdAsync(_session.DeviceId.Id);
                name = dev?.Name;
                if (dev != null) RequestLowLatency(dev);
            }
            catch (Exception ex) { Log.Warn("Peer lookup: " + ex.Message); }
            _peer = string.IsNullOrWhiteSpace(name) ? "iPad" : name;
            Log.Info($"iPad connected: {_peer}, ATT MTU {_mtu}");
            SetState(TransportState.Connected, null);
        }
        catch (Exception ex) { Log.Error("Connection update", ex); }
    }

    private static void RequestLowLatency(BluetoothLEDevice dev)
    {
        // Windows 11 (22000+): ask for the shortest connection interval the iPad accepts.
        try
        {
            if (!ApiInformation.IsMethodPresent("Windows.Devices.Bluetooth.BluetoothLEDevice", "RequestPreferredConnectionParameters")) return;
            var result = dev.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
            Log.Info($"Low-latency connection request: {result.Status}");
        }
        catch (Exception ex) { Log.Warn("Connection parameter request failed: " + ex.Message); }
    }

    private void OnMtuChanged(GattSession sender, object args)
    {
        _mtu = sender.MaxPduSize;
        Log.Info($"ATT MTU now {_mtu}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnAdvertisementStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        Log.Info($"Advertising: {args.Status} ({args.Error})");
        if (args.Status == GattServiceProviderAdvertisementStatus.Aborted)
        {
            SetState(TransportState.Error, $"Bluetooth advertising stopped ({args.Error}). Retrying…");
            ScheduleRestart(TimeSpan.FromSeconds(3));
        }
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        Log.Info($"Bluetooth radio: {sender.State}");
        if (sender.State == RadioState.On) ScheduleRestart(TimeSpan.FromSeconds(1));
        else
        {
            _ = TearDownAsync();
            SetState(TransportState.Unavailable, "Bluetooth is turned off.");
        }
    }

    private void ScheduleRestart(TimeSpan delay)
    {
        _restartCts?.Cancel();
        var cts = _restartCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                await StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        });
    }

    // ---------------- sending ----------------
    public async Task<bool> SendInputReportAsync(byte reportId, ReadOnlyMemory<byte> data)
    {
        if (!_inputs.TryGetValue(reportId, out var ch)) return false;
        var bytes = data.ToArray();
        lock (_gate) _lastInput[reportId] = bytes;
        if (_state != TransportState.Connected) return false;
        if (bytes.Length > MaxReportPayload) return false;
        try
        {
            var results = await ch.NotifyValueAsync(bytes.AsBuffer());
            foreach (var r in results) if (r.Status == GattCommunicationStatus.Success) return true;
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Notify report {reportId}: {ex.Message}");
            return false;
        }
    }

    public void SetBatteryLevel(int percent)
    {
        byte p = (byte)Math.Clamp(percent, 0, 100);
        if (p == _batteryPercent) return;
        _batteryPercent = p;
        var ch = _batteryLevel;
        if (ch != null && ch.SubscribedClients.Count > 0)
            _ = ch.NotifyValueAsync(new[] { p }.AsBuffer()).AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
    }

    // ---------------- lifecycle ----------------
    public async Task StopAsync()
    {
        _restartCts?.Cancel();
        if (_radio != null) _radio.StateChanged -= OnRadioStateChanged;
        await TearDownAsync();
        SetState(TransportState.Stopped, null);
    }

    private Task TearDownAsync()
    {
        try
        {
            if (_hid != null)
            {
                _hid.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
                if (_hid.AdvertisementStatus != GattServiceProviderAdvertisementStatus.Stopped) _hid.StopAdvertising();
            }
            if (_battery != null && _battery.AdvertisementStatus != GattServiceProviderAdvertisementStatus.Stopped) _battery.StopAdvertising();
        }
        catch (Exception ex) { Log.Warn("Stop advertising: " + ex.Message); }
        foreach (var ch in _inputs.Values) ch.SubscribedClientsChanged -= OnSubscribedClientsChanged;
        _inputs = new Dictionary<byte, GattLocalCharacteristic>();
        _hid = null;
        _battery = null;
        _batteryLevel = null;
        _session = null;
        _peer = null;
        return Task.CompletedTask;
    }

    private void SetState(TransportState s, string? detail)
    {
        bool changed = s != _state || detail != _detail;
        _state = s;
        _detail = detail;
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Describe()
    {
        return $"Transport: {Name}\n" +
               $"  State: {_state}{(_detail != null ? " — " + _detail : "")}\n" +
               $"  Adapter: {AdapterName ?? "?"} ({AdapterAddress:X12}), peripheral role: {_adapter?.IsPeripheralRoleSupported}, " +
               $"LE: {_adapter?.IsLowEnergySupported}, offload: {_adapter?.IsAdvertisementOffloadSupported}\n" +
               $"  Advertising: {_hid?.AdvertisementStatus}\n" +
               $"  Peer: {_peer ?? "-"}, ATT MTU {_mtu}, max payload {MaxReportPayload}\n" +
               $"  Subscriptions: {string.Join(", ", _inputs.Select(kv => $"#{kv.Key}={kv.Value.SubscribedClients.Count}"))}";
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
