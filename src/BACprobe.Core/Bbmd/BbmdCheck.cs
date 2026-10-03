using System.Net;
using BACprobe.Core.Discovery;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Bbmd;

/// <summary>One line of the broadcast table as shown: the entry, plus what its own table says back (for peers).</summary>
public sealed record BdtRow(BdtEntry Entry, bool IsThisBbmd, string Note)
{
    public string Address => Entry.Bbmd.ToString();
    public string Mask => Entry.Mask.ToString();
    public string Distribution => Entry.Distribution;
}

/// <summary>One line of the foreign device table as shown.</summary>
public sealed record FdtRow(FdtEntry Entry, bool IsThisPc)
{
    public string Device => Entry.Device.ToString();
    public string Ttl => $"{Entry.TtlSeconds} s";
    public string Remaining => $"{Entry.RemainingSeconds} s";
    public string Note => IsThisPc ? "this PC" : "";
}

/// <summary>Everything one BBMD check found: both tables, each peer's table, and what looks wrong.</summary>
public sealed record BbmdReport(
    IPEndPoint Target,
    TableRead<BdtEntry> Bdt,
    TableRead<FdtEntry> Fdt,
    IReadOnlyDictionary<IPEndPoint, TableRead<BdtEntry>> Peers,
    IReadOnlyList<BdtRow> BdtRows,
    IReadOnlyList<FdtRow> FdtRows,
    IReadOnlyList<NetworkFinding> Findings);

/// <summary>
/// Judges a BBMD's tables. Pure: the reads are done by <see cref="BbmdChecker"/>. Every finding says what is wrong,
/// the likely cause, and the next step.
/// </summary>
public static class BbmdCheck
{
    public static BbmdReport Build(IPEndPoint target, TableRead<BdtEntry> bdt, TableRead<FdtEntry> fdt,
        IReadOnlyDictionary<IPEndPoint, TableRead<BdtEntry>> peers, AdapterInfo? adapter = null)
    {
        var rows = bdt.Entries.Select(e => new BdtRow(e, Same(e.Bbmd, target), Note(e, target, peers))).ToList();
        var fdtRows = fdt.Entries.Select(e => new FdtRow(e, adapter is not null && e.Device.Address.Equals(adapter.Address))).ToList();
        return new BbmdReport(target, bdt, fdt, peers, rows, fdtRows, Analyze(target, bdt, fdt, peers, adapter));
    }

    private static bool Same(IPEndPoint a, IPEndPoint b) => a.Address.Equals(b.Address) && a.Port == b.Port;

    private static bool Lists(IReadOnlyList<BdtEntry> table, IPEndPoint bbmd) => table.Any(e => Same(e.Bbmd, bbmd));

    private static bool OnlySelf(IReadOnlyList<BdtEntry> table, IPEndPoint bbmd) => table.Count > 0 && table.All(e => Same(e.Bbmd, bbmd));

    private static string Note(BdtEntry e, IPEndPoint target, IReadOnlyDictionary<IPEndPoint, TableRead<BdtEntry>> peers)
    {
        if (Same(e.Bbmd, target)) return "this BBMD";
        var peer = peers.FirstOrDefault(p => Same(p.Key, e.Bbmd)).Value;
        return peer?.Status switch
        {
            null => "",
            TableReadStatus.Ok => Lists(peer.Entries, target) ? "lists this BBMD back" : "does NOT list this BBMD",
            TableReadStatus.Refused => "refuses to show its table",
            _ => "did not answer",
        };
    }

    public static IReadOnlyList<NetworkFinding> Analyze(IPEndPoint target, TableRead<BdtEntry> bdt, TableRead<FdtEntry> fdt,
        IReadOnlyDictionary<IPEndPoint, TableRead<BdtEntry>> peers, AdapterInfo? adapter = null)
    {
        var f = new List<NetworkFinding>();
        var t = target.ToString();

        if (!bdt.Ok && !fdt.Ok && bdt.Status is TableReadStatus.NoAnswer or TableReadStatus.SendFailed
                               && fdt.Status is TableReadStatus.NoAnswer or TableReadStatus.SendFailed)
        {
            f.Add(new(FindingSeverity.Problem, $"BBMD {t} did not answer",
                bdt.Status == TableReadStatus.SendFailed ? $"The request could not be sent ({bdt.Error})." : "Neither table request got a reply.",
                $"Wrong IP or port, a firewall or VPN blocking UDP {target.Port} between this PC and it, or that address is not a BBMD.",
                "Confirm the BBMD's address with the site contact, check this PC can reach it (ping), and that the port is right (normally 47808)."));
            return f;
        }

        // --- the broadcast table ---
        switch (bdt.Status)
        {
            case TableReadStatus.Refused:
                f.Add(new(FindingSeverity.Warning, $"{t} refused to show its broadcast table",
                    "It answered, but with a NAK to Read-Broadcast-Distribution-Table.",
                    "The device is not set up as a BBMD (the function is off, or this is an ordinary controller).",
                    "Check that this is the right device, and that its BBMD function is switched on."));
                break;
            case TableReadStatus.NoAnswer or TableReadStatus.SendFailed:
                f.Add(new(FindingSeverity.Warning, $"{t} did not answer for its broadcast table",
                    "It answered for its foreign device table, but not for its broadcast table.",
                    "Some BBMDs only answer table reads from certain addresses, or the reply was lost.",
                    "Run the check again; if it repeats, read the table in the BBMD's own tool."));
                break;
            case TableReadStatus.Ok when bdt.Entries.Count == 0:
                f.Add(new(FindingSeverity.Warning, $"{t} has an empty broadcast table",
                    "No peer BBMDs are listed, not even itself.",
                    "BBMD is switched on but no peers were entered, so it only serves foreign devices: broadcasts are not exchanged with other subnets.",
                    "Enter this BBMD and one BBMD for each other subnet, the same list on every BBMD."));
                break;
            case TableReadStatus.Ok:
                AnalyzeBdt(f, target, bdt.Entries, peers, adapter);
                break;
        }

        // --- the foreign device table ---
        switch (fdt.Status)
        {
            case TableReadStatus.Refused:
                f.Add(new(FindingSeverity.Info, $"{t} does not accept foreign devices",
                    "It answered Read-Foreign-Device-Table with a NAK.",
                    "Foreign-device registration is switched off on this BBMD, so a PC on another subnet cannot register with it.",
                    "If you need to register this PC with it, ask whoever runs the BBMD to allow foreign devices."));
                break;
            case TableReadStatus.Ok:
                var mine = adapter is null ? null : fdt.Entries.FirstOrDefault(e => e.Device.Address.Equals(adapter.Address));
                f.Add(mine is not null
                    ? new(FindingSeverity.Info, $"This PC is registered with {t}",
                        $"{mine.Device}: registered for {mine.TtlSeconds} s; the BBMD drops it in {mine.RemainingSeconds} s unless it is renewed " +
                        "(BBMDs add 30 s of grace to every registration).",
                        "BACprobe (or another BACnet tool on this PC) registered as a foreign device.", "Nothing to do.")
                    : new(FindingSeverity.Info,
                        fdt.Entries.Count == 0 ? $"No foreign devices are registered with {t}" : $"{fdt.Entries.Count} foreign device(s) registered with {t}",
                        fdt.Entries.Count == 0 ? "The foreign device table is empty." : string.Join(", ", fdt.Entries.Select(e => e.Device)),
                        "Foreign devices are PCs or devices on other subnets that registered to receive broadcasts.",
                        "Nothing to do unless one of them should not be there."));
                break;
        }

        if (f.All(x => x.Severity == FindingSeverity.Info) && bdt.Ok && !OnlySelf(bdt.Entries, target))
            f.Insert(0, new(FindingSeverity.Info, $"{t}'s broadcast table looks right",
                $"{bdt.Entries.Count} BBMD(s) listed, all two-hop{(peers.Count > 0 ? ", and every peer that answered lists it back" : "")}.",
                "The tables agree.", "If a subnet is still missing, check that subnet has a BBMD listed here."));
        return f;
    }

    private static void AnalyzeBdt(List<NetworkFinding> f, IPEndPoint target, IReadOnlyList<BdtEntry> entries,
        IReadOnlyDictionary<IPEndPoint, TableRead<BdtEntry>> peers, AdapterInfo? adapter)
    {
        var t = target.ToString();
        if (OnlySelf(entries, target))
            f.Add(new(FindingSeverity.Info, $"{t} lists only itself",
                "Its broadcast table has no other BBMDs.",
                "It does not exchange broadcasts with any other subnet; only foreign devices registered with it reach across.",
                "Fine if that is all it is for. Otherwise add one BBMD for each other subnet, the same list on every BBMD."));
        if (!Lists(entries, target))
            f.Add(new(FindingSeverity.Warning, $"{t} is not listed in its own broadcast table",
                $"Its table lists {string.Join(", ", entries.Select(e => e.Bbmd))}, but not {t} itself.",
                "Each BBMD is meant to include itself; some products then do not pass broadcasts from other subnets on to their own.",
                $"Add {t} to its own broadcast table (and keep the same list on every BBMD)."));

        foreach (var dup in entries.GroupBy(e => e.Bbmd.ToString()).Where(g => g.Count() > 1))
            f.Add(new(FindingSeverity.Warning, $"{dup.Key} is listed {dup.Count()} times",
                $"The broadcast table of {t} has the same BBMD more than once.",
                "A typing slip when the table was entered.",
                "Remove the extra entries, so broadcasts are not forwarded twice."));

        foreach (var e in entries.Where(e => !e.IsTwoHop))
            f.Add(new(FindingSeverity.Warning, $"{e.Bbmd} uses one-hop distribution (mask {e.Mask})",
                $"{t} sends broadcasts for {e.Bbmd}'s subnet as a directed broadcast, straight onto that subnet.",
                "Most routers drop directed broadcasts by default, so devices there may never see the broadcasts (Who-Is, I-Am).",
                "Use mask 255.255.255.255 (two-hop) unless the routers have been set up to forward directed broadcasts."));

        if (adapter is not null && Subnet.Contains(adapter.Address, adapter.Mask, target.Address))
            foreach (var other in entries.Where(e => !e.Bbmd.Address.Equals(target.Address)
                                                     && Subnet.Contains(adapter.Address, adapter.Mask, e.Bbmd.Address)).Select(e => e.Bbmd).Distinct())
                f.Add(new(FindingSeverity.Warning, $"Two BBMDs on {adapter.Cidr}: {t} and {other}",
                    "Both are listed as BBMDs and both are on this PC's subnet.",
                    "Two BBMDs on one subnet each forward every broadcast, so devices get duplicates and broadcasts can loop.",
                    "Keep one BBMD per subnet and remove the other from every broadcast table."));

        foreach (var (peer, read) in peers)
        {
            if (Same(peer, target)) continue;
            switch (read.Status)
            {
                case TableReadStatus.NoAnswer or TableReadStatus.SendFailed:
                    f.Add(new(FindingSeverity.Warning, $"Peer BBMD {peer} did not answer",
                        $"{t} lists it, but it did not answer a table request from this PC.",
                        "It is down, or a firewall or VPN between this PC and it blocks the request (the BBMDs may still reach each other).",
                        "Check it is on, or run this check from a PC on its subnet."));
                    break;
                case TableReadStatus.Refused:
                    f.Add(new(FindingSeverity.Warning, $"Peer {peer} is listed but is not acting as a BBMD",
                        $"{t} forwards broadcasts to it, but it refused to show a broadcast table.",
                        "Its BBMD function is off, so the broadcasts forwarded to it are dropped and its subnet never sees them.",
                        "Switch its BBMD function on, or take it out of the broadcast tables."));
                    break;
                case TableReadStatus.Ok when !Lists(read.Entries, target):
                    f.Add(new(FindingSeverity.Problem, $"One-way: {peer} does not list {t}",
                        $"{t} forwards broadcasts to {peer}, but {peer}'s table does not include {t}.",
                        $"Broadcasts travel from {t}'s subnet to {peer}'s, but not back: Who-Is and I-Am broadcast on {peer}'s subnet never reach {t}'s, so devices are found from one side only.",
                        $"Add {t} to {peer}'s broadcast table. Every BBMD should have the same list."));
                    break;
                case TableReadStatus.Ok:
                    var missing = read.Entries.Where(e => !Lists(entries, e.Bbmd)).Select(e => e.Bbmd.ToString()).ToList();
                    var extra = entries.Where(e => !Lists(read.Entries, e.Bbmd)).Select(e => e.Bbmd.ToString()).ToList();
                    if (missing.Count > 0 || extra.Count > 0)
                        f.Add(new(FindingSeverity.Warning, $"The broadcast tables of {t} and {peer} differ",
                            (missing.Count > 0 ? $"{peer} lists {string.Join(", ", missing)}, which {t} does not. " : "") +
                            (extra.Count > 0 ? $"{t} lists {string.Join(", ", extra)}, which {peer} does not." : ""),
                            "The tables were edited on one BBMD and not copied to the others.",
                            "Make the broadcast table identical on every BBMD."));
                    break;
            }
        }
    }
}

/// <summary>Reads a BBMD's tables (and its peers' broadcast tables) and judges them.</summary>
public static class BbmdChecker
{
    public static async Task<BbmdReport> CheckAsync(IPEndPoint target, AdapterInfo adapter, bool readPeers = true,
        BbmdTableReader? reader = null, CancellationToken ct = default)
    {
        reader ??= new BbmdTableReader(adapter.Address);
        var bdt = await reader.ReadBdtAsync(target, ct);
        var fdt = await reader.ReadFdtAsync(target, ct);

        var peers = new Dictionary<IPEndPoint, TableRead<BdtEntry>>();
        if (readPeers && bdt.Ok)
        {
            var others = bdt.Entries.Select(e => e.Bbmd)
                .Where(p => !(p.Address.Equals(target.Address) && p.Port == target.Port))
                .DistinctBy(p => p.ToString()).ToList();
            var reads = await Task.WhenAll(others.Select(p => reader.ReadBdtAsync(p, ct)));
            for (var i = 0; i < others.Count; i++) peers[others[i]] = reads[i];
        }
        return BbmdCheck.Build(target, bdt, fdt, peers, adapter);
    }
}
