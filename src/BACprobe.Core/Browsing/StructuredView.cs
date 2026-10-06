using System.IO.BACnet;

namespace BACprobe.Core.Browsing;

/// <summary>One entry of a Structured View's Subordinate_List: an object, on this device unless <see cref="Device"/> says otherwise.</summary>
public sealed record SubordinateRef(BacnetObjectId Point, uint? Device = null);

/// <summary>A node in the device's own folder tree: a view (folder), a point, or a point on another device.</summary>
public sealed class StructureNode
{
    public required BacnetObjectId Id { get; init; }
    public required string Label { get; init; }
    public ObjectSummary? Point { get; init; }
    public uint? OtherDevice { get; init; }
    public bool IsView => Id.type == BacnetObjectTypes.OBJECT_STRUCTURED_VIEW;
    public List<StructureNode> Children { get; } = [];

    /// <summary>For the tree: "Zone Temp  ·  AI 1  ·  69.4 °F", or a folder name with its count.</summary>
    public string Text => IsView ? $"{Label} ({Children.Count})"
        : Point is { } p ? $"{Label}  ·  {BacnetNames.ObjectTypeShort(Id.type)} {Id.instance}" + (p.ValueText.Length > 0 ? $"  ·  {p.ValueText}" : "")
        : Label;
}

/// <summary>
/// Builds the tree a device's Structured View objects describe (views inside views, then points), plus a "Not in any view" group so no
/// point is hidden. Pure. Views that list each other in a loop are cut where the loop closes.
/// </summary>
public static class StructureTree
{
    public static readonly BacnetObjectId NotInAnyView = new(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW, uint.MaxValue);

    /// <summary>
    /// Subordinate_List as the library decodes it: one object identifier per entry, with a device identifier in front of an entry that
    /// lives on another device.
    /// </summary>
    public static IReadOnlyList<SubordinateRef> ParseSubordinates(IList<BacnetValue>? values)
    {
        var list = new List<SubordinateRef>();
        if (values is null) return list;
        var ids = values.Where(v => v.Value is BacnetObjectId).Select(v => (BacnetObjectId)v.Value).ToList();
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i].type == BacnetObjectTypes.OBJECT_DEVICE && i + 1 < ids.Count && ids[i + 1].type != BacnetObjectTypes.OBJECT_DEVICE)
            {
                list.Add(new SubordinateRef(ids[i + 1], ids[i].instance));
                i++;
            }
            else list.Add(new SubordinateRef(ids[i]));
        }
        return list;
    }

    public static IReadOnlyList<StructureNode> Build(IReadOnlyList<ObjectSummary> points, IReadOnlyDictionary<BacnetObjectId, IReadOnlyList<SubordinateRef>> views)
    {
        if (views.Count == 0) return [];
        var byId = points.ToDictionary(p => p.Id);
        var referenced = views.Values.SelectMany(v => v).Where(r => r.Device is null).Select(r => r.Point).ToHashSet();
        var roots = views.Keys.Where(v => !referenced.Contains(v)).OrderBy(v => v.instance).ToList();
        if (roots.Count == 0) roots = [.. views.Keys.OrderBy(v => v.instance)]; // every view is inside another: a loop, so start anywhere

        var placed = new HashSet<BacnetObjectId>();
        var result = roots.Select(r => Node(r, null, [])).ToList();

        var loose = points.Where(p => !placed.Contains(p.Id) && p.Id.type is not (BacnetObjectTypes.OBJECT_DEVICE or BacnetObjectTypes.OBJECT_STRUCTURED_VIEW))
            .OrderBy(p => (int)p.Id.type).ThenBy(p => p.Id.instance).ToList();
        if (loose.Count > 0)
        {
            var rest = new StructureNode { Id = NotInAnyView, Label = "Not in any view" };
            rest.Children.AddRange(loose.Select(p => new StructureNode { Id = p.Id, Label = p.Name ?? BacnetNames.ObjectLabel(p.Id), Point = p }));
            result.Add(rest);
        }
        return result;

        StructureNode Node(BacnetObjectId id, uint? device, HashSet<BacnetObjectId> path)
        {
            byId.TryGetValue(id, out var summary);
            if (device is not null)
                return new StructureNode { Id = id, Label = $"{BacnetNames.ObjectLabel(id)} on device {device}", OtherDevice = device };
            placed.Add(id);
            var node = new StructureNode { Id = id, Label = summary?.Name ?? BacnetNames.ObjectLabel(id), Point = summary };
            if (!views.TryGetValue(id, out var subs) || !path.Add(id)) return node; // a point, or a view already on the way here
            foreach (var s in subs) node.Children.Add(Node(s.Point, s.Device, path));
            path.Remove(id);
            return node;
        }
    }

    /// <summary>The tree as indented lines, for the CLI.</summary>
    public static IEnumerable<string> Lines(IReadOnlyList<StructureNode> roots)
    {
        foreach (var r in roots)
            foreach (var line in Lines(r, 0)) yield return line;
    }

    private static IEnumerable<string> Lines(StructureNode n, int depth)
    {
        yield return new string(' ', depth * 2) + (n.IsView ? "[+] " : "    ") + n.Text;
        foreach (var c in n.Children)
            foreach (var line in Lines(c, depth + 1)) yield return line;
    }
}
