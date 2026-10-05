using BACprobe.Core.Discovery;

namespace BACprobe.Core.Mstp;

/// <summary>
/// Whether it is safe to join a live MS/TP trunk as a master, and at which address. Joining TRANSMITS on the building's trunk
/// (it answers Poll For Master, takes the token and passes it on), so a wrong MAC, a noisy bus or a slow adapter can disturb real
/// equipment. <see cref="CanJoin"/> is false whenever there is a reason not to.
/// </summary>
public sealed record MstpJoinPlan(bool CanJoin, byte Mac, byte MaxMaster, IReadOnlyList<NetworkFinding> Notes)
{
    /// <summary>The reasons not to join (Problem), then cautions (Warning), then what will happen (Info).</summary>
    public IReadOnlyList<NetworkFinding> Refusals => [.. Notes.Where(n => n.Severity == FindingSeverity.Problem)];
}

public static class MstpJoinPlanner
{
    /// <summary>A master address is 0-127. 128-254 are slaves, 255 is broadcast.</summary>
    public const byte HighestMasterMac = 127;

    /// <summary>Enough good frames to be sure this is a live MS/TP trunk at this baud rate and not noise.</summary>
    public const int MinFramesHeard = 20;

    public const double RefuseErrorRate = 0.05;
    public const double WarnErrorRate = 0.01;

    /// <summary>
    /// Decide from a passive survey of the trunk. <paramref name="requestedMac"/> null means "pick one for me".
    /// <paramref name="latencyTooHigh"/> is true when the adapter's USB latency timer is over 2 ms (see <see cref="FtdiLatency"/>).
    /// </summary>
    public static MstpJoinPlan Plan(MstpSnapshot survey, int baud, byte? requestedMac, bool latencyTooHigh)
    {
        var notes = new List<NetworkFinding>();
        void No(string title, string detail, string cause, string next) => notes.Add(new(FindingSeverity.Problem, title, detail, cause, next));

        var masters = survey.Nodes.Where(n => n.Role == "master").Select(n => n.Mac).ToList();
        var seen = survey.Nodes.Select(n => n.Mac).ToHashSet();
        var attempts = survey.Frames + survey.Errors;
        var errorRate = attempts == 0 ? 0 : (double)survey.Errors / attempts;

        if (survey.Frames < MinFramesHeard)
            No("Not enough MS/TP traffic heard to join safely",
                $"Only {survey.Frames} good frames were heard (at least {MinFramesHeard} are needed).",
                "Wrong COM port or baud rate, A/B not connected, or the trunk is idle with no master running.",
                "Run the passive monitor first (bacprobe mstp-monitor) and get a clean picture of the trunk. Do not transmit onto a trunk you cannot hear.");
        else if (masters.Count == 0)
            No("No master heard on the trunk",
                "Frames were heard but none came from a master passing the token.",
                "Only slaves answering something outside this capture, or a very quiet trunk.",
                "Listen longer with the passive monitor until the token is seen going round.");

        if (errorRate >= RefuseErrorRate)
            No($"The trunk is too noisy to join ({errorRate:P0} of frames damaged)",
                "Transmitting onto a trunk that is already losing frames can make it worse for the equipment on it.",
                "Electrical noise, missing or doubled termination, a bad node or a wiring fault.",
                "Fix the wiring first. The passive monitor's findings point at the node or the cause.");
        else if (errorRate >= WarnErrorRate)
            notes.Add(new(FindingSeverity.Warning, $"{errorRate:P1} of frames on this trunk are damaged",
                "The trunk is usable but not clean.", "Noise, termination or a marginal node.",
                "Joining will work, but look at the passive monitor's findings when you can."));

        if (latencyTooHigh)
            No("The USB adapter's latency timer is too high",
                "A master has to answer within milliseconds. With the FTDI default of 16 ms this PC would reply late, and the other masters would treat it as a faulty node and keep dropping the token.",
                "The adapter's driver setting was never changed from its default.",
                "In Device Manager open the port's Properties, Port Settings, Advanced, set Latency Timer to 1 ms, replug the adapter, and try again.");

        if (baud <= 19200)
            notes.Add(new(FindingSeverity.Warning, $"{baud} baud is slow",
                "Everything on a slow trunk answers slowly, and this PC will add to the traffic.", "An old install or one device that cannot go faster.",
                "Continue only if you need to, and keep your reads small."));

        // Which MAC to use.
        var knownMax = Math.Max(survey.HighestPolled, masters.Count == 0 ? -1 : masters.Max());
        byte mac = 0;
        if (requestedMac is { } want)
        {
            mac = want;
            if (want > HighestMasterMac)
                No($"MAC {want} is not a master address",
                    "Master addresses are 0 to 127. 128 and up are slaves, and a slave cannot hold the token.",
                    "The wrong address was typed.", "Pick an address from 0 to 127 that no other device uses.");
            else if (seen.Contains(want))
                No($"MAC {want} is already in use on this trunk",
                    $"A device at MAC {want} was heard. Two devices on one MAC corrupt each other's frames.",
                    "That address belongs to a real device.", "Pick another address, or let BACprobe choose one.");
            else if (knownMax >= 0 && want > knownMax)
                notes.Add(new(FindingSeverity.Warning, $"MAC {want} is above the highest address the other masters look for ({knownMax})",
                    "The masters only poll up to their Max_Master, so they may never find BACprobe and it would sit waiting for a token.",
                    "Max_Master on the existing masters is lower than this address.",
                    $"Use a free address of {knownMax} or lower, or raise Max_Master on the existing masters."));
        }
        else if (knownMax < 0)
        {
            mac = 0;
        }
        else
        {
            var free = Enumerable.Range(1, Math.Min(knownMax, HighestMasterMac)).Cast<int?>().FirstOrDefault(m => !seen.Contains((byte)m!.Value));
            if (free is { } f) mac = (byte)f;
            else
                No("No free master address to use",
                    $"Every address from 1 to {knownMax} is already in use, and the other masters only look up to {knownMax}.",
                    "A full trunk, or Max_Master set tight.", "Raise Max_Master on the existing masters, or free an address.");
        }

        var canJoin = !notes.Any(n => n.Severity == FindingSeverity.Problem);
        if (canJoin)
            notes.Add(new(FindingSeverity.Info, $"BACprobe will join as master MAC {mac}",
                $"It will answer Poll For Master, take the token when passed it, pass it on, and send your reads. Its Max_Master will be {HighestMasterMac}, so no existing master is left out of its ring.",
                "That is what an MS/TP master does.",
                "When you disconnect, the ring pauses briefly while the other masters notice MAC " + mac + " has gone. Do not leave BACprobe running unattended."));

        return new MstpJoinPlan(canJoin, mac, HighestMasterMac, [.. notes.OrderByDescending(n => n.Severity)]);
    }
}
