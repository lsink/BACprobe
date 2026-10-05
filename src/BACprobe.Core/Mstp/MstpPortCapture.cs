using System.Diagnostics;
using System.IO.Ports;

namespace BACprobe.Core.Mstp;

/// <summary>Opens a serial port and feeds what it hears to an <see cref="MstpMonitor"/>. Receive-only: nothing is ever written to the port.</summary>
public sealed class MstpPortCapture : IDisposable
{
    private readonly SerialPort _port;
    private readonly MstpMonitor _monitor;
    private readonly FileStream? _record;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MstpPortCapture(string portName, MstpMonitor monitor, string? recordPath = null)
    {
        _monitor = monitor;
        _port = new SerialPort(portName, monitor.Baud, Parity.None, 8, StopBits.One) { ReadTimeout = 100 };
        if (recordPath is not null) _record = File.Create(recordPath);
    }

    /// <summary>Raised on the reader thread if the adapter stops answering (unplugged). The text includes cause and next step.</summary>
    public event Action<string>? Failed;

    /// <summary>The COM ports Windows lists. Empty (not a crash) if the serial library cannot be loaded or the list cannot be read.</summary>
    public static string[] PortNames()
    {
        try { return [.. SerialPort.GetPortNames().Order()]; }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Open the port and start reading. Throws the open error (in <see cref="OpenErrorText"/> wording) for the caller to show.</summary>
    public void Start()
    {
        try { _port.Open(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            throw new IOException(OpenErrorText(_port.PortName, ex.Message), ex);
        }
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() =>
        {
            var buffer = new byte[4096];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int n;
                    try { n = _port.Read(buffer, 0, buffer.Length); }
                    catch (TimeoutException) { continue; }
                    var ts = Stopwatch.GetTimestamp();
                    _record?.Write(buffer, 0, n);
                    _monitor.Feed(buffer.AsSpan(0, n), ts);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                if (!token.IsCancellationRequested)
                    Failed?.Invoke($"The adapter stopped answering: {ex.Message} Likely cause: it was unplugged. Next step: plug it back in and start again; what was heard so far is still shown.");
            }
        });
    }

    public static string OpenErrorText(string port, string message) =>
        $"Could not open {port}: {message} Likely cause: another program (a BACnet tool or terminal) has the port open, or the adapter was unplugged. " +
        "Next step: close the other program, or pick another port.";

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(500); } catch (AggregateException) { }
        if (_port.IsOpen)
        {
            try { _port.Close(); } catch (IOException) { }
        }
        _record?.Flush();
    }

    public void Dispose()
    {
        Stop();
        _record?.Dispose();
        _port.Dispose();
        _cts?.Dispose();
    }

    /// <summary>
    /// Listen a few seconds at each common baud rate and pick the one that decodes frames (see <see cref="MstpBaudDetector"/>).
    /// Returns the trials too, so the window can show why nothing was picked.
    /// </summary>
    public static (int? Baud, IReadOnlyList<BaudTrial> Trials, string Explanation) DetectBaud(
        string portName, int secondsEach, Action<string>? progress = null, CancellationToken ct = default)
    {
        var trials = new List<BaudTrial>();
        foreach (var candidate in MstpBaudDetector.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"Trying {candidate} baud...");
            var parser = new MstpFrameParser();
            int good = 0, bad = 0;
            using var port = new SerialPort(portName, candidate, Parity.None, 8, StopBits.One) { ReadTimeout = 100 };
            try { port.Open(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
            {
                throw new IOException(OpenErrorText(portName, ex.Message), ex);
            }
            var end = Stopwatch.GetTimestamp() + secondsEach * Stopwatch.Frequency;
            var buffer = new byte[4096];
            while (Stopwatch.GetTimestamp() < end && !ct.IsCancellationRequested)
            {
                int n;
                try { n = port.Read(buffer, 0, buffer.Length); }
                catch (TimeoutException) { continue; }
                foreach (var item in parser.Feed(buffer.AsSpan(0, n), Stopwatch.GetTimestamp()))
                    if (item is MstpFrame) good++; else bad++;
            }
            trials.Add(new BaudTrial(candidate, good, bad, parser.DiscardedBytes));
        }
        var picked = MstpBaudDetector.Pick(trials);
        return (picked, trials, MstpBaudDetector.Explain(trials, picked));
    }
}
