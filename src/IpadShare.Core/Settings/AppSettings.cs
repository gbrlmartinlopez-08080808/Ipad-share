using System.Text.Json;
using System.Text.Json.Serialization;
using IpadShare.Core.Handoff;
using IpadShare.Core.Input;
using IpadShare.Core.Touch;

namespace IpadShare.Core.Settings;

public enum TrackpadMode
{
    /// <summary>The laptop recognises gestures and sends iPadOS shortcuts (always works).</summary>
    Translated,
    /// <summary>Raw fingers are forwarded as a multi-touch trackpad; iPadOS does the gestures.</summary>
    Native,
}

public enum TransportPreference { Auto, BuiltInBluetooth, UsbBridge }

public enum CrossingResistance { Low, Medium, High }

public sealed class ArrangementSettings
{
    /// <summary>Windows monitor device name the iPad is attached to (e.g. \\.\DISPLAY1); null = primary.</summary>
    public string? MonitorDevice { get; set; }
    public EdgeSide Side { get; set; } = EdgeSide.Right;
    /// <summary>Offset of the iPad's edge start along the monitor edge, as a fraction of the monitor edge. Null = centred.</summary>
    public double? Offset { get; set; }
    public string IpadModel { get; set; } = Handoff.IpadModel.IpadAirM1.Name;
    public bool Portrait { get; set; }
}

public sealed class CalibrationData
{
    public List<double[]>? MousePoints { get; set; }   // [speed, gain]
    public List<double[]>? TouchPoints { get; set; }
    public DateTime? CalibratedAt { get; set; }
    public string? IpadModel { get; set; }

    [JsonIgnore] public bool HasMouse => MousePoints is { Count: > 0 };

    public GainCurve MouseCurve() =>
        HasMouse ? new GainCurve(MousePoints!.Where(p => p.Length == 2).Select(p => (p[0], p[1]))) : GainCurve.DefaultMouse;

    public GainCurve TouchCurve() =>
        TouchPoints is { Count: > 0 } ? new GainCurve(TouchPoints.Where(p => p.Length == 2).Select(p => (p[0], p[1]))) : GainCurve.DefaultTouch;

    public static List<double[]> FromCurve(GainCurve c) => c.Points.Select(p => new[] { p.Speed, p.Gain }).ToList();
}

/// <summary>All user settings, persisted as JSON in %APPDATA%\iPadShare\settings.json.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool FirstRunCompleted { get; set; }

    // Connection
    public TransportPreference Transport { get; set; } = TransportPreference.Auto;
    public string? BridgePort { get; set; }

    // Behaviour
    public bool SharingEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ShowEdgeGlow { get; set; } = true;
    public CrossingResistance Resistance { get; set; } = CrossingResistance.Medium;
    public bool HotkeyEnabled { get; set; } = true;   // Ctrl+Alt+Space

    public ArrangementSettings Arrangement { get; set; } = new();

    // Trackpad
    public TrackpadMode TrackpadMode { get; set; } = TrackpadMode.Translated;
    public bool NativeModeVerified { get; set; }
    public double PointerSpeed { get; set; } = 1.0;
    public double ScrollSpeed { get; set; } = 1.0;
    public bool ReverseScroll { get; set; }
    public bool Momentum { get; set; } = true;
    public bool PinchToZoom { get; set; } = true;
    public bool MultiFingerGestures { get; set; } = true;
    /// <summary>Null = follow the Windows touchpad settings (tap to click, etc.).</summary>
    public bool? TapToClick { get; set; }
    public bool? TwoFingerTapRightClick { get; set; }
    public bool? TapAndDrag { get; set; }
    public bool? RightClickZone { get; set; }
    public double MouseSpeed { get; set; } = 1.0;

    /// <summary>What each gesture sends to the iPad (shortcut strings, "None" to disable).</summary>
    public Dictionary<GestureKind, string> GestureActions { get; set; } = DefaultGestureActions();

    // Keyboard
    public KeyboardStyle KeyboardStyle { get; set; } = KeyboardStyle.Windows;
    public bool SwapIsoKeys { get; set; }
    public bool WinTapOpensSpotlight { get; set; } = true;

    public CalibrationData Calibration { get; set; } = new();

    public static Dictionary<GestureKind, string> DefaultGestureActions() => new()
    {
        [GestureKind.ThreeSwipeUp] = "Cmd+H",                 // Home
        [GestureKind.ThreeSwipeUpHold] = "Globe+Up",          // App Switcher
        [GestureKind.ThreeSwipeDown] = "Globe+N",             // Notification Center
        [GestureKind.ThreeSwipeLeft] = "Cmd+Tab",             // last app
        [GestureKind.ThreeSwipeRight] = "Cmd+Tab",
        [GestureKind.FourSwipeUp] = "Globe+Up",
        [GestureKind.FourSwipeDown] = "Cmd+H",
        [GestureKind.FourSwipeLeft] = "Cmd+Tab",
        [GestureKind.FourSwipeRight] = "Cmd+Tab",
        [GestureKind.ThreeFingerTap] = "Cmd+Space",           // Spotlight
        [GestureKind.FourFingerTap] = "Globe+C",              // Control Center
        [GestureKind.PinchOut] = "Cmd+Equal",                 // zoom in
        [GestureKind.PinchIn] = "Cmd+Minus",                  // zoom out
    };

    public double PushThreshold => Resistance switch
    {
        CrossingResistance.Low => 25,
        CrossingResistance.High => 140,
        _ => 60,
    };

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static AppSettings FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        s.Normalize();
        return s;
    }

    /// <summary>Clamp values and fill anything missing (old or hand-edited files).</summary>
    public void Normalize()
    {
        PointerSpeed = Clamp(PointerSpeed, 0.25, 3, 1);
        ScrollSpeed = Clamp(ScrollSpeed, 0.25, 3, 1);
        MouseSpeed = Clamp(MouseSpeed, 0.25, 3, 1);
        Arrangement ??= new ArrangementSettings();
        if (Arrangement.Offset is double o && (!double.IsFinite(o) || o < -3 || o > 3)) Arrangement.Offset = null;
        Calibration ??= new CalibrationData();
        GestureActions ??= DefaultGestureActions();
        foreach (var kv in DefaultGestureActions())
            if (!GestureActions.ContainsKey(kv.Key)) GestureActions[kv.Key] = kv.Value;
    }

    private static double Clamp(double v, double lo, double hi, double fallback) =>
        double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    public AppSettings Clone() => FromJson(ToJson());
}

/// <summary>Loads/saves settings atomically (write temp + replace) so a crash never corrupts them.</summary>
public sealed class SettingsStore
{
    public SettingsStore(string path) => Path = path;

    public string Path { get; }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path)) return AppSettings.FromJson(File.ReadAllText(Path));
        }
        catch (Exception)
        {
            // Corrupt file: keep a copy for diagnostics and start fresh.
            try { File.Copy(Path, Path + ".corrupt", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, settings.ToJson());
        File.Move(tmp, Path, overwrite: true);
    }
}
