using System.IO.BACnet;

namespace BACprobe.Core.Browsing;

public enum CompareKind
{
    Same,
    /// <summary>Both sides have it and the value differs.</summary>
    Different,
    OnlyInA,
    OnlyInB,
}

/// <summary>One object (matched by type and instance) on two devices.</summary>
public sealed record PointCompareRow(BacnetObjectId Id, string? NameA, string? ValueA, string? NameB, string? ValueB, CompareKind Kind,
    bool IsLiveInput, bool NamesDiffer)
{
    public string Object => $"{BacnetNames.ObjectTypeShort(Id.type)} {Id.instance}";
    public string Name => NameA ?? NameB ?? "";
    public string KindText => Kind switch
    {
        CompareKind.Same => "same",
        CompareKind.Different => IsLiveInput ? "different (live input)" : "different",
        CompareKind.OnlyInA => "only on A",
        _ => "only on B",
    };
}

/// <summary>One property of one object on two devices.</summary>
public sealed record PropertyCompareRow(string Property, string? ValueA, string? ValueB, CompareKind Kind)
{
    public string KindText => Kind switch
    {
        CompareKind.Same => "same",
        CompareKind.Different => "different",
        CompareKind.OnlyInA => "only on A",
        _ => "only on B",
    };
}

/// <summary>
/// Lines two similar devices (or the same point on two devices) up side by side, to find what is set differently: a setpoint
/// someone changed, a point that is missing on one, a point named differently. Pure, so it can be tested.
/// </summary>
public static class Comparison
{
    /// <summary>Compare two devices' points, matched by object type and instance. Rows come out in object order.</summary>
    public static IReadOnlyList<PointCompareRow> ComparePoints(IReadOnlyList<ObjectSummary> a, IReadOnlyList<ObjectSummary> b)
    {
        // A device's own object has its device number as the instance, so two devices never share one: match them as "the device".
        static BacnetObjectId Key(BacnetObjectId id) => id.type == BacnetObjectTypes.OBJECT_DEVICE ? new BacnetObjectId(id.type, 0) : id;
        var bById = b.GroupBy(o => Key(o.Id)).ToDictionary(g => g.Key, g => g.First());
        var aById = a.GroupBy(o => Key(o.Id)).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<PointCompareRow>();

        foreach (var (id, x) in aById)
        {
            var live = IsInput(id.type);
            if (bById.TryGetValue(id, out var y))
            {
                var same = Normalise(x.ValueText) == Normalise(y.ValueText);
                var isDevice = id.type == BacnetObjectTypes.OBJECT_DEVICE; // two devices are named differently by definition
                rows.Add(new PointCompareRow(id, x.Name, x.ValueText, y.Name, y.ValueText, same ? CompareKind.Same : CompareKind.Different,
                    live, !isDevice && !string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)));
            }
            else rows.Add(new PointCompareRow(id, x.Name, x.ValueText, null, null, CompareKind.OnlyInA, live, false));
        }
        foreach (var (id, y) in bById.Where(p => !aById.ContainsKey(p.Key)))
            rows.Add(new PointCompareRow(id, null, null, y.Name, y.ValueText, CompareKind.OnlyInB, IsInput(id.type), false));

        return [.. rows.OrderBy(r => (int)r.Id.type).ThenBy(r => r.Id.instance)];
    }

    /// <summary>Inputs follow the real world, so two devices will rarely agree on them; this lets a view put them aside.</summary>
    public static bool IsInput(BacnetObjectTypes type) =>
        type is BacnetObjectTypes.OBJECT_ANALOG_INPUT or BacnetObjectTypes.OBJECT_BINARY_INPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT
            or BacnetObjectTypes.OBJECT_PULSE_CONVERTER or BacnetObjectTypes.OBJECT_ACCUMULATOR;

    /// <summary>Compare one object's properties on two devices, matched by property name.</summary>
    public static IReadOnlyList<PropertyCompareRow> CompareProperties(IReadOnlyList<PropertyRow> a, IReadOnlyList<PropertyRow> b)
    {
        var bBy = b.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<PropertyCompareRow>();
        foreach (var x in a)
        {
            if (rows.Any(r => r.Property == x.Name)) continue;
            rows.Add(bBy.TryGetValue(x.Name, out var y)
                ? new PropertyCompareRow(x.Name, x.Display, y.Display, Normalise(x.Display) == Normalise(y.Display) ? CompareKind.Same : CompareKind.Different)
                : new PropertyCompareRow(x.Name, x.Display, null, CompareKind.OnlyInA));
        }
        var seen = a.Select(p => p.Name).ToHashSet();
        foreach (var y in b.Where(p => !seen.Contains(p.Name)))
            if (rows.All(r => r.Property != y.Name))
                rows.Add(new PropertyCompareRow(y.Name, null, y.Display, CompareKind.OnlyInB));
        return rows;
    }

    private static string Normalise(string? s) => (s ?? "").Trim();

    /// <summary>One line for the top of the view: "12 differ, 3 only on A, 0 only on B, 86 the same".</summary>
    public static string Summarise(IReadOnlyList<PointCompareRow> rows, bool ignoreLiveInputs)
    {
        var shown = ignoreLiveInputs ? rows.Where(r => !(r.IsLiveInput && r.Kind == CompareKind.Different)).ToList() : rows.ToList();
        var ignored = rows.Count - shown.Count;
        var text = $"{shown.Count(r => r.Kind == CompareKind.Different)} differ, {shown.Count(r => r.Kind == CompareKind.OnlyInA)} only on A, " +
                   $"{shown.Count(r => r.Kind == CompareKind.OnlyInB)} only on B, {shown.Count(r => r.Kind == CompareKind.Same)} the same";
        return ignored > 0 ? text + $" ({ignored} live input difference(s) set aside)" : text;
    }
}
