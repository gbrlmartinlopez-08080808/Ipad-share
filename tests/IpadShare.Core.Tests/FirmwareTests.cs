using System.Globalization;
using System.Text.RegularExpressions;
using IpadShare.Core.Hid;
using IpadShare.Core.Transport;

namespace IpadShare.Core.Tests;

/// <summary>The ESP32 bridge firmware must expose exactly the same report map as the Windows app.</summary>
public class FirmwareTests
{
    private static string Header()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IpadShare.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "firmware", "esp32-bridge", "src", "report_map.h"));
    }

    [Fact]
    public void Firmware_report_map_matches_the_csharp_descriptor()
    {
        var h = Header();
        var block = Regex.Match(h, @"REPORT_MAP\[\]\s*=\s*\{(.*?)\};", RegexOptions.Singleline).Groups[1].Value;
        var bytes = Regex.Matches(block, @"0x([0-9A-Fa-f]{2})").Select(m => byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber)).ToArray();
        Assert.Equal(ReportMap.Descriptor.ToArray(), bytes);

        var crc = Regex.Match(h, @"REPORT_MAP_CRC16\s*=\s*0x([0-9A-Fa-f]{4})").Groups[1].Value;
        Assert.Equal(BridgeProtocol.Crc16(ReportMap.Descriptor), ushort.Parse(crc, NumberStyles.HexNumber));
    }

    [Fact]
    public void Csharp_and_firmware_encode_identical_frames()
    {
        // Produced by firmware/esp32-bridge/src/protocol.h compiled with gcc (see docs/ARCHITECTURE.md §2.6).
        var frame = BridgeProtocol.Encode(BridgeMessage.InputReport, new byte[] { 3, 0, 0, 5, 0 });
        Assert.Equal("03020301020502B500", Convert.ToHexString(frame));
    }
}
