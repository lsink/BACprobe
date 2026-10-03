using System.Globalization;
using System.IO.BACnet;
using BACprobe.Core.Browsing;

namespace BACprobe.Core.Trends;

/// <summary>
/// A temporary trend: one point's present value, sampled by BACprobe on a timer while its window is open. Kept in memory
/// only, never written to the device; closing the window discards it, so export first to keep it. Times are this PC's clock.
/// </summary>
public sealed class LiveTrend(BacnetObjectTypes type, IReadOnlyList<string?>? stateNames = null, int maxSamples = LiveTrend.DefaultMaxSamples)
{
    /// <summary>A day of one-second samples. Past this the oldest samples are dropped, so a window left open cannot eat memory.</summary>
    public const int DefaultMaxSamples = 86_400;

    private readonly List<TrendRecord> _records = [];
    private readonly Lock _lock = new();

    /// <summary>Samples dropped from the start because the trend reached its size limit.</summary>
    public int Dropped { get; private set; }

    public int Count
    {
        get { lock (_lock) return _records.Count; }
    }

    /// <summary>Every sample so far, oldest first.</summary>
    public IReadOnlyList<TrendRecord> Records
    {
        get { lock (_lock) return [.. _records]; }
    }

    /// <summary>The numeric samples, for the chart: on/off as 1/0, multi-state as the state number.</summary>
    public IReadOnlyList<(DateTime Time, double Value)> Series
    {
        get { lock (_lock) return [.. _records.Where(r => r is { IsValue: true, Number: not null }).Select(r => (r.Time, r.Number!.Value))]; }
    }

    /// <summary>Record the point's present value as read (e.g. "72.4", "Active", "3"). Shown with its state name when it has one.</summary>
    public TrendRecord AddValue(DateTime time, string presentValue) =>
        Add(new TrendRecord(time, TrendRecordKind.Value, StateText.Label(type, presentValue, stateNames), ToNumber(type, presentValue), ""));

    /// <summary>Record that the device did not answer, so the gap shows in the table and the export instead of being hidden.</summary>
    public TrendRecord AddNoAnswer(DateTime time, string reason) =>
        Add(new TrendRecord(time, TrendRecordKind.Error, $"no answer ({reason})", null, ""));

    public void Clear()
    {
        lock (_lock)
        {
            _records.Clear();
            Dropped = 0;
        }
    }

    private TrendRecord Add(TrendRecord r)
    {
        lock (_lock)
        {
            _records.Add(r);
            if (_records.Count > maxSamples)
            {
                var extra = _records.Count - maxSamples;
                _records.RemoveRange(0, extra);
                Dropped += extra;
            }
        }
        return r;
    }

    /// <summary>A present value as a number for the chart: analog as is, binary Active/Inactive as 1/0, multi-state as its state number.</summary>
    public static double? ToNumber(BacnetObjectTypes type, string? presentValue)
    {
        if (string.IsNullOrWhiteSpace(presentValue)) return null;
        if (StateText.IsBinary(type))
            return presentValue.Equals("Active", StringComparison.OrdinalIgnoreCase) ? 1
                : presentValue.Equals("Inactive", StringComparison.OrdinalIgnoreCase) ? 0 : null;
        return double.TryParse(presentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }
}

/// <summary>Takes one reading at a time for a <see cref="LiveTrend"/>, through its own copy of the point so it never disturbs the main window.</summary>
public sealed class LiveTrendSampler(DeviceBrowser browser, ObjectSummary point, LiveTrend trend)
{
    private readonly ObjectSummary _copy = new()
    {
        Id = point.Id, Name = point.Name, Units = point.Units, UnitsCode = point.UnitsCode, StateNames = point.StateNames,
    };

    /// <summary>Read the point now and add the sample, or a "no answer" entry if the device did not reply. Never throws except for cancellation.</summary>
    public async Task<TrendRecord> SampleAsync(CancellationToken ct = default)
    {
        try
        {
            _copy.PresentValue = null; // so a read that brings no value is recorded as such, not as the previous value again
            await browser.RefreshValuesAsync([_copy], ct);
            return _copy.PresentValue is { } pv
                ? trend.AddValue(DateTime.Now, pv)
                : trend.AddNoAnswer(DateTime.Now, "the device sent no present value");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return trend.AddNoAnswer(DateTime.Now, BacnetFailure.IsTimeout(ex) ? "timeout" : ex.Message);
        }
    }
}
