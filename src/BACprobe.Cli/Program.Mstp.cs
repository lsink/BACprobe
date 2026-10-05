using System.Diagnostics;
using System.IO.Ports;
using BACprobe.Core.Discovery;
using BACprobe.Core.Mstp;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>
    /// Listen to an MS/TP trunk without transmitting and report who is on it, how busy it is and what looks wrong.
    /// The adapter must be an RS-485 one wired to the trunk (A, B and the shield or reference).
    /// </summary>
    private static async Task<int> MstpMonitorAsync(Dictionary<string, string?> opts)
    {
        if (opts.ContainsKey("list"))
        {
            var ports = SerialPort.GetPortNames();
            Console.WriteLine(ports.Length == 0 ? "No serial ports found. Plug in the USB-RS485 adapter and install its driver." : string.Join(Environment.NewLine, ports.Order()));
            if (OperatingSystem.IsWindows())
                foreach (var l in FtdiLatency.ReadAll()) Console.WriteLine(l.Verdict);
            return ports.Length == 0 ? 3 : 0;
        }

        if (opts.TryGetValue("make-sample", out var samplePath) && samplePath is not null)
        {
            if (File.Exists(samplePath) && !opts.ContainsKey("force")) return Fail($"{samplePath} already exists. Use --force to overwrite it.");
            await File.WriteAllBytesAsync(samplePath, MstpSampleCapture.Build());
            Console.WriteLine($"Wrote a made-up capture to {samplePath}. Analyse it with: bacprobe mstp-monitor --replay {samplePath} --frames");
            return 0;
        }

        var autoBaud = opts.TryGetValue("baud", out var baudText) && string.Equals(baudText, "auto", StringComparison.OrdinalIgnoreCase);
        if (autoBaud)
        {
            if (!opts.TryGetValue("port", out var autoPort) || autoPort is null)
                return Fail("--baud auto needs a port: bacprobe mstp-monitor --port COM5 --baud auto");
            var detected = DetectBaud(autoPort);
            if (detected is null) return 3;
            opts["baud"] = detected.Value.ToString();
        }
        var baud = IntOpt(opts, "baud", 38400);
        if (baud <= 0) return Fail("--baud needs a number such as 38400, or 'auto'.");
        var seconds = IntOpt(opts, "seconds", 30);
        var showFrames = opts.ContainsKey("frames");

        var analyzer = new MstpBusAnalyzer(baud, Stopwatch.Frequency);
        var parser = new MstpFrameParser();
        var pcapFrames = new List<(double, MstpFrame)>();
        void Take(ReadOnlySpan<byte> bytes, long ts)
        {
            foreach (var item in parser.Feed(bytes, ts))
            {
                analyzer.Add(item);
                if (item is MstpFrame kept && pcapFrames.Count < MstpMonitor.MaxExportFrames)
                    pcapFrames.Add(((double)(kept.Timestamp - _t0) / Stopwatch.Frequency, kept));
                if (!showFrames) continue;
                Console.WriteLine(item switch
                {
                    MstpFrame f => $"{(double)(f.Timestamp - _t0) / Stopwatch.Frequency,9:0.000}  {f.Meaning}",
                    MstpError e => $"{(double)(e.Timestamp - _t0) / Stopwatch.Frequency,9:0.000}  damaged frame ({e.Kind})",
                    _ => "",
                });
            }
        }

        if (opts.TryGetValue("replay", out var replay) && replay is not null)
        {
            if (!File.Exists(replay)) return Fail($"No file {replay}. Likely cause: a typo in the path. Next step: record one first with --record.");
            // A recording has no clock: give each byte the time it would have taken on the wire.
            _t0 = 0;
            var bytes = await File.ReadAllBytesAsync(replay);
            var ticksPerByte = Stopwatch.Frequency * 10.0 / baud;
            for (var i = 0; i < bytes.Length; i += 64)
            {
                var n = Math.Min(64, bytes.Length - i);
                Take(bytes.AsSpan(i, n), (long)((i + n) * ticksPerByte));
            }
            Console.WriteLine($"Replayed {bytes.Length} bytes from {replay} at {baud} baud.");
        }
        else
        {
            if (!opts.TryGetValue("port", out var portName) || portName is null)
                return Fail("Say which serial port to listen on: bacprobe mstp-monitor --port COM5 [--baud 38400]. 'bacprobe mstp-monitor --list' shows the ports.");

            if (OperatingSystem.IsWindows())
                foreach (var l in FtdiLatency.ReadAll().Where(l => string.Equals(l.PortName, portName, StringComparison.OrdinalIgnoreCase) && l.IsTooHigh))
                    Console.WriteLine("Warning: " + l.Verdict);

            using var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One) { ReadTimeout = 100 };
            try { port.Open(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
            {
                return Fail($"Could not open {portName}: {ex.Message}\n  Likely cause: another program (a BACnet tool or terminal) has the port open, or the adapter was unplugged.\n  Next step:    close the other program, or run 'bacprobe mstp-monitor --list' to see which ports exist.");
            }

            FileStream? record = opts.TryGetValue("record", out var recPath) && recPath is not null ? File.Create(recPath) : null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            Console.WriteLine($"Listening on {portName} at {baud} baud for {seconds} s (Ctrl+C to stop). Nothing is transmitted.");

            _t0 = Stopwatch.GetTimestamp();
            var buffer = new byte[4096];
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    int n;
                    try { n = port.Read(buffer, 0, buffer.Length); }
                    catch (TimeoutException) { continue; }
                    var ts = Stopwatch.GetTimestamp();
                    record?.Write(buffer, 0, n);
                    Take(buffer.AsSpan(0, n), ts);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"The adapter stopped answering: {ex.Message} Likely cause: it was unplugged. Next step: plug it back in and run again; the report below covers what was heard.");
            }
            record?.Dispose();
        }

        analyzer.DiscardedBytes = parser.DiscardedBytes;
        analyzer.TimingKnown = !opts.ContainsKey("replay");
        PrintMstpReport(analyzer);

        if (opts.TryGetValue("pcap", out var pcapPath) && pcapPath is not null)
        {
            if (pcapFrames.Count == 0) Console.WriteLine("\nNo good frames to write to the pcap file.");
            else if (File.Exists(pcapPath) && !opts.ContainsKey("force")) return Fail($"\n{pcapPath} already exists. Use --force to overwrite it.");
            else
            {
                using (var file = File.Create(pcapPath)) MstpPcap.Write(file, pcapFrames, DateTimeOffset.Now);
                Console.WriteLine($"\nWrote {pcapFrames.Count} frames to {pcapPath}. Open it in Wireshark. Damaged frames are not included.");
            }
        }
        return analyzer.Frames == 0 ? 3 : 0;
    }

    private static long _t0;

    /// <summary>Listen for a couple of seconds at each common baud rate and pick the one that decodes frames. Receive-only.</summary>
    private static int? DetectBaud(string portName)
    {
        const int secondsEach = 3;
        Console.WriteLine($"Finding the trunk's baud rate on {portName}: listening {secondsEach} s at each of {string.Join(", ", MstpBaudDetector.Candidates)}...");
        var trials = new List<BaudTrial>();
        foreach (var candidate in MstpBaudDetector.Candidates)
        {
            var parser = new MstpFrameParser();
            int good = 0, bad = 0;
            try
            {
                using var port = new SerialPort(portName, candidate, Parity.None, 8, StopBits.One) { ReadTimeout = 100 };
                port.Open();
                var end = Stopwatch.GetTimestamp() + secondsEach * Stopwatch.Frequency;
                var buffer = new byte[4096];
                while (Stopwatch.GetTimestamp() < end)
                {
                    int n;
                    try { n = port.Read(buffer, 0, buffer.Length); }
                    catch (TimeoutException) { continue; }
                    foreach (var item in parser.Feed(buffer.AsSpan(0, n), Stopwatch.GetTimestamp()))
                        if (item is MstpFrame) good++; else bad++;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Could not open {portName}: {ex.Message}\n  Likely cause: another program has the port open, or the adapter was unplugged.\n  Next step:    close the other program and try again.");
                return null;
            }
            trials.Add(new BaudTrial(candidate, good, bad, parser.DiscardedBytes));
            Console.WriteLine($"  {candidate,6} baud: {good} good frames, {bad} damaged");
        }
        var picked = MstpBaudDetector.Pick(trials);
        Console.WriteLine(MstpBaudDetector.Explain(trials, picked));
        return picked;
    }

    private static void PrintMstpReport(MstpBusAnalyzer a)
    {
        Console.WriteLine();
        if (!a.TimingKnown)
            Console.WriteLine($"Read {a.Frames} good frames from the recording; {a.Errors} damaged. (A recording has no clock, so bus load and token loop time are not shown.)");
        else
        {
            Console.WriteLine($"Heard {a.Frames} good frames in {a.ElapsedSeconds:0.#} s at {a.Baud} baud; {a.Errors} damaged; bus {a.Utilisation:P0} busy.");
            if (a.AverageTokenLoop is { } loop) Console.WriteLine($"The token goes round in about {loop.TotalMilliseconds:0} ms.");
        }

        if (a.Nodes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{"MAC",4} {"Role",-8} {"Frames",7} {"Token in",9} {"Missed",7} {"Polls",6} {"Data",6} {"Bad turns",10}");
            foreach (var n in a.Nodes)
                Console.WriteLine($"{n.Mac,4} {(n.IsMaster ? "master" : "slave?"),-8} {n.FramesSent,7} {n.TokensReceived,9} {n.TokenNotTaken,7} {n.PollsSent,6} {n.DataFramesSent,6} {n.TurnErrors,10}");
            Console.WriteLine("(\"slave?\" means the node only ever answered: a slave, or a master that never held the token while we listened.)");
        }

        var findings = a.Findings();
        Console.WriteLine();
        if (findings.Count == 0)
        {
            Console.WriteLine("Nothing looks wrong.");
            return;
        }
        foreach (var f in findings)
        {
            var icon = f.Severity switch { FindingSeverity.Problem => "[PROBLEM]", FindingSeverity.Warning => "[WARNING]", _ => "[info]   " };
            Console.WriteLine($"{icon} {f.Title}");
            Console.WriteLine($"          {f.Detail}");
            Console.WriteLine($"          Likely cause: {f.LikelyCause}");
            Console.WriteLine($"          Next step:    {f.NextStep}");
        }
    }
}
