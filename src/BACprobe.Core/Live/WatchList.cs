using System.IO.BACnet;

namespace BACprobe.Core.Live;

/// <summary>One point on someone's watch list. <see cref="Label"/> is the point's name when it was added, so a saved job can still show it offline.</summary>
public sealed record WatchEntry(uint Device, BacnetObjectId Point, string Label);

/// <summary>
/// The points a tech wants to keep an eye on across devices ("the three supply fans, the boiler enable, the outdoor air temp").
/// Kept with the job file. Order is the order added; a point can be on the list once.
/// </summary>
public sealed class WatchList
{
    /// <summary>Enough for any realistic visit; a bigger list would be a point-list export, not a watch list.</summary>
    public const int MaxEntries = 200;

    private readonly List<WatchEntry> _items = [];

    public int Count => _items.Count;
    public IReadOnlyList<WatchEntry> Items => [.. _items];

    public bool Contains(uint device, BacnetObjectId point) => _items.Any(e => e.Device == device && e.Point == point);

    /// <summary>Add a point. False (and nothing changes) if it is already there or the list is full.</summary>
    public bool Add(uint device, BacnetObjectId point, string label)
    {
        if (_items.Count >= MaxEntries || Contains(device, point)) return false;
        _items.Add(new WatchEntry(device, point, label));
        return true;
    }

    public bool Remove(uint device, BacnetObjectId point) =>
        _items.RemoveAll(e => e.Device == device && e.Point == point) > 0;

    public void Clear() => _items.Clear();

    /// <summary>Replace the list with saved entries (duplicates and anything past the cap are dropped).</summary>
    public void Load(IEnumerable<WatchEntry> entries)
    {
        _items.Clear();
        foreach (var e in entries) Add(e.Device, e.Point, e.Label);
    }

    /// <summary>Who is on the list, grouped by device, in the order the devices first appear.</summary>
    public IReadOnlyList<IGrouping<uint, WatchEntry>> ByDevice() => [.. _items.GroupBy(e => e.Device)];
}
