using System.Net;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Tests;

public class PreflightRulesTests
{
    private static AdapterInfo Nic(string ip, string mask = "255.255.255.0", bool up = true, bool virt = false,
        string name = "Ethernet") =>
        new(name, name, name, IPAddress.Parse(ip), IPAddress.Parse(mask), up, virt, false);

    private static readonly PortProbe FreePort = new(true, null, []);

    [Fact]
    public void Healthy_adapter_passes_everything()
    {
        var a = Nic("192.168.1.50");
        var results = PreflightRules.Evaluate(a, [a], FreePort);
        Assert.All(results, r => Assert.Equal(PreflightSeverity.Pass, r.Severity));
        Assert.True(PreflightRules.CanProceed(results));
    }

    [Fact]
    public void Down_adapter_fails_with_cause_and_next_step()
    {
        var r = PreflightRules.CheckAdapterUp(Nic("192.168.1.50", up: false));
        Assert.Equal(PreflightSeverity.Fail, r.Severity);
        Assert.NotNull(r.LikelyCause);
        Assert.NotNull(r.NextStep);
    }

    [Fact]
    public void Link_local_address_fails()
    {
        var r = PreflightRules.CheckAddress(Nic("169.254.3.4", "255.255.0.0"));
        Assert.Equal(PreflightSeverity.Fail, r.Severity);
        Assert.Contains("169.254", r.LikelyCause);
    }

    [Fact]
    public void Virtual_adapter_warns_but_can_proceed()
    {
        var a = Nic("172.20.0.1", virt: true, name: "vEthernet (WSL)");
        var results = PreflightRules.Evaluate(a, [a], FreePort);
        Assert.Contains(results, r => r.Severity == PreflightSeverity.Warning);
        Assert.True(PreflightRules.CanProceed(results));
    }

    [Fact]
    public void Two_adapters_on_one_subnet_warn()
    {
        var eth = Nic("192.168.1.50");
        var wifi = Nic("192.168.1.77", name: "Wi-Fi");
        Assert.Equal(PreflightSeverity.Warning, PreflightRules.CheckDuplicateSubnet(eth, [eth, wifi]).Severity);
    }

    [Fact]
    public void Down_adapter_on_same_subnet_is_ignored()
    {
        var eth = Nic("192.168.1.50");
        var wifi = Nic("192.168.1.77", up: false, name: "Wi-Fi");
        Assert.Equal(PreflightSeverity.Pass, PreflightRules.CheckDuplicateSubnet(eth, [eth, wifi]).Severity);
    }

    [Fact]
    public void Exclusive_port_holder_fails_and_is_named()
    {
        var probe = new PortProbe(false, "Address already in use", [new PortOwner(1234, "yabe", "0.0.0.0")]);
        var r = PreflightRules.CheckPort(probe);
        Assert.Equal(PreflightSeverity.Fail, r.Severity);
        Assert.Contains("yabe", r.Message);
        Assert.Contains("1234", r.Message);
    }

    [Fact]
    public void Shared_port_with_other_process_warns()
    {
        var probe = new PortProbe(true, null, [new PortOwner(99, "other", "0.0.0.0")]);
        Assert.Equal(PreflightSeverity.Warning, PreflightRules.CheckPort(probe).Severity);
    }
}
