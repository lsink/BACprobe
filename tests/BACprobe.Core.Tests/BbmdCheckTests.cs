using System.Net;
using BACprobe.Core.Bbmd;
using BACprobe.Core.Discovery;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Tests;

public class BbmdCheckTests
{
    private static IPEndPoint Ep(string s) => IPEndPoint.Parse(s);
    private static BdtEntry Two(string ep) => new(Ep(ep), IPAddress.Broadcast);
    private static BdtEntry One(string ep, string mask = "255.255.255.0") => new(Ep(ep), IPAddress.Parse(mask));

    private static readonly IPEndPoint A = Ep("10.1.1.10:47808");
    private static readonly IPEndPoint B = Ep("10.2.2.10:47808");
    private static readonly IPEndPoint C = Ep("10.3.3.10:47808");

    private static TableRead<BdtEntry> Bdt(params BdtEntry[] e) => new(TableReadStatus.Ok, e);
    private static TableRead<FdtEntry> Fdt(params FdtEntry[] e) => new(TableReadStatus.Ok, e);
    private static readonly Dictionary<IPEndPoint, TableRead<BdtEntry>> NoPeers = [];

    private static AdapterInfo Nic(string ip) =>
        new("e", "Ethernet", "Ethernet", IPAddress.Parse(ip), IPAddress.Parse("255.255.255.0"), true, false, false);

    private static IReadOnlyList<NetworkFinding> Check(TableRead<BdtEntry> bdt, TableRead<FdtEntry>? fdt = null,
        Dictionary<IPEndPoint, TableRead<BdtEntry>>? peers = null, AdapterInfo? nic = null) =>
        BbmdCheck.Analyze(A, bdt, fdt ?? Fdt(), peers ?? NoPeers, nic);

    private static void AllExplained(IEnumerable<NetworkFinding> findings) => Assert.All(findings, f =>
    {
        Assert.False(string.IsNullOrWhiteSpace(f.LikelyCause));
        Assert.False(string.IsNullOrWhiteSpace(f.NextStep));
    });

    // --- the wire format ---

    [Fact]
    public void Requests_are_the_four_byte_bvlc_messages()
    {
        Assert.Equal(new byte[] { 0x81, 0x02, 0x00, 0x04 }, BvlcTables.ReadBdtRequest());
        Assert.Equal(new byte[] { 0x81, 0x06, 0x00, 0x04 }, BvlcTables.ReadFdtRequest());
    }

    [Fact]
    public void A_broadcast_table_ack_decodes_by_hand()
    {
        byte[] msg =
        [
            0x81, 0x03, 0x00, 0x18,
            10, 1, 1, 10, 0xBA, 0xC0, 255, 255, 255, 255, // 10.1.1.10:47808, two-hop
            10, 2, 2, 10, 0xBA, 0xC1, 255, 255, 255, 0,   // 10.2.2.10:47809, one-hop /24
        ];
        Assert.Equal(BvlcTables.ReplyKind.Bdt, BvlcTables.Classify(msg));
        var e = BvlcTables.ParseBdt(msg);
        Assert.Equal(2, e.Count);
        Assert.Equal(A, e[0].Bbmd);
        Assert.True(e[0].IsTwoHop);
        Assert.Equal(Ep("10.2.2.10:47809"), e[1].Bbmd);
        Assert.False(e[1].IsTwoHop);
    }

    [Fact]
    public void A_foreign_device_table_round_trips()
    {
        FdtEntry[] table = [new(Ep("192.168.5.20:47808"), 300, 245), new(Ep("192.168.5.21:50000"), 60, 12)];
        var msg = BvlcTables.EncodeFdtAck(table);
        Assert.Equal(BvlcTables.ReplyKind.Fdt, BvlcTables.Classify(msg));
        Assert.Equal(table, BvlcTables.ParseFdt(msg));
    }

    [Fact]
    public void Naks_are_recognised_and_junk_is_ignored()
    {
        Assert.Equal(BvlcTables.ReplyKind.BdtNak, BvlcTables.Classify(BvlcTables.EncodeResult(BvlcTables.ReadBdtNak)));
        Assert.Equal(BvlcTables.ReplyKind.FdtNak, BvlcTables.Classify(BvlcTables.EncodeResult(BvlcTables.ReadFdtNak)));
        Assert.Equal(BvlcTables.ReplyKind.Other, BvlcTables.Classify(BvlcTables.EncodeResult(0x0000))); // a successful registration, not ours
        Assert.Equal(BvlcTables.ReplyKind.Other, BvlcTables.Classify([0x81, 0x03]));
        Assert.Equal(BvlcTables.ReplyKind.Other, BvlcTables.Classify([0x82, 0x03, 0x00, 0x04]));
        Assert.Equal(BvlcTables.ReplyKind.Other, BvlcTables.Classify([0x81, 0x03, 0x00, 0x40])); // claims more bytes than arrived
    }

    [Fact]
    public void A_partial_trailing_entry_is_ignored()
    {
        var msg = BvlcTables.EncodeBdtAck([Two("10.1.1.10:47808")]).Concat(new byte[] { 1, 2, 3 }).ToArray();
        msg[3] = (byte)msg.Length; // the header claims the stray bytes too
        Assert.Single(BvlcTables.ParseBdt(msg));
    }

    // --- judging the tables ---

    [Fact]
    public void Tables_that_agree_look_right()
    {
        var f = Check(Bdt(Two("10.1.1.10:47808"), Two("10.2.2.10:47808")),
            peers: new() { [B] = Bdt(Two("10.2.2.10:47808"), Two("10.1.1.10:47808")) });
        Assert.All(f, x => Assert.Equal(FindingSeverity.Info, x.Severity));
        Assert.Contains(f, x => x.Title.Contains("looks right"));
    }

    [Fact]
    public void Silence_on_both_tables_is_one_problem_with_a_next_step()
    {
        var f = Check(TableRead.Failed<BdtEntry>(TableReadStatus.NoAnswer), TableRead.Failed<FdtEntry>(TableReadStatus.NoAnswer));
        var only = Assert.Single(f);
        Assert.Equal(FindingSeverity.Problem, only.Severity);
        Assert.Contains("did not answer", only.Title);
        AllExplained(f);
    }

    [Fact]
    public void A_refused_broadcast_table_means_not_a_bbmd() =>
        Assert.Contains(Check(TableRead.Failed<BdtEntry>(TableReadStatus.Refused)), x => x.Title.Contains("refused to show its broadcast table"));

    [Fact]
    public void An_empty_table_and_a_table_of_only_itself_are_called_out()
    {
        Assert.Contains(Check(Bdt()), x => x.Severity == FindingSeverity.Warning && x.Title.Contains("empty"));
        var self = Check(Bdt(Two("10.1.1.10:47808")));
        Assert.Contains(self, x => x.Title.Contains("lists only itself"));
        Assert.DoesNotContain(self, x => x.Title.Contains("looks right"));
    }

    [Fact]
    public void Missing_itself_duplicates_and_one_hop_are_warnings()
    {
        var f = Check(Bdt(Two("10.2.2.10:47808"), Two("10.2.2.10:47808"), One("10.3.3.10:47808")));
        Assert.Contains(f, x => x.Title.Contains("not listed in its own broadcast table"));
        Assert.Contains(f, x => x.Title.Contains("listed 2 times"));
        Assert.Contains(f, x => x.Title.Contains("one-hop") && x.Title.Contains("10.3.3.10"));
        AllExplained(f.Where(x => x.Severity != FindingSeverity.Info));
    }

    [Fact]
    public void A_peer_that_does_not_list_this_bbmd_is_a_one_way_problem()
    {
        var f = Check(Bdt(Two("10.1.1.10:47808"), Two("10.2.2.10:47808")),
            peers: new() { [B] = Bdt(Two("10.2.2.10:47808")) });
        var oneWay = Assert.Single(f, x => x.Severity == FindingSeverity.Problem);
        Assert.Contains("One-way", oneWay.Title);
        Assert.Contains("Add 10.1.1.10:47808 to 10.2.2.10:47808", oneWay.NextStep);
    }

    [Fact]
    public void Peers_with_different_lists_are_flagged()
    {
        var f = Check(Bdt(Two("10.1.1.10:47808"), Two("10.2.2.10:47808")),
            peers: new() { [B] = Bdt(Two("10.2.2.10:47808"), Two("10.1.1.10:47808"), Two("10.3.3.10:47808")) });
        var differ = Assert.Single(f, x => x.Title.Contains("differ"));
        Assert.Contains("10.3.3.10:47808", differ.Detail);
    }

    [Fact]
    public void A_silent_or_refusing_peer_is_a_warning()
    {
        var f = Check(Bdt(Two("10.1.1.10:47808"), Two("10.2.2.10:47808"), Two("10.3.3.10:47808")),
            peers: new() { [B] = TableRead.Failed<BdtEntry>(TableReadStatus.NoAnswer), [C] = TableRead.Failed<BdtEntry>(TableReadStatus.Refused) });
        Assert.Contains(f, x => x.Title == "Peer BBMD 10.2.2.10:47808 did not answer");
        Assert.Contains(f, x => x.Title.Contains("10.3.3.10:47808 is listed but is not acting as a BBMD"));
        Assert.DoesNotContain(f, x => x.Severity == FindingSeverity.Problem);
    }

    [Fact]
    public void Two_bbmds_on_this_pcs_subnet_are_flagged()
    {
        var f = Check(Bdt(Two("10.1.1.10:47808"), Two("10.1.1.20:47808")), nic: Nic("10.1.1.50"));
        Assert.Contains(f, x => x.Title.StartsWith("Two BBMDs on 10.1.1.0/24"));
        // The same BBMD on another port is not a second BBMD on the subnet.
        Assert.DoesNotContain(Check(Bdt(Two("10.1.1.10:47808"), Two("10.1.1.10:47809")), nic: Nic("10.1.1.50")),
            x => x.Title.StartsWith("Two BBMDs"));
    }

    [Fact]
    public void Foreign_device_table_notes()
    {
        Assert.Contains(Check(Bdt(Two("10.1.1.10:47808")), TableRead.Failed<FdtEntry>(TableReadStatus.Refused)),
            x => x.Title.Contains("does not accept foreign devices"));
        var mine = Check(Bdt(Two("10.1.1.10:47808")), Fdt(new FdtEntry(Ep("10.9.9.9:51200"), 300, 280)), nic: Nic("10.9.9.9"));
        Assert.Contains(mine, x => x.Title.StartsWith("This PC is registered"));
    }

    [Fact]
    public void Rows_note_what_each_peer_says_back()
    {
        var report = BbmdCheck.Build(A, Bdt(Two("10.1.1.10:47808"), Two("10.2.2.10:47808"), Two("10.3.3.10:47808")), Fdt(),
            new Dictionary<IPEndPoint, TableRead<BdtEntry>> { [B] = Bdt(Two("10.2.2.10:47808"), Two("10.1.1.10:47808")), [C] = Bdt(Two("10.3.3.10:47808")) });
        Assert.Equal(["this BBMD", "lists this BBMD back", "does NOT list this BBMD"], report.BdtRows.Select(r => r.Note));
        Assert.True(report.BdtRows[0].IsThisBbmd);
    }
}
