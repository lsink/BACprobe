using System.IO.BACnet;

namespace BACprobe.Core.Jobs;

/// <summary>One note a tech wrote about a device (<see cref="Point"/> null) or one of its points. Free text, never read from a device.</summary>
public sealed record NoteEntry(uint Device, BacnetObjectId? Point, string Text);

/// <summary>
/// The notes for one visit, kept with the job file: "AHU-2 supply fan belt slipping, told site contact", "BV 7 wired
/// backwards, left overridden on purpose". Thread-confined to the UI; saving takes a copy of <see cref="All"/>.
/// </summary>
public sealed class SessionNotes
{
    /// <summary>Notes longer than this are cut when saved: a note is a reminder, not a document, and the file must stay small.</summary>
    public const int MaxLength = 4000;

    private readonly Dictionary<(uint Device, int Type, uint Instance), string> _notes = [];

    // The device note itself lives under a point that no real object can have.
    private const int DeviceNoteType = -1;

    private static (uint, int, uint) Key(uint device, BacnetObjectId? point) =>
        point is { } p ? (device, (int)p.type, p.instance) : (device, DeviceNoteType, 0);

    public int Count => _notes.Count;

    public string Get(uint device, BacnetObjectId? point = null) =>
        _notes.TryGetValue(Key(device, point), out var t) ? t : "";

    /// <summary>Set or replace a note. Empty or whitespace-only text deletes it.</summary>
    public void Set(uint device, BacnetObjectId? point, string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length > MaxLength) t = t[..MaxLength];
        if (t.Length == 0) _notes.Remove(Key(device, point));
        else _notes[Key(device, point)] = t;
    }

    public bool Has(uint device, BacnetObjectId? point = null) => _notes.ContainsKey(Key(device, point));

    /// <summary>How many notes this device has, counting its own note and its points'.</summary>
    public int CountFor(uint device) => _notes.Keys.Count(k => k.Device == device);

    public IReadOnlyList<NoteEntry> All => [.. _notes
        .OrderBy(n => n.Key.Device).ThenBy(n => n.Key.Type).ThenBy(n => n.Key.Instance)
        .Select(n => new NoteEntry(n.Key.Device, n.Key.Type == DeviceNoteType ? null : new BacnetObjectId((BacnetObjectTypes)n.Key.Type, n.Key.Instance), n.Value))];

    public void Clear() => _notes.Clear();

    public void Load(IEnumerable<NoteEntry> entries)
    {
        _notes.Clear();
        foreach (var e in entries) Set(e.Device, e.Point, e.Text);
    }
}
