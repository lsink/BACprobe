using BACprobe.Core.Browsing;

namespace BACprobe.Core.Tests;

public class QuickCopyTests
{
    [Fact]
    public void Device_filter_uses_the_ip_without_the_port()
        => Assert.Equal("bacnet && ip.addr == 10.1.2.3", QuickCopy.WiresharkFilterForAddress("10.1.2.3:47808"));

    [Fact]
    public void Routed_device_uses_the_routers_ip()
        => Assert.Equal("bacnet && ip.addr == 10.1.2.3",
            QuickCopy.WiresharkFilterForAddress("network 3, MAC 17 (via 10.1.2.3:47808)"));

    [Fact]
    public void Object_filter_adds_type_and_instance()
        => Assert.Equal("bacnet && ip.addr == 10.1.2.3 && bacapp.objectType == 2 && bacapp.instance_number == 7",
            QuickCopy.WiresharkFilterForObject("10.1.2.3:47808", 2, 7));

    [Fact]
    public void No_ip_means_no_filter()
    {
        Assert.Null(QuickCopy.WiresharkFilterForAddress("MS/TP 12"));
        Assert.Null(QuickCopy.WiresharkFilterForObject("999.1.1.1", 0, 1));
    }
}
