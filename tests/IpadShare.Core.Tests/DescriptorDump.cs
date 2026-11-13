using IpadShare.Core.Hid;
using Xunit.Abstractions;

namespace IpadShare.Core.Tests;

public class DescriptorDump
{
    private readonly ITestOutputHelper _o;
    public DescriptorDump(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Print_size()
    {
        _o.WriteLine($"Report map: {ReportMap.Descriptor.Length} bytes");
        _o.WriteLine(Convert.ToHexString(ReportMap.Descriptor));
    }
}
