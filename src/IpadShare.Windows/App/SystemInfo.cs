using Microsoft.Win32;
using IpadShare.Core.Handoff;
using static IpadShare.App.Native.NativeMethods;

namespace IpadShare.App.App;

internal sealed record MonitorInfo(string Device, RectD Bounds, bool Primary, SizeD PhysicalMm, bool PhysicalKnown, uint Dpi)
{
    public string Label(int index) => Primary ? "This screen" : $"Display {index + 1}";
}

/// <summary>Windows facts the app adapts to: monitors, battery, touchpad preferences, secure desktop.</summary>
internal static class SystemInfo
{
    /// <summary>All monitors in physical pixels, with physical size from EDID-based raw DPI when available.</summary>
    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        MonitorEnumProc proc = (IntPtr h, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref mi)) return true;
            var b = new RectD(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
            uint effDpi = 96;
            SizeD mm;
            bool known = false;
            try
            {
                GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out effDpi, out _);
                if (GetDpiForMonitor(h, MDT_RAW_DPI, out uint rx, out uint ry) == 0 && rx > 50 && ry > 50 && rx < 1000 && ry < 1000)
                {
                    mm = new SizeD(b.Width / rx * 25.4, b.Height / ry * 25.4);
                    known = mm.Width > 100 && mm.Width < 2000;
                }
                else mm = default;
            }
            catch { mm = default; }
            if (!known)
            {
                // Assume a 14" 16:10 laptop panel for the primary, 24" for others.
                double diagIn = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0 ? 14 : 24;
                double aspect = b.Width / Math.Max(1, b.Height);
                double hIn = diagIn / Math.Sqrt(1 + aspect * aspect);
                mm = new SizeD(hIn * aspect * 25.4, hIn * 25.4);
            }
            list.Add(new MonitorInfo(mi.szDevice, b, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0, mm, known, effDpi));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        list.Sort((a, b) => a.Primary == b.Primary ? a.Bounds.X.CompareTo(b.Bounds.X) : a.Primary ? -1 : 1);
        return list;
    }

    /// <summary>Battery percentage, or null on desktops.</summary>
    public static int? BatteryPercent()
    {
        try
        {
            var ps = SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) || ps.BatteryLifePercent > 1.0f) return null;
            return (int)Math.Round(ps.BatteryLifePercent * 100);
        }
        catch { return null; }
    }

    /// <summary>The user's Windows Precision Touchpad preferences (null = setting not found).</summary>
    public sealed record TouchpadPrefs(bool? Taps, bool? TwoFingerTap, bool? RightClickZone, bool? TapAndDrag, int? ThreeFingerSlide);

    public static TouchpadPrefs ReadTouchpadPrefs()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
            if (k == null) return new TouchpadPrefs(null, null, null, null, null);
            bool? B(string name) => k.GetValue(name) is int v ? v != 0 : null;
            int? I(string name) => k.GetValue(name) is int v ? v : null;
            return new TouchpadPrefs(B("TapsEnabled"), B("TwoFingerTapEnabled"), B("RightClickZoneEnabled"), B("TapAndDrag"), I("ThreeFingerSlideEnabled"));
        }
        catch { return new TouchpadPrefs(null, null, null, null, null); }
    }

    /// <summary>True when the lock screen, UAC or another secure desktop has the input.</summary>
    public static bool SecureDesktopActive()
    {
        IntPtr d = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (d == IntPtr.Zero) return true;   // can't open → not our desktop
        try
        {
            var buf = new char[256];
            if (!GetUserObjectInformation(d, UOI_NAME, buf, (uint)(buf.Length * 2), out _)) return false;
            var name = new string(buf).TrimEnd('\0');
            return !name.Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(d); }
    }

    public static bool IsDarkMode()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static Color AccentColor()
    {
        try
        {
            if (DwmGetColorizationColor(out uint c, out _) == 0)
            {
                var col = Color.FromArgb(255, (int)((c >> 16) & 0xFF), (int)((c >> 8) & 0xFF), (int)(c & 0xFF));
                // Very dark or very light accents read poorly; fall back to iOS blue.
                float b = col.GetBrightness();
                if (b > 0.2f && b < 0.85f) return col;
            }
        }
        catch { }
        return Color.FromArgb(10, 132, 255);
    }
}

/// <summary>Start-with-Windows via the per-user Run key (no admin rights needed).</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "iPadShare";

    public static void Apply(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) k.SetValue(Name, $"\"{exe}\" --background");
            }
            else if (k.GetValue(Name) != null) k.DeleteValue(Name);
        }
        catch (Exception ex) { Log.Warn("Startup registration failed: " + ex.Message); }
    }
}

/// <summary>One running copy; launching again shows the existing window.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly RegisteredWaitHandle? _wait;

    public bool IsFirst { get; }

    public SingleInstance(Action onShowRequested)
    {
        _mutex = new Mutex(true, @"Local\iPadShare.SingleInstance", out bool created);
        IsFirst = created;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\iPadShare.Show");
        if (IsFirst)
            _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => onShowRequested(), null, -1, false);
        else
            _show.Set();
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show.Dispose();
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
    }
}
