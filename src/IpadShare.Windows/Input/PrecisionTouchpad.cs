using System.Runtime.InteropServices;
using IpadShare.Core.Touch;
using static IpadShare.App.Native.NativeMethods;

namespace IpadShare.App.Input;

/// <summary>
/// Reads raw finger contacts from a Windows Precision Touchpad (HID digitizer, usage page 0x0D,
/// usage 0x05) received via Raw Input. Uses the device's own report descriptor (preparsed data)
/// through hid.dll, so any compliant touchpad works. Reassembles "hybrid" reporting, where the
/// fingers of one frame are spread over several reports.
/// </summary>
internal sealed class PrecisionTouchpad : IDisposable
{
    private const ushort PageDigitizer = 0x0D, PageGeneric = 0x01, PageButton = 0x09;
    private const ushort UsageTip = 0x42, UsageConfidence = 0x47, UsageContactId = 0x51,
        UsageContactCount = 0x54, UsageScanTime = 0x56, UsageX = 0x30, UsageY = 0x31;

    private sealed record Finger(ushort LinkCollection, HIDP_VALUE_CAPS X, HIDP_VALUE_CAPS Y, bool HasContactId, bool HasConfidence);

    private readonly IntPtr _preparsed;
    private readonly List<Finger> _fingers = new();
    private readonly ushort _contactCountLink;
    private readonly bool _hasContactCount;
    private readonly ushort _buttonLink;
    private readonly bool _hasButton;
    private readonly ushort[] _usageBuffer = new ushort[32];

    // Hybrid-mode assembly
    private readonly List<TouchPoint> _pending = new();
    private int _expected;
    private bool _lastButton;

    public IntPtr Handle { get; }
    public string DeviceName { get; }
    public uint VendorId { get; }
    public uint ProductId { get; }
    public TouchpadGeometry Geometry { get; }
    public bool GeometryFromDevice { get; }
    public int MaxFingersPerReport => _fingers.Count;
    public long ReportsParsed { get; private set; }
    public long ParseErrors { get; private set; }

    private PrecisionTouchpad(IntPtr handle, string name, uint vid, uint pid, IntPtr preparsed)
    {
        Handle = handle;
        DeviceName = name;
        VendorId = vid;
        ProductId = pid;
        _preparsed = preparsed;

        if (HidP_GetCaps(preparsed, out var caps) != HIDP_STATUS_SUCCESS)
            throw new InvalidOperationException("HidP_GetCaps failed");

        ushort nv = caps.NumberInputValueCaps;
        var values = new HIDP_VALUE_CAPS[Math.Max((int)nv, 1)];
        if (nv > 0 && HidP_GetValueCaps(HIDP_REPORT_TYPE.Input, values, ref nv, preparsed) != HIDP_STATUS_SUCCESS)
            throw new InvalidOperationException("HidP_GetValueCaps failed");
        Array.Resize(ref values, nv);

        ushort nb = caps.NumberInputButtonCaps;
        var buttons = new HIDP_BUTTON_CAPS[Math.Max((int)nb, 1)];
        if (nb > 0 && HidP_GetButtonCaps(HIDP_REPORT_TYPE.Input, buttons, ref nb, preparsed) != HIDP_STATUS_SUCCESS)
            throw new InvalidOperationException("HidP_GetButtonCaps failed");
        Array.Resize(ref buttons, nb);

        static bool Covers(ushort usage, byte isRange, ushort min, ushort max) =>
            isRange != 0 ? usage >= min && usage <= max : usage == min;

        // A finger is a link collection that has both X and Y.
        foreach (var x in values.Where(v => v.UsagePage == PageGeneric && Covers(UsageX, v.IsRange, v.UsageMin, v.UsageMax)))
        {
            var y = values.FirstOrDefault(v => v.UsagePage == PageGeneric && v.LinkCollection == x.LinkCollection
                                               && Covers(UsageY, v.IsRange, v.UsageMin, v.UsageMax));
            if (y.BitSize == 0) continue;
            bool hasId = values.Any(v => v.UsagePage == PageDigitizer && v.LinkCollection == x.LinkCollection
                                         && Covers(UsageContactId, v.IsRange, v.UsageMin, v.UsageMax));
            bool hasConf = buttons.Any(b => b.UsagePage == PageDigitizer && b.LinkCollection == x.LinkCollection
                                            && Covers(UsageConfidence, b.IsRange, b.UsageMin, b.UsageMax));
            bool hasTip = buttons.Any(b => b.UsagePage == PageDigitizer && b.LinkCollection == x.LinkCollection
                                           && Covers(UsageTip, b.IsRange, b.UsageMin, b.UsageMax));
            if (!hasTip) continue;
            _fingers.Add(new Finger(x.LinkCollection, x, y, hasId, hasConf));
        }
        if (_fingers.Count == 0) throw new InvalidOperationException("no finger collections");

        var cc = values.FirstOrDefault(v => v.UsagePage == PageDigitizer && Covers(UsageContactCount, v.IsRange, v.UsageMin, v.UsageMax));
        _hasContactCount = cc.BitSize > 0;
        _contactCountLink = cc.LinkCollection;

        var btn = buttons.FirstOrDefault(b => b.UsagePage == PageButton && Covers(1, b.IsRange, b.UsageMin, b.UsageMax));
        _hasButton = btn.UsagePage == PageButton;
        _buttonLink = btn.LinkCollection;

        var (g, fromDevice) = ComputeGeometry(_fingers[0].X, _fingers[0].Y);
        Geometry = g;
        GeometryFromDevice = fromDevice;
    }

    /// <summary>Physical size from the X/Y physical extents and units (cm or inch, with exponent).</summary>
    private static (TouchpadGeometry, bool) ComputeGeometry(HIDP_VALUE_CAPS x, HIDP_VALUE_CAPS y)
    {
        double? Mm(HIDP_VALUE_CAPS c)
        {
            double range = c.PhysicalMax - c.PhysicalMin;
            if (range <= 0) return null;
            int exp = (int)(c.UnitsExp & 0xF);
            if (exp >= 8) exp -= 16;
            double scale = Math.Pow(10, exp);
            uint system = c.Units & 0xF, length = (c.Units >> 4) & 0xF;
            if (length != 1) return null;
            return system switch
            {
                1 => range * scale * 10,     // SI linear: centimetres
                3 => range * scale * 25.4,   // English linear: inches
                _ => null,
            };
        }
        var w = Mm(x); var h = Mm(y);
        if (w is double ww && h is double hh)
        {
            var g = new TouchpadGeometry(ww, hh);
            if (g.IsValid) return (g, true);
        }
        // Unknown: keep the logical aspect ratio on a typical width.
        double lw = Math.Max(1, x.LogicalMax - x.LogicalMin), lh = Math.Max(1, y.LogicalMax - y.LogicalMin);
        double width = TouchpadGeometry.Default.WidthMm;
        return (new TouchpadGeometry(width, width * lh / lw), false);
    }

    /// <summary>Finds all Precision Touchpads currently attached.</summary>
    public static List<PrecisionTouchpad> Enumerate(Action<string>? log = null)
    {
        var result = new List<PrecisionTouchpad>();
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return result;
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1)) return result;

        foreach (var dev in list.Take((int)count))
        {
            if (dev.dwType != RIM_TYPEHID) continue;
            try
            {
                var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
                uint infoSize = info.cbSize;
                IntPtr infoPtr = Marshal.AllocHGlobal((int)infoSize);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    if (GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICEINFO, infoPtr, ref infoSize) == unchecked((uint)-1)) continue;
                    info = Marshal.PtrToStructure<RID_DEVICE_INFO>(infoPtr);
                }
                finally { Marshal.FreeHGlobal(infoPtr); }

                if (info.hid.usUsagePage != PageDigitizer || info.hid.usUsage != 0x05) continue;

                string name = DeviceNameOf(dev.hDevice);
                uint ppSize = 0;
                GetRawInputDeviceInfo(dev.hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref ppSize);
                if (ppSize == 0) continue;
                IntPtr pp = Marshal.AllocHGlobal((int)ppSize);
                if (GetRawInputDeviceInfo(dev.hDevice, RIDI_PREPARSEDDATA, pp, ref ppSize) == unchecked((uint)-1))
                {
                    Marshal.FreeHGlobal(pp);
                    continue;
                }
                try
                {
                    result.Add(new PrecisionTouchpad(dev.hDevice, name, info.hid.dwVendorId, info.hid.dwProductId, pp));
                }
                catch (Exception ex)
                {
                    Marshal.FreeHGlobal(pp);
                    log?.Invoke($"Touchpad {name} not usable: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Touchpad enumeration error: {ex.Message}");
            }
        }
        return result;
    }

    private static string DeviceNameOf(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return "?";
        IntPtr buf = Marshal.AllocHGlobal((int)chars * 2 + 2);
        try
        {
            GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buf, ref chars);
            return Marshal.PtrToStringUni(buf) ?? "?";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// Parses one HID report. Returns a complete frame when one is ready (possibly after several
    /// hybrid-mode reports), otherwise null.
    /// </summary>
    public TouchFrame? Parse(IntPtr report, uint length, double time)
    {
        ReportsParsed++;
        uint contactCount = 0;
        bool countValid = _hasContactCount &&
            HidP_GetUsageValue(HIDP_REPORT_TYPE.Input, PageDigitizer, _contactCountLink, UsageContactCount,
                               out contactCount, _preparsed, report, length) == HIDP_STATUS_SUCCESS;

        bool button = _lastButton;
        if (_hasButton)
        {
            uint n = (uint)_usageBuffer.Length;
            if (HidP_GetUsages(HIDP_REPORT_TYPE.Input, PageButton, _buttonLink, _usageBuffer, ref n, _preparsed, report, length) == HIDP_STATUS_SUCCESS)
            {
                button = false;
                for (int i = 0; i < n; i++) if (_usageBuffer[i] == 1) button = true;
            }
        }

        if (!countValid)
        {
            // Not a touch report (e.g. another report ID). Only a button change matters.
            if (button == _lastButton) return null;
            _lastButton = button;
            return new TouchFrame(time, Array.Empty<TouchPoint>(), button);
        }

        int take;
        if (contactCount > 0 || _expected == 0)
        {
            // first report of a frame
            if (_pending.Count > 0) _pending.Clear();          // previous frame was never completed
            _expected = (int)contactCount;
            take = Math.Min(_expected, _fingers.Count);
        }
        else
        {
            take = Math.Min(_expected - _pending.Count, _fingers.Count); // continuation report
        }

        for (int i = 0; i < take; i++)
        {
            var f = _fingers[i];
            if (!TryReadFinger(f, report, length, out var p)) { ParseErrors++; continue; }
            _pending.Add(p);
        }

        if (_pending.Count < _expected) return null;            // wait for the rest of the frame

        var frame = new TouchFrame(time, _pending.ToArray(), button);
        _pending.Clear();
        _expected = 0;
        _lastButton = button;
        return frame;
    }

    private bool TryReadFinger(Finger f, IntPtr report, uint length, out TouchPoint point)
    {
        point = default;
        if (HidP_GetUsageValue(HIDP_REPORT_TYPE.Input, PageGeneric, f.LinkCollection, UsageX, out uint rx, _preparsed, report, length) != HIDP_STATUS_SUCCESS) return false;
        if (HidP_GetUsageValue(HIDP_REPORT_TYPE.Input, PageGeneric, f.LinkCollection, UsageY, out uint ry, _preparsed, report, length) != HIDP_STATUS_SUCCESS) return false;
        uint id = f.LinkCollection;
        if (f.HasContactId)
            HidP_GetUsageValue(HIDP_REPORT_TYPE.Input, PageDigitizer, f.LinkCollection, UsageContactId, out id, _preparsed, report, length);

        bool tip = false, confidence = !f.HasConfidence;
        uint n = (uint)_usageBuffer.Length;
        if (HidP_GetUsages(HIDP_REPORT_TYPE.Input, PageDigitizer, f.LinkCollection, _usageBuffer, ref n, _preparsed, report, length) == HIDP_STATUS_SUCCESS)
        {
            for (int i = 0; i < n; i++)
            {
                if (_usageBuffer[i] == UsageTip) tip = true;
                else if (_usageBuffer[i] == UsageConfidence) confidence = true;
            }
        }

        double x = Normalize((int)rx, f.X) * Geometry.WidthMm;
        double y = Normalize((int)ry, f.Y) * Geometry.HeightMm;
        point = new TouchPoint((int)id, x, y, tip, confidence);
        return true;
    }

    private static double Normalize(int raw, HIDP_VALUE_CAPS c)
    {
        double range = (double)c.LogicalMax - c.LogicalMin;
        if (range <= 0) return 0;
        return Math.Clamp((raw - c.LogicalMin) / range, 0, 1);
    }

    /// <summary>Forget a partially assembled frame (e.g. after a long silence).</summary>
    public void ResetAssembly()
    {
        _pending.Clear();
        _expected = 0;
    }

    public string Describe() =>
        $"{DeviceName} (VID {VendorId:X4} PID {ProductId:X4}), {Geometry.WidthMm:0.0} × {Geometry.HeightMm:0.0} mm " +
        $"({(GeometryFromDevice ? "from device" : "estimated")}), {MaxFingersPerReport} contacts/report, " +
        $"{ReportsParsed} reports, {ParseErrors} errors";

    public void Dispose()
    {
        if (_preparsed != IntPtr.Zero) Marshal.FreeHGlobal(_preparsed);
    }
}
