namespace IpadShare.Core.Transport;

/// <summary>
/// Debounced "is the iPad connected?" state.
/// <para>
/// iOS briefly drops and re-adds characteristic subscriptions during normal operation (reconnects,
/// encryption refresh, MTU exchange). Treating every such gap as a disconnect makes the link look
/// like it flaps, throws the pointer back to Windows and drops reports. So: connected as soon as
/// the iPad subscribes or its session is active; disconnected only after both have been gone for
/// <see cref="Grace"/> seconds.
/// </para>
/// </summary>
public sealed class LinkStateTracker
{
    private double _lostAt = double.NaN;

    /// <summary>How long subscriptions may be missing before we call it a disconnect.</summary>
    public double Grace { get; set; } = 3.0;

    public bool Connected { get; private set; }

    /// <summary>True while subscriptions are missing but the grace period hasn't expired.</summary>
    public bool Wavering => Connected && !double.IsNaN(_lostAt);

    public int Connects { get; private set; }
    public int Disconnects { get; private set; }
    /// <summary>Gaps that healed within the grace period (would have been flaps before).</summary>
    public int AbsorbedBlips { get; private set; }

    /// <summary>Feed the current observation. Returns true when <see cref="Connected"/> changed.</summary>
    public bool Update(bool subscribed, bool sessionActive, double now)
    {
        if (subscribed || sessionActive)
        {
            if (!double.IsNaN(_lostAt)) { AbsorbedBlips++; _lostAt = double.NaN; }
            if (Connected) return false;
            Connected = true;
            Connects++;
            return true;
        }

        if (!Connected) return false;
        if (double.IsNaN(_lostAt)) _lostAt = now;
        if (now - _lostAt < Grace) return false;
        Connected = false;
        _lostAt = double.NaN;
        Disconnects++;
        return true;
    }

    /// <summary>Immediate disconnect (radio off, transport stopped).</summary>
    public bool ForceDisconnected()
    {
        _lostAt = double.NaN;
        if (!Connected) return false;
        Connected = false;
        Disconnects++;
        return true;
    }
}
