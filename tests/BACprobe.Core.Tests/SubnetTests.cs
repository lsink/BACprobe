using System.Net;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Tests;

public class SubnetTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public void Broadcast_and_network_for_slash24()
    {
        Assert.Equal(Ip("192.168.1.255"), Subnet.BroadcastAddress(Ip("192.168.1.50"), Ip("255.255.255.0")));
        Assert.Equal(Ip("192.168.1.0"), Subnet.NetworkAddress(Ip("192.168.1.50"), Ip("255.255.255.0")));
    }

    [Fact]
    public void Broadcast_for_slash22()
    {
        Assert.Equal(Ip("10.4.7.255"), Subnet.BroadcastAddress(Ip("10.4.5.9"), Ip("255.255.252.0")));
        Assert.Equal("10.4.4.0/22", Subnet.Cidr(Ip("10.4.5.9"), Ip("255.255.252.0")));
    }

    [Fact]
    public void Non_contiguous_mask_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Subnet.PrefixLength(Ip("255.0.255.0")));

    [Theory]
    [InlineData("169.254.10.1", true)]
    [InlineData("169.253.10.1", false)]
    [InlineData("192.168.1.1", false)]
    public void Link_local_detection(string ip, bool expected) =>
        Assert.Equal(expected, Subnet.IsLinkLocal(Ip(ip)));

    [Fact]
    public void Same_subnet_detects_overlap_with_different_masks()
    {
        Assert.True(Subnet.SameSubnet(Ip("10.0.1.5"), Ip("255.255.255.0"), Ip("10.0.1.9"), Ip("255.255.255.0")));
        Assert.True(Subnet.SameSubnet(Ip("10.0.1.5"), Ip("255.255.255.0"), Ip("10.0.2.9"), Ip("255.255.0.0")));
        Assert.False(Subnet.SameSubnet(Ip("10.0.1.5"), Ip("255.255.255.0"), Ip("10.0.2.9"), Ip("255.255.255.0")));
    }
}
