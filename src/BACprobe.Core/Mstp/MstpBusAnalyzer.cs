using BACprobe.Core.Discovery;

namespace BACprobe.Core.Mstp;

/// <summary>What one MAC address has been seen doing on the bus.</summary>
public sealed class MstpNodeStats(byte mac)
{
    public byte Mac { get; } = mac;
    public int FramesSent { get; internal set; }
    public int TokensPassed { get; internal set; }
    public int TokensReceived { get; internal set; }
    public int PollsSent { get; internal set; }
    public int PollReplies { get; internal set; }
    public int DataFramesSent { get; internal set; }

    /// <summary>Times someone passed this node the token and it did nothing: the passer had to try again.</summary>
    public int TokenNotTaken { get; internal set; }

    /// <summary>Times this node took the token it was passed (it transmitted next).</summary>
    public int TokenTaken { get; internal set; }

    /// <summary>A master: it passed or received the token, polled, or answered a poll. Slaves only ever answer.</summary>
    public bool IsMaster => TokensPassed + TokensReceived + PollsSent + PollReplies > 0;

    public long LastSeen { get; internal set; }

    internal long LastTokenArrival { get; set; } = -1;
    public int LoopSamples { get; internal set; }
    internal long LoopTicksTotal { get; set; }
    public long LoopTicksMax { get; internal set; }
}

/// <summary>
/// Reads a passive capture of an MS/TP trunk and works out what is going on: who is on it, how busy it is, how often the
/// token is dropped, and whether the bus is noisy. Nothing is transmitted. Pure: feed it frames and errors in order.
/// </summary>
public sealed class MstpBusAnalyzer(int baud, long ticksPerSecond)
{
    private readonly Dictionary<byte, MstpNodeStats> _nodes = [];
    private (byte From, byte To)? _pendingPass;
    private long _first = -1, _last = -1, _wireBytes;
    private long _loopTicksTotal;
    private int _loopSamples;

    public int Baud { get; } = baud;

    /// <summary>
    /// False for a replayed raw capture: it has no real clock, so bus load and token loop time would be made up
    /// and are left out of the findings.
    /// </summary>
    public bool TimingKnown { get; set; } = true;
    public int Frames { get; private set; }
    public int HeaderCrcErrors { get; private set; }
    public int DataCrcErrors { get; private set; }
    public int BadLengths { get; private set; }
    public long DiscardedBytes { get; set; }

    /// <summary>The highest destination any master has polled: masters above this are never found (Max_Master is set too low).</summary>
    public int HighestPolled { get; private set; } = -1;

    public int Errors => HeaderCrcErrors + DataCrcErrors + BadLengths;
    public IReadOnlyList<MstpNodeStats> Nodes => [.. _nodes.Values.OrderBy(n => n.Mac)];
    public IReadOnlyList<MstpNodeStats> Masters => [.. Nodes.Where(n => n.IsMaster)];

    public double ElapsedSeconds => _first < 0 ? 0 : (double)(_last - _first) / ticksPerSecond;

    /// <summary>Share of the bus's capacity the frames used, 0 to 1 (ten bits per byte on the wire).</summary>
    public double Utilisation => ElapsedSeconds <= 0 ? 0 : Math.Min(1, _wireBytes * 10.0 / Baud / ElapsedSeconds);

    /// <summary>Average time for the token to come back to the same node, once a few passes have been seen. Null before that.</summary>
    public TimeSpan? AverageTokenLoop => _loopSamples < 3 ? null : TimeSpan.FromSeconds((double)_loopTicksTotal / _loopSamples / ticksPerSecond);

    public void Add(object item)
    {
        if (item is MstpFrame f) AddFrame(f);
        else if (item is MstpError e) AddError(e);
    }

    public void AddError(MstpError e)
    {
        Touch(e.Timestamp);
        switch (e.Kind)
        {
            case MstpErrorKind.HeaderCrc: HeaderCrcErrors++; break;
            case MstpErrorKind.DataCrc: DataCrcErrors++; break;
            default: BadLengths++; break;
        }
    }

    public void AddFrame(MstpFrame f)
    {
        Touch(f.Timestamp);
        Frames++;
        _wireBytes += f.WireBytes;

        var src = Node(f.Source);
        src.FramesSent++;
        src.LastSeen = f.Timestamp;

        // Did the node that was just passed the token take it?
        if (_pendingPass is { } pass)
        {
            if (f.Source == pass.To) Node(pass.To).TokenTaken++;
            else if (f.Source == pass.From) Node(pass.To).TokenNotTaken++; // the passer is talking again: nobody took it
            _pendingPass = null;
        }

        switch (f.Type)
        {
            case (byte)MstpFrameType.Token:
                src.TokensPassed++;
                if (f.Destination != MstpFrame.Broadcast)
                {
                    var dst = Node(f.Destination);
                    dst.TokensReceived++;
                    if (dst.LastTokenArrival >= 0)
                    {
                        var loop = f.Timestamp - dst.LastTokenArrival;
                        dst.LoopSamples++;
                        dst.LoopTicksTotal += loop;
                        dst.LoopTicksMax = Math.Max(dst.LoopTicksMax, loop);
                        _loopTicksTotal += loop;
                        _loopSamples++;
                    }
                    dst.LastTokenArrival = f.Timestamp;
                    _pendingPass = (f.Source, f.Destination);
                }
                break;
            case (byte)MstpFrameType.PollForMaster:
                src.PollsSent++;
                if (f.Destination != MstpFrame.Broadcast) HighestPolled = Math.Max(HighestPolled, f.Destination);
                break;
            case (byte)MstpFrameType.ReplyToPollForMaster:
                src.PollReplies++;
                break;
            default:
                src.DataFramesSent++;
                break;
        }
    }

    private void Touch(long ts)
    {
        if (_first < 0) _first = ts;
        _last = ts;
    }

    private MstpNodeStats Node(byte mac)
    {
        if (!_nodes.TryGetValue(mac, out var n)) _nodes[mac] = n = new MstpNodeStats(mac);
        return n;
    }

    /// <summary>What a tech should look at, worst first. Every finding says the likely cause and a next step.</summary>
    public IReadOnlyList<NetworkFinding> Findings()
    {
        var list = new List<NetworkFinding>();

        if (Frames == 0)
        {
            list.Add(Errors == 0 && DiscardedBytes == 0
                ? new(FindingSeverity.Problem, "Heard nothing on the trunk",
                    "No bytes arrived while listening.",
                    "The adapter is on the wrong COM port or not wired to the trunk, the trunk has no power or no masters, or A and B are not connected.",
                    "Check the COM port, then the A/B/shield wiring, then confirm another device on the trunk is powered.")
                : new(FindingSeverity.Problem, "Heard bytes but no valid frames",
                    $"{DiscardedBytes + Errors} bytes or damaged frames arrived and none were a good MS/TP frame.",
                    $"The baud rate here ({Baud}) does not match the trunk, A and B are swapped, or the bus is badly noisy.",
                    "Try the trunk's real baud rate (38400 and 76800 are common), swap A and B, and check termination."));
            return list;
        }

        if (Baud <= 19200)
            list.Add(new(FindingSeverity.Info, $"The trunk runs at {Baud} baud, which is slow",
                "At this rate every frame takes four to eight times longer than at 76800, so busy trunks answer slowly.",
                "An old install, or one device that cannot go faster and holds everyone back.",
                "If every device supports 38400 or 76800, raise the rate on all of them together."));

        var attempts = Frames + Errors;
        var errorRate = (double)Errors / attempts;
        if (errorRate >= 0.01)
            list.Add(new(errorRate >= 0.05 ? FindingSeverity.Problem : FindingSeverity.Warning,
                $"{Errors} damaged frames ({errorRate:P1} of all traffic)",
                $"{HeaderCrcErrors} with a bad header CRC, {DataCrcErrors} with a bad data CRC, {BadLengths} with an impossible length.",
                "Electrical noise, missing or doubled termination, a bad ground or shield, a long stub, or two devices transmitting at once.",
                "Terminate only the two ends of the trunk, check the shield is grounded at one point, and look for the node whose traffic is damaged."));

        foreach (var n in Nodes.Where(n => n.TokensReceived >= 3 || n.TokenNotTaken > 0))
        {
            var passes = n.TokenTaken + n.TokenNotTaken;
            if (n.TokenNotTaken == 0 || passes == 0) continue;
            if (n.TokenTaken == 0 && n.TokenNotTaken >= 3)
                list.Add(new(FindingSeverity.Problem, $"MAC {n.Mac} never takes the token",
                    $"It was passed the token {n.TokenNotTaken} times and never used it.",
                    "The node is off, wired badly, set to the wrong baud rate, or has a MAC that clashes with another device.",
                    $"Check power and wiring at MAC {n.Mac}, its baud rate, and that no other device has the same MAC."));
            else if ((double)n.TokenNotTaken / passes >= 0.1)
                list.Add(new(FindingSeverity.Warning, $"MAC {n.Mac} often misses the token",
                    $"It did not take the token {n.TokenNotTaken} of {passes} times.",
                    "Marginal wiring or noise near that node, or a slow device that is busy.",
                    $"Check the wiring and connections at MAC {n.Mac}."));
        }

        var masters = Masters;
        if (HighestPolled >= 0)
        {
            var tooHigh = masters.Where(m => m.Mac > HighestPolled).Select(m => m.Mac).ToList();
            if (tooHigh.Count > 0)
                list.Add(new(FindingSeverity.Warning, "Max Master is set too low",
                    $"Masters poll up to MAC {HighestPolled}, but MAC {string.Join(", ", tooHigh)} is above that.",
                    "A device's Max_Master property is lower than the highest master address, so it will not be found when the token ring changes.",
                    "Raise Max_Master on the masters to the highest master MAC (or 127), or give the high node a lower MAC."));
        }

        if (masters.Count == 1)
            list.Add(new(FindingSeverity.Info, "Only one master heard",
                $"Only MAC {masters[0].Mac} is passing or polling for the token.",
                "Either it really is the only master, or the others are off, on another baud rate or not wired in.",
                "If you expect more devices, check their power, baud rate and wiring."));

        if (TimingKnown && Utilisation >= 0.5)
            list.Add(new(Utilisation >= 0.75 ? FindingSeverity.Problem : FindingSeverity.Warning,
                $"The trunk is {Utilisation:P0} busy",
                "Frames are using most of the bus's capacity, so everything on it answers slowly.",
                "Too many devices or too much polling on one trunk, or a slow baud rate.",
                "Raise the baud rate if every device supports it, split the trunk, or reduce polling."));

        if (TimingKnown && AverageTokenLoop is { } loop && loop.TotalMilliseconds >= 500)
            list.Add(new(FindingSeverity.Warning, $"The token takes {loop.TotalMilliseconds:0} ms to go round",
                "Each master waits that long between turns, so replies and COV updates are slow.",
                "Many masters, a busy trunk, or a node that holds the token a long time.",
                "Look at the busiest MACs in the table below, and check the trunk's utilisation."));

        if (DiscardedBytes > 0 && DiscardedBytes > _wireBytes / 20)
            list.Add(new(FindingSeverity.Info, $"{DiscardedBytes} stray bytes ignored",
                "Bytes arrived that were not part of any MS/TP frame.",
                "Line noise, or a device on the trunk using a different baud rate.",
                "Check for noise sources near the cable and for a device left at the wrong baud rate."));

        return [.. list.OrderByDescending(f => f.Severity)];
    }
}
