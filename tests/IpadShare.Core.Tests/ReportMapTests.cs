using IpadShare.Core.Hid;
using static IpadShare.Core.Tests.HidDescriptorParser;

namespace IpadShare.Core.Tests;

public class ReportMapTests
{
    private static readonly HidDescriptorParser P = Parse(ReportMap.Descriptor);

    [Fact]
    public void Descriptor_fits_in_one_BLE_attribute()
    {
        Assert.True(ReportMap.Descriptor.Length <= 512, $"descriptor is {ReportMap.Descriptor.Length} bytes");
    }

    [Fact]
    public void Has_four_application_collections()
    {
        Assert.Equal(4, P.ApplicationCollections);
    }

    [Theory]
    [InlineData(ReportIds.Keyboard, HidReportType.Input, ReportMap.KeyboardReportLength)]
    [InlineData(ReportIds.Consumer, HidReportType.Input, ReportMap.ConsumerReportLength)]
    [InlineData(ReportIds.Mouse, HidReportType.Input, ReportMap.MouseReportLength)]
    [InlineData(ReportIds.MouseResolution, HidReportType.Feature, ReportMap.MouseResolutionReportLength)]
    [InlineData(ReportIds.Touchpad, HidReportType.Input, ReportMap.TouchpadReportLength)]
    [InlineData(ReportIds.TouchpadCaps, HidReportType.Feature, ReportMap.TouchpadCapsReportLength)]
    public void Report_lengths_match_descriptor(byte id, HidReportType type, int bytes)
    {
        Assert.True(P.ReportBits.TryGetValue((id, type), out int bits), $"report {id} {type} missing");
        Assert.Equal(bytes * 8, bits);
    }

    [Fact]
    public void Every_declared_report_exists_and_nothing_else()
    {
        var declared = ReportMap.Reports.Select(r => (r.Id, r.Type)).ToHashSet();
        Assert.Equal(declared, P.ReportBits.Keys.ToHashSet());
        foreach (var r in ReportMap.Reports)
            Assert.Equal(r.Length * 8, P.ReportBits[(r.Id, r.Type)]);
    }

    private static Field FieldFor(byte id, HidReportType type, ushort page, ushort usage, int occurrence = 0)
    {
        int seen = 0;
        foreach (var f in P.Fields.Where(f => f.ReportId == id && f.Type == type))
        {
            if (f.IsConstant) continue;
            for (int i = 0; i < f.Count; i++)
            {
                if (f.UsageAt(i) == FullUsage(page, usage))
                {
                    if (seen++ == occurrence)
                        return f with { BitOffset = f.IsVariable ? f.BitOffset + i * f.BitSize : f.BitOffset, Count = f.IsVariable ? 1 : f.Count };
                }
            }
        }
        throw new Xunit.Sdk.XunitException($"usage {page:X}:{usage:X} #{occurrence} not found in report {id}");
    }

    [Fact]
    public void Keyboard_encoder_matches_descriptor()
    {
        var rep = new KeyboardReport(Mods.LGui | Mods.LShift | Mods.Globe, new byte[] { Keys.Z, Keys.A }).ToBytes();
        Assert.Equal(1, ReadBits(rep, FieldFor(1, HidReportType.Input, HidUsages.PageKeyboard, Keys.LeftGui).BitOffset, 1, false));
        Assert.Equal(1, ReadBits(rep, FieldFor(1, HidReportType.Input, HidUsages.PageKeyboard, Keys.LeftShift).BitOffset, 1, false));
        Assert.Equal(0, ReadBits(rep, FieldFor(1, HidReportType.Input, HidUsages.PageKeyboard, Keys.LeftCtrl).BitOffset, 1, false));
        var globe = FieldFor(1, HidReportType.Input, HidUsages.PageAppleVendorTopCase, HidUsages.AppleTopCaseKeyboardFn);
        Assert.Equal(8, globe.BitOffset);
        Assert.Equal(1, ReadBits(rep, globe.BitOffset, 1, false));
        var keyArray = P.Fields.Single(f => f.ReportId == 1 && f.Type == HidReportType.Input && !f.IsVariable);
        Assert.Equal(16, keyArray.BitOffset);
        Assert.Equal(6, keyArray.Count);
        Assert.Equal(255, keyArray.LogicalMax);
        Assert.Equal(Keys.Z, ReadBits(rep, keyArray.BitOffset, 8, false));
        Assert.Equal(Keys.A, ReadBits(rep, keyArray.BitOffset + 8, 8, false));
    }

    [Fact]
    public void Mouse_encoder_matches_descriptor()
    {
        var rep = new MouseReport(MouseButtons.Right, -1234, 32767, -5, 77).ToBytes();
        Assert.Equal(1, ReadBits(rep, FieldFor(3, HidReportType.Input, HidUsages.PageButton, 2).BitOffset, 1, false));
        Assert.Equal(0, ReadBits(rep, FieldFor(3, HidReportType.Input, HidUsages.PageButton, 1).BitOffset, 1, false));
        var x = FieldFor(3, HidReportType.Input, HidUsages.PageGenericDesktop, HidUsages.GdX);
        var y = FieldFor(3, HidReportType.Input, HidUsages.PageGenericDesktop, HidUsages.GdY);
        var w = FieldFor(3, HidReportType.Input, HidUsages.PageGenericDesktop, HidUsages.GdWheel);
        var pan = FieldFor(3, HidReportType.Input, HidUsages.PageConsumer, HidUsages.ConsumerAcPan);
        Assert.True(x.IsRelative && y.IsRelative && w.IsRelative && pan.IsRelative);
        Assert.Equal(-1234, ReadBits(rep, x.BitOffset, x.BitSize, true));
        Assert.Equal(32767, ReadBits(rep, y.BitOffset, y.BitSize, true));
        Assert.Equal(-5, ReadBits(rep, w.BitOffset, w.BitSize, true));
        Assert.Equal(77, ReadBits(rep, pan.BitOffset, pan.BitSize, true));
        Assert.Equal(-32767, x.LogicalMin);
    }

    [Fact]
    public void Wheel_resolution_feature_is_a_resolution_multiplier()
    {
        var f = FieldFor(4, HidReportType.Feature, HidUsages.PageGenericDesktop, HidUsages.GdResolutionMultiplier);
        Assert.Equal(0, f.LogicalMin);
        Assert.Equal(1, f.LogicalMax);
    }

    [Fact]
    public void Consumer_encoder_matches_descriptor()
    {
        var rep = ConsumerReport.ToBytes(ConsumerUsages.VolumeUp);
        var f = P.Fields.Single(f => f.ReportId == 2);
        Assert.Equal(16, f.BitSize);
        Assert.Equal(ConsumerUsages.VolumeUp, ReadBits(rep, f.BitOffset, 16, false));
    }

    [Fact]
    public void Touchpad_encoder_matches_descriptor_for_every_contact()
    {
        var contacts = new[]
        {
            new TouchContact(3, true, true, 100, 2900),
            new TouchContact(7, true, false, 1500, 1500),
            new TouchContact(63, false, true, 3000, 0),
            new TouchContact(1, true, true, 1, 2),
            new TouchContact(2, false, false, 2999, 2998),
        };
        var rep = new TouchpadReport(contacts, 0xBEEF, true).ToBytes();

        for (int i = 0; i < 5; i++)
        {
            var tip = FieldFor(5, HidReportType.Input, HidUsages.PageDigitizer, HidUsages.DigTipSwitch, i);
            var conf = FieldFor(5, HidReportType.Input, HidUsages.PageDigitizer, HidUsages.DigConfidence, i);
            var id = FieldFor(5, HidReportType.Input, HidUsages.PageDigitizer, HidUsages.DigContactIdentifier, i);
            var x = FieldFor(5, HidReportType.Input, HidUsages.PageGenericDesktop, HidUsages.GdX, i);
            var y = FieldFor(5, HidReportType.Input, HidUsages.PageGenericDesktop, HidUsages.GdY, i);
            Assert.Equal(contacts[i].Tip ? 1 : 0, ReadBits(rep, tip.BitOffset, 1, false));
            Assert.Equal(contacts[i].Confidence ? 1 : 0, ReadBits(rep, conf.BitOffset, 1, false));
            Assert.Equal(contacts[i].Id, ReadBits(rep, id.BitOffset, id.BitSize, false));
            Assert.Equal(contacts[i].X, ReadBits(rep, x.BitOffset, x.BitSize, false));
            Assert.Equal(contacts[i].Y, ReadBits(rep, y.BitOffset, y.BitSize, false));
            Assert.Equal(ReportMap.TouchLogicalMax, x.LogicalMax);
            Assert.Equal(HidUnits.Centimeter, x.Unit);
            Assert.Equal(-2, x.UnitExponent);
            Assert.Equal(0u, tip.Unit); // push/pop keeps units off the switches
            Assert.Equal(0u, id.Unit);
        }
        var scan = FieldFor(5, HidReportType.Input, HidUsages.PageDigitizer, HidUsages.DigScanTime);
        var count = FieldFor(5, HidReportType.Input, HidUsages.PageDigitizer, HidUsages.DigContactCount);
        var button = FieldFor(5, HidReportType.Input, HidUsages.PageButton, 1);
        Assert.Equal(0xBEEF, ReadBits(rep, scan.BitOffset, 16, false));
        Assert.Equal(HidUnits.Second, scan.Unit);
        Assert.Equal(-4, scan.UnitExponent);
        Assert.Equal(5, ReadBits(rep, count.BitOffset, count.BitSize, false));
        Assert.Equal(0u, count.Unit);
        Assert.Equal(1, ReadBits(rep, button.BitOffset, 1, false));
    }

    [Fact]
    public void Touchpad_caps_feature_reports_five_contact_clickpad()
    {
        var max = FieldFor(6, HidReportType.Feature, HidUsages.PageDigitizer, HidUsages.DigContactCountMaximum);
        var pad = FieldFor(6, HidReportType.Feature, HidUsages.PageDigitizer, HidUsages.DigPadType);
        var v = new[] { ReportMap.TouchpadCapsValue };
        Assert.Equal(ReportMap.TouchContacts, ReadBits(v, max.BitOffset, max.BitSize, false));
        Assert.Equal(0, ReadBits(v, pad.BitOffset, pad.BitSize, false));
    }

    [Fact]
    public void Keyboard_report_drops_extra_keys_and_zeros()
    {
        var r = new KeyboardReport(Mods.None, new byte[] { 0, 4, 5, 0, 6, 7, 8, 9, 10, 11 });
        Assert.Equal(6, r.KeyCount);
        Assert.True(r.Contains(4));
        Assert.False(r.Contains(11));
    }
}
