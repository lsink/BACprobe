using BACprobe.Core.Discovery;
using BACprobe.Core.Mstp;

namespace BACprobe.Core.Tests;

public class MstpJoinPlannerTests
{
    private static MstpNodeRow Node(byte mac, string role = "master") => new(mac, role, 10, 5, 0, 0, 0, "-");

    /// <summary>A survey of a healthy trunk: masters 1, 2, 3 passing the token, MAC 4 never polled-yet, a slave at 130.</summary>
    private static MstpSnapshot Survey(int frames = 200, int errors = 0, int highestPolled = 4, params MstpNodeRow[] nodes) =>
        new(frames, errors, 10, 0.2, TimeSpan.FromMilliseconds(30), true, 38400,
            nodes.Length > 0 ? nodes : [Node(1), Node(2), Node(3)], [], 5000, highestPolled);

    [Fact]
    public void A_healthy_trunk_lets_us_join_at_the_lowest_free_address()
    {
        var plan = MstpJoinPlanner.Plan(Survey(), 38400, requestedMac: null, latencyTooHigh: false);
        Assert.True(plan.CanJoin);
        Assert.Equal(4, plan.Mac);                        // 1, 2, 3 are taken; 4 is the lowest free one the masters look for
        Assert.Equal(127, plan.MaxMaster);                // so no existing master is left out of our ring
        Assert.Contains(plan.Notes, n => n.Severity == FindingSeverity.Info && n.Title.Contains("MAC 4"));
    }

    [Fact]
    public void Never_picks_an_address_that_was_heard_even_as_a_slave()
    {
        var plan = MstpJoinPlanner.Plan(Survey(nodes: [Node(1), Node(2), Node(3, "slave?"), Node(5)], highestPolled: 6), 38400, null, false);
        Assert.True(plan.CanJoin);
        Assert.Equal(4, plan.Mac);
        var full = MstpJoinPlanner.Plan(Survey(nodes: [Node(1), Node(2), Node(3), Node(4)], highestPolled: 4), 38400, null, false);
        Assert.False(full.CanJoin);
        Assert.Contains(full.Refusals, n => n.Title.Contains("No free master address"));
    }

    [Fact]
    public void Too_little_traffic_means_do_not_transmit()
    {
        var plan = MstpJoinPlanner.Plan(Survey(frames: 5), 38400, null, false);
        Assert.False(plan.CanJoin);
        var r = Assert.Single(plan.Refusals);
        Assert.Contains("Not enough", r.Title);
        Assert.Contains("passive monitor", r.NextStep);
    }

    [Fact]
    public void Silence_is_never_a_green_light()
        => Assert.False(MstpJoinPlanner.Plan(Survey(frames: 0, errors: 0, nodes: []), 38400, null, false).CanJoin);

    [Fact]
    public void Frames_with_no_master_in_them_are_refused()
    {
        var plan = MstpJoinPlanner.Plan(Survey(nodes: [Node(130, "slave?")]), 38400, null, false);
        Assert.False(plan.CanJoin);
        Assert.Contains(plan.Refusals, n => n.Title.Contains("No master"));
    }

    [Theory]
    [InlineData(200, 5, true)]    // under 1 percent: fine
    [InlineData(200, 4, true)]
    [InlineData(100, 10, false)]  // about 9 percent: refuse
    public void A_noisy_trunk_is_refused(int frames, int errors, bool canJoin)
        => Assert.Equal(canJoin, MstpJoinPlanner.Plan(Survey(frames, errors), 38400, null, false).CanJoin);

    [Fact]
    public void A_little_noise_is_a_warning_not_a_refusal()
    {
        var plan = MstpJoinPlanner.Plan(Survey(frames: 190, errors: 4), 38400, null, false); // 2 percent
        Assert.True(plan.CanJoin);
        Assert.Contains(plan.Notes, n => n.Severity == FindingSeverity.Warning && n.Title.Contains("damaged"));
    }

    [Fact]
    public void A_slow_usb_adapter_is_refused_with_the_fix()
    {
        var plan = MstpJoinPlanner.Plan(Survey(), 38400, null, latencyTooHigh: true);
        Assert.False(plan.CanJoin);
        var r = Assert.Single(plan.Refusals);
        Assert.Contains("latency", r.Title);
        Assert.Contains("1 ms", r.NextStep);
    }

    [Fact]
    public void A_slow_baud_rate_is_only_a_warning()
    {
        var plan = MstpJoinPlanner.Plan(Survey(), 9600, null, false);
        Assert.True(plan.CanJoin);
        Assert.Contains(plan.Notes, n => n.Severity == FindingSeverity.Warning && n.Title.Contains("9600"));
    }

    [Fact]
    public void A_requested_address_in_use_is_refused()
    {
        var plan = MstpJoinPlanner.Plan(Survey(), 38400, requestedMac: 2, latencyTooHigh: false);
        Assert.False(plan.CanJoin);
        Assert.Contains(plan.Refusals, n => n.Title.Contains("MAC 2 is already in use"));
    }

    [Fact]
    public void A_requested_free_address_is_used()
    {
        var plan = MstpJoinPlanner.Plan(Survey(), 38400, requestedMac: 4, latencyTooHigh: false);
        Assert.True(plan.CanJoin);
        Assert.Equal(4, plan.Mac);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(200)]
    [InlineData(255)]
    public void A_slave_or_broadcast_address_cannot_be_a_master(byte mac)
    {
        var plan = MstpJoinPlanner.Plan(Survey(), 38400, mac, false);
        Assert.False(plan.CanJoin);
        Assert.Contains(plan.Refusals, n => n.Title.Contains("not a master address"));
    }

    [Fact]
    public void An_address_above_what_the_masters_poll_is_allowed_but_warned_about()
    {
        var plan = MstpJoinPlanner.Plan(Survey(highestPolled: 4), 38400, requestedMac: 20, latencyTooHigh: false);
        Assert.True(plan.CanJoin);
        Assert.Contains(plan.Notes, n => n.Severity == FindingSeverity.Warning && n.Title.Contains("MAC 20 is above"));
    }

    [Fact]
    public void Every_refusal_names_a_likely_cause_and_a_next_step()
    {
        foreach (var plan in new[]
                 {
                     MstpJoinPlanner.Plan(Survey(frames: 3), 38400, null, false),
                     MstpJoinPlanner.Plan(Survey(100, 20), 38400, null, false),
                     MstpJoinPlanner.Plan(Survey(), 38400, null, true),
                     MstpJoinPlanner.Plan(Survey(), 38400, 1, false),
                 })
            Assert.All(plan.Refusals, r => { Assert.NotEmpty(r.LikelyCause); Assert.NotEmpty(r.NextStep); });
    }

    [Fact]
    public void The_joined_message_says_what_it_will_do_and_not_to_leave_it_running()
    {
        var info = MstpJoinPlanner.Plan(Survey(), 38400, null, false).Notes.Single(n => n.Severity == FindingSeverity.Info);
        Assert.Contains("answer Poll For Master", info.Detail);
        Assert.Contains("unattended", info.NextStep);
    }
}
