namespace IpadShare.Core.Hid;

/// <summary>Report IDs used by the composite device. Shared by every transport and the firmware.</summary>
public static class ReportIds
{
    public const byte Keyboard = 1;
    public const byte Consumer = 2;
    public const byte Mouse = 3;
    public const byte MouseResolution = 4;   // feature
    public const byte Touchpad = 5;
    public const byte TouchpadCaps = 6;      // feature
}

public enum HidReportType : byte
{
    Input = 1,
    Output = 2,
    Feature = 3,
}

/// <summary>A report exposed by the device (one GATT Report characteristic each).</summary>
public readonly record struct ReportInfo(byte Id, HidReportType Type, int Length, string Name);

/// <summary>
/// The composite HID device the iPad sees: keyboard (+ Apple Globe key), consumer
/// control, high-resolution mouse and a 5-contact multi-touch trackpad.
/// The descriptor is intentionally fixed (not derived from the local touchpad) because
/// iOS caches the report map of bonded devices.
/// </summary>
public static class ReportMap
{
    public const int TouchContacts = 5;

    /// <summary>Virtual trackpad: logical 0..3000 on both axes over 150 mm (0.05 mm/unit).</summary>
    public const int TouchLogicalMax = 3000;
    public const double TouchPhysicalSizeMm = 150.0;
    public const double TouchUnitsPerMm = TouchLogicalMax / TouchPhysicalSizeMm; // 20

    /// <summary>Wheel/pan resolution multiplier advertised when the host enables hi-res scrolling.</summary>
    public const int HiResScrollMultiplier = 120;

    public const int KeyboardReportLength = 8;
    public const int ConsumerReportLength = 2;
    public const int MouseReportLength = 9;
    public const int MouseResolutionReportLength = 1;
    public const int TouchpadReportLength = TouchContacts * 5 + 2 + 1 + 1; // 29
    public const int TouchpadCapsReportLength = 1;

    public static readonly IReadOnlyList<ReportInfo> Reports = new[]
    {
        new ReportInfo(ReportIds.Keyboard, HidReportType.Input, KeyboardReportLength, "Keyboard"),
        new ReportInfo(ReportIds.Consumer, HidReportType.Input, ConsumerReportLength, "Consumer"),
        new ReportInfo(ReportIds.Mouse, HidReportType.Input, MouseReportLength, "Mouse"),
        new ReportInfo(ReportIds.MouseResolution, HidReportType.Feature, MouseResolutionReportLength, "Wheel resolution"),
        new ReportInfo(ReportIds.Touchpad, HidReportType.Input, TouchpadReportLength, "Trackpad"),
        new ReportInfo(ReportIds.TouchpadCaps, HidReportType.Feature, TouchpadCapsReportLength, "Trackpad capabilities"),
    };

    private static readonly Lazy<byte[]> s_descriptor = new(Build);

    /// <summary>The HID report descriptor (BLE "Report Map" characteristic value).</summary>
    public static ReadOnlySpan<byte> Descriptor => s_descriptor.Value;
    public static byte[] DescriptorCopy() => (byte[])s_descriptor.Value.Clone();

    /// <summary>HID Information: bcdHID 1.11, country code 0, flags RemoteWake|NormallyConnectable.</summary>
    public static readonly byte[] HidInformation = { 0x11, 0x01, 0x00, 0x03 };

    /// <summary>Default value of the trackpad capability feature report: 5 contacts, clickpad.</summary>
    public static byte TouchpadCapsValue => (byte)(TouchContacts | (0 << 4));

    private static byte[] Build()
    {
        var d = new HidDescriptorBuilder();

        // ---------------- Keyboard (report 1) ----------------
        d.UsagePage(HidUsages.PageGenericDesktop).Usage(HidUsages.GdKeyboard).Collection(HidCollection.Application)
         .ReportId(ReportIds.Keyboard)
         // Modifier bits (E0..E7)
         .UsagePage(HidUsages.PageKeyboard).UsageMinimum(0xE0).UsageMaximum(0xE7)
         .LogicalMaximum(1).ReportSize(1).ReportCount(8).Input(HidMain.DataVarAbs)   // LogicalMin 0 = initial state
         // Apple Globe / fn key (AppleVendor Top Case page 0xFF, usage 0x03), then 7 bits padding
         .UsagePage(HidUsages.PageAppleVendorTopCase).Usage(HidUsages.AppleTopCaseKeyboardFn)
         .ReportCount(1).Input(HidMain.DataVarAbs)
         .ReportCount(7).Input(HidMain.ConstVarAbs)
         // 6-key rollover array
         .UsagePage(HidUsages.PageKeyboard).UsageMinimum(0x00).UsageMaximum(0xFF)
         .LogicalMaximum(0xFF).ReportSize(8).ReportCount(6).Input(HidMain.DataArrAbs)
         .EndCollection();

        // ---------------- Consumer control (report 2) ----------------
        d.UsagePage(HidUsages.PageConsumer).Usage(HidUsages.ConsumerControl).Collection(HidCollection.Application)
         .ReportId(ReportIds.Consumer)
         .LogicalMaximum(0x3FF).UsageMinimum(0).UsageMaximum(0x3FF)                  // LogicalMin still 0
         .ReportSize(16).ReportCount(1).Input(HidMain.DataArrAbs)
         .EndCollection();

        // ---------------- Mouse (report 3, feature 4) ----------------
        d.UsagePage(HidUsages.PageGenericDesktop).Usage(HidUsages.GdMouse).Collection(HidCollection.Application)
         .ReportId(ReportIds.Mouse)
         .Usage(HidUsages.GdPointer).Collection(HidCollection.Physical)
           .UsagePage(HidUsages.PageButton).UsageMinimum(1).UsageMaximum(5)
           .LogicalMaximum(1).ReportSize(1).ReportCount(5).Input(HidMain.DataVarAbs)
           .ReportCount(3).Input(HidMain.ConstVarAbs)
           .UsagePage(HidUsages.PageGenericDesktop)
           .LogicalMinimum(-32767).LogicalMaximum(32767).ReportSize(16).ReportCount(2)
           .Usage(HidUsages.GdX).Usage(HidUsages.GdY).Input(HidMain.DataVarRel)
           // Wheel + AC Pan share one resolution multiplier inside one logical collection.
           .Collection(HidCollection.Logical)
             .ReportId(ReportIds.MouseResolution)
             .Usage(HidUsages.GdResolutionMultiplier)
             .LogicalMinimum(0).LogicalMaximum(1).PhysicalMinimum(1).PhysicalMaximum(HiResScrollMultiplier)
             .ReportSize(8).ReportCount(1).Feature(HidMain.DataVarAbs)
             .ReportId(ReportIds.Mouse)
             .PhysicalMinimum(0).PhysicalMaximum(0)
             .LogicalMinimum(-32767).LogicalMaximum(32767).ReportSize(16)
             .Usage(HidUsages.GdWheel).Input(HidMain.DataVarRel)
             .UsagePage(HidUsages.PageConsumer).Usage(HidUsages.ConsumerAcPan).Input(HidMain.DataVarRel)
           .EndCollection()
         .EndCollection()
         .EndCollection();

        // ---------------- Precision trackpad (report 5, feature 6) ----------------
        d.UsagePage(HidUsages.PageDigitizer).Usage(HidUsages.DigTouchPad).Collection(HidCollection.Application)
         .ReportId(ReportIds.Touchpad)
         .LogicalMinimum(0);                                       // PhysicalMin already 0 (mouse)
        for (int i = 0; i < TouchContacts; i++)
        {
            d.Usage(HidUsages.DigFinger).Collection(HidCollection.Logical)
             // byte 0: contact id (6 bits), confidence, tip
             .LogicalMaximum(63).ReportSize(6).ReportCount(1)
             .Usage(HidUsages.DigContactIdentifier).Input(HidMain.DataVarAbs)
             .LogicalMaximum(1).ReportSize(1).ReportCount(2)
             .Usage(HidUsages.DigConfidence).Usage(HidUsages.DigTipSwitch).Input(HidMain.DataVarAbs)
             .Push()
               .UsagePage(HidUsages.PageGenericDesktop)
               .LogicalMaximum(TouchLogicalMax).ReportSize(16)         // ReportCount 2 inherited
               .UnitExponent(-2).Unit(HidUnits.Centimeter)              // units of 0.1 mm
               .PhysicalMaximum((int)(TouchPhysicalSizeMm * 10))
               .Usage(HidUsages.GdX).Usage(HidUsages.GdY).Input(HidMain.DataVarAbs)
             .Pop()
             .EndCollection();
        }
        d.Push()
           .UnitExponent(-4).Unit(HidUnits.Second)                    // 100 µs units
           .LogicalMaximum(0xFFFF).ReportSize(16).ReportCount(1)     // physical = logical
           .Usage(HidUsages.DigScanTime).Input(HidMain.DataVarAbs)
         .Pop()
         .LogicalMaximum(TouchContacts).ReportSize(8).ReportCount(1)
         .Usage(HidUsages.DigContactCount).Input(HidMain.DataVarAbs)
         .UsagePage(HidUsages.PageButton).Usage(1)
         .LogicalMaximum(1).ReportSize(1).ReportCount(1).Input(HidMain.DataVarAbs)
         .ReportCount(7).Input(HidMain.ConstVarAbs)
         // Capabilities feature: contact count maximum (4 bits) + pad type (4 bits, 0 = clickpad)
         .ReportId(ReportIds.TouchpadCaps)
         .UsagePage(HidUsages.PageDigitizer)
         .LogicalMaximum(15).ReportSize(4).ReportCount(1)
         .Usage(HidUsages.DigContactCountMaximum).Feature(HidMain.DataVarAbs)
         .Usage(HidUsages.DigPadType).Feature(HidMain.DataVarAbs)
         .EndCollection();

        return d.ToArray();
    }
}
