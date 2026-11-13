using IpadShare.Core.Hid;

namespace IpadShare.Core.Transport;

public enum TransportState
{
    /// <summary>Not usable on this machine (no adapter, no peripheral role, no bridge).</summary>
    Unavailable,
    Stopped,
    Starting,
    /// <summary>Waiting for the iPad (advertising).</summary>
    Advertising,
    Connected,
    Error,
}

/// <summary>A host write to one of our reports (feature or output), e.g. iPadOS enabling hi-res scrolling.</summary>
public readonly record struct HostReportWrite(byte ReportId, HidReportType Type, byte[] Data);

/// <summary>Something that can deliver HID reports to the iPad (built-in Bluetooth, USB bridge…).</summary>
public interface IHidTransport : IAsyncDisposable
{
    string Name { get; }
    TransportState State { get; }
    /// <summary>Human-readable reason for Unavailable/Error.</summary>
    string? StatusDetail { get; }
    /// <summary>Name of the connected iPad, when known.</summary>
    string? PeerName { get; }
    /// <summary>Largest input-report payload that fits one notification (ATT MTU − 3).</summary>
    int MaxReportPayload { get; }

    event EventHandler? StateChanged;
    event EventHandler<HostReportWrite>? HostWrite;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();

    /// <summary>Sends one input report. Returns false if it could not be delivered.</summary>
    Task<bool> SendInputReportAsync(byte reportId, ReadOnlyMemory<byte> data);

    void SetBatteryLevel(int percent);

    /// <summary>Multi-line diagnostic text.</summary>
    string Describe();
}
