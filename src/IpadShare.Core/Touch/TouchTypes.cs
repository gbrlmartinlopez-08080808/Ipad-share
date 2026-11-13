namespace IpadShare.Core.Touch;

/// <summary>One finger as reported by the laptop touchpad, in millimetres from the pad's top-left corner.</summary>
public readonly record struct TouchPoint(int Id, double X, double Y, bool Tip, bool Confidence);

/// <summary>A complete touchpad frame (all fingers at one instant).</summary>
public sealed class TouchFrame
{
    public TouchFrame(double time, IReadOnlyList<TouchPoint> contacts, bool button)
    {
        Time = time;
        Contacts = contacts;
        Button = button;
    }

    /// <summary>Seconds on a monotonic clock.</summary>
    public double Time { get; }
    public IReadOnlyList<TouchPoint> Contacts { get; }
    /// <summary>Physical clickpad button.</summary>
    public bool Button { get; }

    public int ActiveCount
    {
        get { int n = 0; foreach (var c in Contacts) if (c.Tip && c.Confidence) n++; return n; }
    }

    public static TouchFrame Empty(double time) => new(time, Array.Empty<TouchPoint>(), false);
}

/// <summary>Physical size of the laptop touchpad.</summary>
public readonly record struct TouchpadGeometry(double WidthMm, double HeightMm)
{
    /// <summary>A typical 14" laptop clickpad, used until the real size is known.</summary>
    public static readonly TouchpadGeometry Default = new(115, 72);
    public bool IsValid => WidthMm > 10 && HeightMm > 10 && WidthMm < 300 && HeightMm < 300;
}

/// <summary>Multi-finger gestures recognised in Translated mode.</summary>
public enum GestureKind
{
    None,
    ThreeFingerTap,
    FourFingerTap,
    ThreeSwipeUp,
    ThreeSwipeUpHold,
    ThreeSwipeDown,
    ThreeSwipeLeft,
    ThreeSwipeRight,
    FourSwipeUp,
    FourSwipeDown,
    FourSwipeLeft,
    FourSwipeRight,
    /// <summary>Fingers spreading apart (zoom in).</summary>
    PinchOut,
    /// <summary>Fingers moving together (zoom out).</summary>
    PinchIn,
}
