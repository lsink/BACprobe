using System.IO.BACnet;
using System.Net;
using BACprobe.Core.Bbmd;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Tests;

public class BbmdTargetTests
{
    [Theory]
    [InlineData("10.20.30.40", "10.20.30.40", 47808)]
    [InlineData("10.20.30.40:47809", "10.20.30.40", 47809)]
    [InlineData("  192.168.5.9  ", "192.168.5.9", 47808)]
    public void Valid_addresses_parse(string text, string ip, int port)
    {
        Assert.True(BbmdTarget.TryParse(text, 300, out var t, out _));
        Assert.Equal(IPAddress.Parse(ip), t!.Address);
        Assert.Equal(port, t.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bbmd.example.com")]
    [InlineData("10.20.30")]
    [InlineData("10.20.30.40:0")]
    [InlineData("10.20.30.40:99999")]
    [InlineData("10.20.30.40:abc")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    public void Bad_addresses_are_rejected_with_a_message(string text)
    {
        Assert.False(BbmdTarget.TryParse(text, 300, out var t, out var error));
        Assert.Null(t);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(40000)]
    public void Ttl_out_of_range_is_rejected(int ttl) =>
        Assert.False(BbmdTarget.TryParse("10.1.1.1", ttl, out _, out _));

    [Fact]
    public void Renewal_happens_at_half_the_ttl_but_never_faster_than_5_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(150), new BbmdTarget(IPAddress.Parse("10.1.1.1"), 47808, 300).RenewInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), new BbmdTarget(IPAddress.Parse("10.1.1.1"), 47808, 4).RenewInterval);
    }

    [Fact]
    public void Same_subnet_bbmd_gets_a_warning_and_other_subnet_passes()
    {
        var nic = new AdapterInfo("e", "Ethernet", "Ethernet", IPAddress.Parse("192.168.1.50"), IPAddress.Parse("255.255.255.0"),
            true, false, false);
        Assert.Equal(PreflightSeverity.Warning,
            new BbmdTarget(IPAddress.Parse("192.168.1.2"), 47808, 300).CheckAgainst(nic).Severity);
        Assert.Equal(PreflightSeverity.Pass,
            new BbmdTarget(IPAddress.Parse("10.9.9.9"), 47808, 300).CheckAgainst(nic).Severity);
    }

    [Fact]
    public void Port_is_shown_only_when_not_standard()
    {
        Assert.Equal("10.1.1.1", new BbmdTarget(IPAddress.Parse("10.1.1.1"), 47808, 300).ToString());
        Assert.Equal("10.1.1.1:47809", new BbmdTarget(IPAddress.Parse("10.1.1.1"), 47809, 300).ToString());
    }
}

public class ForeignDeviceRegistrationTests
{
    private static readonly BbmdTarget Target = new(IPAddress.Parse("10.9.9.9"), 47808, 300);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(150);

    private sealed class FakeLink : IBvlcLink
    {
        public event Action<IPEndPoint, BacnetBvlcResults>? ResultReceived;
        public int Sent;
        public bool CanSend = true;
        public BacnetBvlcResults? Reply;
        public IPAddress ReplyFrom = Target.Address;
        public List<(int, int)> WhoIs { get; } = [];

        public bool SendRegister(IPEndPoint bbmd, short ttlSeconds)
        {
            Sent++;
            if (CanSend && Reply is { } r) Task.Run(() => ResultReceived?.Invoke(new IPEndPoint(ReplyFrom, 47808), r));
            return CanSend;
        }

        public void SendRemoteWhoIs(IPEndPoint bbmd, int low, int high) => WhoIs.Add((low, high));
    }

    [Fact]
    public async Task Success_reply_registers()
    {
        var link = new FakeLink { Reply = BacnetBvlcResults.BVLC_RESULT_SUCCESSFUL_COMPLETION };
        using var reg = new ForeignDeviceRegistration(link, Target);
        Assert.Equal(BbmdState.Registered, await reg.RegisterAsync(Quick));
        Assert.True(reg.IsRegistered);
        Assert.Contains("Registered", reg.Message);
        Assert.Equal(1, link.Sent);
    }

    [Fact]
    public async Task Nak_means_refused_with_cause_and_next_step()
    {
        var link = new FakeLink { Reply = BacnetBvlcResults.BVLC_RESULT_REGISTER_FOREIGN_DEVICE_NAK };
        using var reg = new ForeignDeviceRegistration(link, Target);
        Assert.Equal(BbmdState.Refused, await reg.RegisterAsync(Quick));
        Assert.False(reg.IsRegistered);
        Assert.Contains("Likely cause", reg.Message);
        Assert.Contains("Next step", reg.Message);
    }

    [Fact]
    public async Task Silence_is_retried_then_reported_as_no_answer()
    {
        var link = new FakeLink();
        using var reg = new ForeignDeviceRegistration(link, Target);
        Assert.Equal(BbmdState.NoAnswer, await reg.RegisterAsync(Quick, attempts: 2));
        Assert.Equal(2, link.Sent);
        Assert.Contains("did not answer", reg.Message);
        Assert.Contains("Likely cause", reg.Message);
    }

    [Fact]
    public async Task Unsendable_packet_is_reported_without_retrying()
    {
        var link = new FakeLink { CanSend = false };
        using var reg = new ForeignDeviceRegistration(link, Target);
        Assert.Equal(BbmdState.SendFailed, await reg.RegisterAsync(Quick));
        Assert.Equal(1, link.Sent);
        Assert.Contains("Next step", reg.Message);
    }

    [Fact]
    public async Task Replies_from_some_other_host_are_ignored()
    {
        var link = new FakeLink
        {
            Reply = BacnetBvlcResults.BVLC_RESULT_SUCCESSFUL_COMPLETION,
            ReplyFrom = IPAddress.Parse("10.1.2.3"),
        };
        using var reg = new ForeignDeviceRegistration(link, Target);
        Assert.Equal(BbmdState.NoAnswer, await reg.RegisterAsync(Quick, attempts: 1));
    }

    [Fact]
    public async Task Remote_who_is_goes_to_the_bbmd()
    {
        var link = new FakeLink { Reply = BacnetBvlcResults.BVLC_RESULT_SUCCESSFUL_COMPLETION };
        using var reg = new ForeignDeviceRegistration(link, Target);
        await reg.RegisterAsync(Quick);
        reg.RemoteWhoIs(1000, 2000);
        Assert.Equal([(1000, 2000)], link.WhoIs);
    }

    private static async Task WaitFor(ForeignDeviceRegistration reg, BbmdState state)
    {
        for (var i = 0; i < 100 && reg.State != state; i++) await Task.Delay(20);
        Assert.Equal(state, reg.State);
    }

    [Fact]
    public async Task A_failed_renewal_keeps_retrying_and_recovers()
    {
        var link = new FakeLink { Reply = BacnetBvlcResults.BVLC_RESULT_SUCCESSFUL_COMPLETION };
        using var reg = new ForeignDeviceRegistration(link, Target)
        {
            RenewEvery = TimeSpan.FromMilliseconds(50), RetryEvery = TimeSpan.FromMilliseconds(50), RenewReplyTimeout = Quick,
        };
        Assert.Equal(BbmdState.Registered, await reg.RegisterAsync(Quick));

        link.Reply = null; // the BBMD goes quiet (VPN reconnecting)
        await WaitFor(reg, BbmdState.NoAnswer);
        Assert.Contains("keeps retrying", reg.Message);

        link.Reply = BacnetBvlcResults.BVLC_RESULT_SUCCESSFUL_COMPLETION; // and comes back
        await WaitFor(reg, BbmdState.Registered);
        Assert.Contains("restored", reg.Message);
    }

    [Fact]
    public async Task Disposing_stops_the_retries()
    {
        var link = new FakeLink { Reply = BacnetBvlcResults.BVLC_RESULT_SUCCESSFUL_COMPLETION };
        var reg = new ForeignDeviceRegistration(link, Target)
        {
            RenewEvery = TimeSpan.FromMilliseconds(30), RetryEvery = TimeSpan.FromMilliseconds(30), RenewReplyTimeout = TimeSpan.FromMilliseconds(30),
        };
        await reg.RegisterAsync(Quick);
        link.Reply = null;
        await WaitFor(reg, BbmdState.NoAnswer);
        reg.Dispose();
        await Task.Delay(200); // let a retry that was already running finish
        var sent = link.Sent;
        await Task.Delay(200);
        Assert.Equal(sent, link.Sent);
    }
}
