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
public sealed record PointCompareRow(BacnetObjectId Id, string? NameA, string? ValueA, string? NameB, string? ValueB, CompareKind Kind)
{
    public string Object => $"{BacnetNames.ObjectTypeShort(Id.type)} {Id.instance}";
    public string Name => NameA ?? NameB ?? "";

    /// <summary>An input follows the real world, so two devices rarely agree on it.</summary>
    public bool IsLiveInput => Comparison.IsInput(Id.type);

    /// <summary>A live input whose value differs: expected, so views set it aside by default.</summary>
    public bool IsLiveInputDifference => IsLiveInput && Kind == CompareKind.Different;

    /// <summary>On both devices under different names. Two devices' own objects are named differently by definition, so those do not count.</summary>
    public bool NamesDiffer => Kind is CompareKind.Same or CompareKind.Different && Id.type != BacnetObjectTypes.OBJECT_DEVICE
                               && !string.Equals(NameA, NameB, StringComparison.OrdinalIgnoreCase);

    public string KindText => IsLiveInputDifference ? "different (live input)" : Comparison.Word(Kind);
}

/// <summary>One property of one object on two devices.</summary>
public sealed record PropertyCompareRow(string Property, string? ValueA, string? ValueB, CompareKind Kind)
{
    public string KindText => Comparison.Word(Kind);
}

/// <summary>
/// Lines two similar devices (or the same point on two devices) up side by side, to find what is set differently: a setpoint
/// someone changed, a point that is missing on one, a point named differently. Pure, so it can be tested.
/// </summary>
public static class Comparison
{
    public static string Word(CompareKind kind) => kind switch
    {
        CompareKind.Same => "same",
        CompareKind.Different => "different",
        CompareKind.OnlyInA => "only on A",
        _ => "only on B",
    };

    /// <summary>Compare two devices' points, matched by object type and instance. Rows come out in object order.</summary>
    public static IReadOnlyList<PointCompareRow> ComparePoints(IReadOnlyList<ObjectSummary> a, IReadOnlyList<ObjectSummary> b)
    {
        // A device's own object has its device number as the instance, so two devices never share one: match them as "the device".
        static BacnetObjectId Key(BacnetObjectId id) => id.type == BacnetObjectTypes.OBJECT_DEVICE ? new BacnetObjectId(id.type, 0) : id;
        static Dictionary<BacnetObjectId, ObjectSummary> Index(IReadOnlyList<ObjectSummary> list) =>
            list.DistinctBy(o => Key(o.Id)).ToDictionary(o => Key(o.Id));
        var aById = Index(a);
        var bById = Index(b);
        var rows = new List<PointCompareRow>();

        foreach (var (id, x) in aById)
        {
            if (bById.TryGetValue(id, out var y))
                rows.Add(new PointCompareRow(id, x.Name, x.ValueText, y.Name, y.ValueText,
                    Normalise(x.ValueText) == Normalise(y.ValueText) ? CompareKind.Same : CompareKind.Different));
            else rows.Add(new PointCompareRow(id, x.Name, x.ValueText, null, null, CompareKind.OnlyInA));
        }
        foreach (var (id, y) in bById.Where(p => !aById.ContainsKey(p.Key)))
            rows.Add(new PointCompareRow(id, null, null, y.Name, y.ValueText, CompareKind.OnlyInB));

        return [.. rows.OrderBy(r => (int)r.Id.type).ThenBy(r => r.Id.instance)];
    }

    /// <summary>Inputs follow the real world, so two devices will rarely agree on them; this lets a view put them aside.</summary>
    public static bool IsInput(BacnetObjectTypes type) =>
        type is BacnetObjectTypes.OBJECT_ANALOG_INPUT or BacnetObjectTypes.OBJECT_BINARY_INPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT
            or BacnetObjectTypes.OBJECT_PULSE_CONVERTER or BacnetObjectTypes.OBJECT_ACCUMULATOR;

    /// <summary>The rows a view should show: optionally without live-input differences, optionally only what is not the same (or is renamed).</summary>
    public static IReadOnlyList<PointCompareRow> Visible(IReadOnlyList<PointCompareRow> rows, bool ignoreLiveInputs, bool differencesOnly) =>
        [.. rows.Where(r => !(ignoreLiveInputs && r.IsLiveInputDifference) && !(differencesOnly && r.Kind == CompareKind.Same && !r.NamesDiffer))];

    /// <summary>Whether anything other than set-aside live inputs differs (the CLI's exit code).</summary>
    public static bool HasDifferences(IReadOnlyList<PointCompareRow> rows, bool ignoreLiveInputs) =>
        rows.Any(r => r.Kind != CompareKind.Same && !(ignoreLiveInputs && r.IsLiveInputDifference));

    /// <summary>Compare one object's properties on two devices, matched by property name (A's order, then what only B has).</summary>
    public static IReadOnlyList<PropertyCompareRow> CompareProperties(IReadOnlyList<PropertyRow> a, IReadOnlyList<PropertyRow> b)
    {
        var bBy = b.DistinctBy(p => p.Name).ToDictionary(p => p.Name);
        var aNames = new HashSet<string>();
        var rows = new List<PropertyCompareRow>();
        foreach (var x in a.Where(p => aNames.Add(p.Name)))
            rows.Add(bBy.TryGetValue(x.Name, out var y)
                ? new PropertyCompareRow(x.Name, x.Display, y.Display, Normalise(x.Display) == Normalise(y.Display) ? CompareKind.Same : CompareKind.Different)
                : new PropertyCompareRow(x.Name, x.Display, null, CompareKind.OnlyInA));
        rows.AddRange(bBy.Values.Where(y => !aNames.Contains(y.Name)).Select(y => new PropertyCompareRow(y.Name, null, y.Display, CompareKind.OnlyInB)));
        return rows;
    }

    private static string Normalise(string? s) => (s ?? "").Trim();

    /// <summary>One line for the top of the view: "12 differ, 3 only on A, 0 only on B, 86 the same".</summary>
    public static string Summarise(IReadOnlyList<PointCompareRow> rows, bool ignoreLiveInputs)
    {
        var shown = Visible(rows, ignoreLiveInputs, differencesOnly: false);
        var ignored = rows.Count - shown.Count;
        var text = $"{shown.Count(r => r.Kind == CompareKind.Different)} differ, {shown.Count(r => r.Kind == CompareKind.OnlyInA)} only on A, " +
                   $"{shown.Count(r => r.Kind == CompareKind.OnlyInB)} only on B, {shown.Count(r => r.Kind == CompareKind.Same)} the same";
        return ignored > 0 ? text + $" ({ignored} live input difference(s) set aside)" : text;
    }
}
