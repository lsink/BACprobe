using System.Diagnostics;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Mstp;

/// <summary>One line of the live frame list.</summary>
public sealed record MstpLogLine(long Sequence, double Seconds, string Text, byte? Source, byte? Destination, bool IsError, byte? Type)
{
    /// <summary>The same time as shown in the list: seconds since listening started.</summary>
    public string Time => Seconds.ToString("0.000");
}

/// <summary>One node's row in the table.</summary>
public sealed record MstpNodeRow(byte Mac, string Role, int FramesSent, int TokensReceived, int TokenNotTaken, int PollsSent, int DataFrames,
    string AverageLoop, string Pickup = "-", int BadTurns = 0);

/// <summary>Everything the window shows, copied under the lock so the screen never reads a half-updated analyzer.</summary>
public sealed record MstpSnapshot(
    int Frames, int Errors, double ElapsedSeconds, double Utilisation, TimeSpan? AverageTokenLoop, bool TimingKnown, int Baud,
    IReadOnlyList<MstpNodeRow> Nodes, IReadOnlyList<NetworkFinding> Findings, long TotalBytes, int HighestPolled = -1)
{
    public string Summary
    {
        get
        {
            if (Frames == 0 && Errors == 0) return TotalBytes == 0 ? "Listening... nothing heard yet." : $"{TotalBytes} bytes heard, no valid frames yet.";
            if (!TimingKnown) return $"{Frames} good frames, {Errors} damaged (a recording has no clock, so load and token time are not shown).";
            var loop = AverageTokenLoop is { } l ? $", token goes round in about {l.TotalMilliseconds:0} ms" : "";
            return $"{Frames} good frames, {Errors} damaged, bus {Utilisation:P0} busy{loop}.";
        }
    }
}

/// <summary>
/// A running MS/TP listen: bytes in, frames, per-node stats, findings and a short live frame log out. Safe to feed from a
/// serial-port thread while the window reads snapshots. Receive-only.
/// </summary>
public sealed class MstpMonitor
{
    private readonly Lock _lock = new();
    private readonly MstpFrameParser _parser = new();
    private readonly MstpBusAnalyzer _analyzer;
    private readonly LinkedList<MstpLogLine> _log = [];
    private readonly int _keep;
    private readonly long _ticksPerSecond;
    private long _t0 = -1, _seq, _bytes;
    private readonly List<(double Seconds, MstpFrame Frame)> _frames = [];
    private DateTimeOffset _startedAt;

    /// <summary>The most frames kept for export (about 15 minutes of a busy 38400 baud trunk). Older ones are dropped from the export only.</summary>
    public const int MaxExportFrames = 300_000;

    public MstpMonitor(int baud, bool timingKnown = true, int keepFrames = 2000, long ticksPerSecond = 0)
    {
        _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency;
        _analyzer = new MstpBusAnalyzer(baud, _ticksPerSecond) { TimingKnown = timingKnown };
        _keep = keepFrames;
    }

    public int Baud => _analyzer.Baud;

    /// <summary>
    /// Analyse a saved raw capture. A recording has no clock, so each byte is given the time it would take on the wire and
    /// load / token loop time are left out of the results.
    /// </summary>
    public static MstpMonitor FromRecording(ReadOnlySpan<byte> bytes, int baud, int keepFrames = 2000)
    {
        const long tps = 1_000_000; // one tick = 1 microsecond on the pretend clock
        var m = new MstpMonitor(baud, timingKnown: false, keepFrames: keepFrames, ticksPerSecond: tps);
        var ticksPerByte = tps * 10.0 / baud;
        for (var i = 0; i < bytes.Length; i += 64)
        {
            var n = Math.Min(64, bytes.Length - i);
            m.Feed(bytes.Slice(i, n), (long)((i + n) * ticksPerByte));
        }
        return m;
    }

    /// <summary>Add bytes read from the port (or a recording), stamped with the time they arrived.</summary>
    public void Feed(ReadOnlySpan<byte> bytes, long timestamp)
    {
        lock (_lock)
        {
            if (_t0 < 0) { _t0 = timestamp; _startedAt = DateTimeOffset.Now; }
            _bytes += bytes.Length;
            foreach (var item in _parser.Feed(bytes, timestamp))
            {
                _analyzer.Add(item);
                var secs = (double)(timestamp - _t0) / _ticksPerSecond;
                if (item is MstpFrame kept)
                {
                    _frames.Add((secs, kept));
                    if (_frames.Count > MaxExportFrames + 10_000) _frames.RemoveRange(0, 10_000);
                }
                _log.AddLast(item switch
                {
                    MstpFrame f => new MstpLogLine(++_seq, secs, f.Meaning, f.Source, f.Destination, false, f.Type),
                    MstpError e => new MstpLogLine(++_seq, secs, ErrorText(e.Kind), null, null, true, null),
                    _ => new MstpLogLine(++_seq, secs, "", null, null, false, null),
                });
                while (_log.Count > _keep) _log.RemoveFirst();
            }
            _analyzer.DiscardedBytes = _parser.DiscardedBytes;
        }
    }

    private static string ErrorText(MstpErrorKind kind) => kind switch
    {
        MstpErrorKind.HeaderCrc => "Damaged frame: bad header CRC (noise, termination, baud rate, or two senders at once)",
        MstpErrorKind.DataCrc => "Damaged frame: bad data CRC",
        _ => "Damaged frame: impossible length",
    };

    /// <summary>Number of frames an export would contain right now.</summary>
    public int ExportableFrames { get { lock (_lock) return Math.Min(_frames.Count, MaxExportFrames); } }

    /// <summary>
    /// Save the good frames heard so far as a pcap file for Wireshark. Damaged frames cannot be rebuilt, so they are not in it.
    /// A recording replayed from a file has pretend timestamps. Returns how many frames were written.
    /// </summary>
    public int ExportPcap(string path)
    {
        List<(double, MstpFrame)> copy;
        DateTimeOffset start;
        lock (_lock)
        {
            copy = [.. _frames.TakeLast(MaxExportFrames)];
            start = _startedAt == default ? DateTimeOffset.Now : _startedAt;
        }
        using var file = File.Create(path);
        MstpPcap.Write(file, copy, start);
        return copy.Count;
    }

    public MstpSnapshot Snapshot()
    {
        lock (_lock)
        {
            var nodes = _analyzer.Nodes.Select(n => new MstpNodeRow(n.Mac, n.IsMaster ? "master" : "slave?", n.FramesSent, n.TokensReceived,
                n.TokenNotTaken, n.PollsSent, n.DataFramesSent,
                n.LoopSamples > 0 && _analyzer.TimingKnown ? $"{(double)n.LoopTicksTotal / n.LoopSamples / _ticksPerSecond * 1000:0} ms" : "-",
                n.PickupSamples > 0 && _analyzer.TimingKnown
                    ? $"{(double)n.PickupTicksTotal / n.PickupSamples / _ticksPerSecond * 1000:0.#} / {(double)n.PickupTicksMax / _ticksPerSecond * 1000:0.#} ms" : "-",
                n.TurnErrors)).ToList();
            return new MstpSnapshot(_analyzer.Frames, _analyzer.Errors, _analyzer.ElapsedSeconds, _analyzer.Utilisation,
                _analyzer.AverageTokenLoop, _analyzer.TimingKnown, _analyzer.Baud, nodes, _analyzer.Findings(), _bytes, _analyzer.HighestPolled);
        }
    }

    /// <summary>
    /// The lines added after <paramref name="afterSequence"/> (0 for all), oldest first, with the same filters as <see cref="Log"/>.
    /// Walks back from the newest line and stops at the first one already seen, so asking every tick costs only the new lines.
    /// </summary>
    public IReadOnlyList<MstpLogLine> LogSince(long afterSequence, byte? mac = null, bool tokenAndPollsToo = true)
    {
        lock (_lock)
        {
            var fresh = new List<MstpLogLine>();
            for (var node = _log.Last; node is not null && node.Value.Sequence > afterSequence; node = node.Previous)
                if (Passes(node.Value, mac, tokenAndPollsToo)) fresh.Add(node.Value);
            fresh.Reverse();
            return fresh;
        }
    }

    private static bool Passes(MstpLogLine l, byte? mac, bool tokenAndPollsToo) =>
        (mac is not { } m || l.Source == m || l.Destination == m) &&
        (tokenAndPollsToo || l.IsError || l.Type is not ((byte)MstpFrameType.Token or (byte)MstpFrameType.PollForMaster));

    /// <summary>The most recent log lines, optionally only those involving one MAC (as sender or receiver) or only errors.</summary>
    public IReadOnlyList<MstpLogLine> Log(byte? mac = null, bool tokenAndPollsToo = true, int max = 500)
    {
        lock (_lock)
        {
            return [.. _log.Where(l => Passes(l, mac, tokenAndPollsToo)).TakeLast(max)];
        }
    }

    /// <summary>Sequence number of the newest log line (0 before any), to pass back to <see cref="LogSince"/>.</summary>
    public long LastSequence { get { lock (_lock) return _seq; } }
}
