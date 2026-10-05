using BACprobe.Core.Discovery;
using BACprobe.Core.Mstp;

namespace BACprobe.Cli;

internal static partial class Program
{
    /// <summary>
    /// Listen to an MS/TP trunk without transmitting and report who is on it, how busy it is and what looks wrong.
    /// The adapter must be an RS-485 one wired to the trunk (A, B and the shield or reference). A thin console layer over
    /// <see cref="MstpMonitor"/> and <see cref="MstpPortCapture"/>, which the window uses too.
    /// </summary>
    private static async Task<int> MstpMonitorAsync(Dictionary<string, string?> opts)
    {
        if (opts.ContainsKey("list"))
        {
            var ports = MstpPortCapture.PortNames();
            Console.WriteLine(ports.Length == 0 ? "No serial ports found. Plug in the USB-RS485 adapter and install its driver." : string.Join(Environment.NewLine, ports));
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

        int baud;
        if (string.Equals(opts.GetValueOrDefault("baud"), "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (opts.GetValueOrDefault("port") is not { } autoPort)
                return Fail("--baud auto needs a port: bacprobe mstp-monitor --port COM5 --baud auto");
            Console.WriteLine($"Finding the trunk's baud rate on {autoPort} (listening only)...");
            try
            {
                var (picked, trials, explanation) = await Task.Run(() => MstpPortCapture.DetectBaud(autoPort, 3));
                foreach (var t in trials) Console.WriteLine($"  {t.Baud,6} baud: {t.GoodFrames} good frames, {t.Errors} damaged");
                Console.WriteLine(explanation);
                if (picked is null) return 3;
                baud = picked.Value;
            }
            catch (IOException ex) { return Fail(ex.Message); }
        }
        else baud = IntOpt(opts, "baud", 38400);
        if (baud <= 0) return Fail("--baud needs a number such as 38400, or 'auto'.");

        var showFrames = opts.ContainsKey("frames");
        MstpMonitor monitor;

        if (opts.GetValueOrDefault("replay") is { } replay)
        {
            if (!File.Exists(replay)) return Fail($"No file {replay}. Likely cause: a typo in the path. Next step: record one first with --record.");
            var bytes = await File.ReadAllBytesAsync(replay);
            monitor = MstpMonitor.FromRecording(bytes, baud, keepFrames: showFrames ? int.MaxValue : 2000);
            Console.WriteLine($"Replayed {bytes.Length} bytes from {replay} at {baud} baud.");
            if (showFrames) PrintMstpLog(monitor.LogSince(0));
        }
        else
        {
            if (opts.GetValueOrDefault("port") is not { } port)
                return Fail("Say which serial port to listen on: bacprobe mstp-monitor --port COM5 [--baud 38400]. 'bacprobe mstp-monitor --list' shows the ports.");
            if (OperatingSystem.IsWindows())
                foreach (var l in FtdiLatency.ReadAll().Where(l => string.Equals(l.PortName, port, StringComparison.OrdinalIgnoreCase) && l.IsTooHigh))
                    Console.WriteLine("Warning: " + l.Verdict);

            var seconds = IntOpt(opts, "seconds", 30);
            monitor = new MstpMonitor(baud);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            using var capture = new MstpPortCapture(port, monitor, opts.GetValueOrDefault("record"));
            capture.Failed += message => { Console.Error.WriteLine(message); cts.Cancel(); };
            try { capture.Start(); }
            catch (IOException ex) { return Fail(ex.Message); }

            Console.WriteLine($"Listening on {port} at {baud} baud for {seconds} s (Ctrl+C to stop). Nothing is transmitted.");
            long shown = 0;
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(300, cts.Token); }
                catch (OperationCanceledException) { break; }
                if (!showFrames) continue;
                PrintMstpLog(monitor.LogSince(shown));
                shown = monitor.LastSequence;
            }
            capture.Stop();
            if (showFrames) PrintMstpLog(monitor.LogSince(shown));
        }

        var snapshot = monitor.Snapshot();
        PrintMstpReport(snapshot);

        if (opts.GetValueOrDefault("pcap") is { } pcapPath)
        {
            if (monitor.ExportableFrames == 0) Console.WriteLine("\nNo good frames to write to the pcap file.");
            else if (File.Exists(pcapPath) && !opts.ContainsKey("force")) return Fail($"\n{pcapPath} already exists. Use --force to overwrite it.");
            else Console.WriteLine($"\nWrote {monitor.ExportPcap(pcapPath)} frames to {pcapPath}. Open it in Wireshark. Damaged frames are not included.");
        }
        return snapshot.Frames == 0 ? 3 : 0;
    }

    private static void PrintMstpLog(IEnumerable<MstpLogLine> lines)
    {
        foreach (var l in lines) Console.WriteLine($"{l.Time,9}  {l.Text}");
    }

    private static void PrintMstpReport(MstpSnapshot s)
    {
        Console.WriteLine();
        Console.WriteLine(s.Summary);

        if (s.Nodes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{"MAC",4} {"Role",-8} {"Frames",7} {"Token in",9} {"Missed",7} {"Polls",6} {"Data",6} {"Bad turns",10} {"Loop",8} {"Pickup avg / max",18}");
            foreach (var n in s.Nodes)
                Console.WriteLine($"{n.Mac,4} {n.Role,-8} {n.FramesSent,7} {n.TokensReceived,9} {n.TokenNotTaken,7} {n.PollsSent,6} {n.DataFrames,6} {n.BadTurns,10} {n.AverageLoop,8} {n.Pickup,18}");
            Console.WriteLine("(\"slave?\" means the node only ever answered: a slave, or a master that never held the token while we listened.)");
        }

        Console.WriteLine();
        if (s.Findings.Count == 0) Console.WriteLine("Nothing looks wrong.");
        else PrintFindings(s.Findings);
    }
}
