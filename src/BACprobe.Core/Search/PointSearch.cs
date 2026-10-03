using System.Globalization;
using BACprobe.Core.Browsing;
using BACprobe.Core.Export;

namespace BACprobe.Core.Search;

/// <summary>One matching point and how well it matched. Higher scores are better matches.</summary>
public sealed record PointHit(ExportDevice Device, ObjectSummary Point, int Score)
{
    public uint DeviceInstance => Device.Device.InstanceId;
    public string DeviceName => Device.Name;
}

/// <summary>
/// Find points by words across any number of devices. Every word must match somewhere (name, description, type, units,
/// device name, or value), so "zone temp" finds "Zone Temp" and "Zone Temperature Sensor". A word in the point's name
/// counts for more than one in its description. Quote a phrase to match it exactly: "supply fan". Add
/// <c>is:overridden</c> to see only points someone has overridden.
/// </summary>
public static class PointSearch
{
    /// <summary>Split a query into words and quoted phrases.</summary>
    public static IReadOnlyList<string> Tokens(string query)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in query)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) tokens.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Lowercase, and treat "-" and "_" as spaces, so "zone-temp", "Zone_Temp" and "zone temp" all match each other.</summary>
    public static string Normalize(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.ToLower(CultureInfo.InvariantCulture).Replace('-', ' ').Replace('_', ' ');

    public static IReadOnlyList<PointHit> Search(IEnumerable<ExportDevice> devices, string query, int max = 1000)
    {
        var words = new List<string>();
        var onlyOverridden = false;
        foreach (var t in Tokens(query))
        {
            if (string.Equals(t, "is:overridden", StringComparison.OrdinalIgnoreCase)) onlyOverridden = true;
            else words.Add(Normalize(t).Trim());
        }
        words.RemoveAll(w => w.Length == 0);
        if (words.Count == 0 && !onlyOverridden) return [];

        var whole = string.Join(' ', words);
        var hits = new List<PointHit>();
        foreach (var device in devices)
        {
            var deviceName = Normalize(device.Name);
            foreach (var p in device.Points)
            {
                if (onlyOverridden && !p.IsOverridden) continue;
                var score = ScoreOne(p, deviceName, words, whole);
                if (score >= 0) hits.Add(new PointHit(device, p, score));
            }
        }

        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.DeviceInstance)
            .ThenBy(h => (int)h.Point.Id.type).ThenBy(h => h.Point.Id.instance).Take(max)];
    }

    /// <summary>The score for one point, or -1 if any word does not match.</summary>
    private static int ScoreOne(ObjectSummary p, string deviceName, List<string> words, string whole)
    {
        var name = Normalize(p.Name);
        var description = Normalize(p.Description);
        var label = Normalize($"{BacnetNames.ObjectLabel(p.Id)} {BacnetNames.ObjectTypeShort(p.Id.type)} {p.Id.instance}");
        var other = Normalize($"{p.Units} {p.DisplayValue}"); // "occupied" finds a multi-state point showing "Occupied (2)"

        var score = 0;
        foreach (var w in words)
        {
            if (name.StartsWith(w, StringComparison.Ordinal) || name.Contains(' ' + w, StringComparison.Ordinal)) score += 100; // start of the name or of a word in it
            else if (name.Contains(w, StringComparison.Ordinal)) score += 70;
            else if (description.Contains(w, StringComparison.Ordinal)) score += 40;
            else if (label.Contains(w, StringComparison.Ordinal)) score += 30;
            else if (deviceName.Contains(w, StringComparison.Ordinal)) score += 20;
            else if (other.Contains(w, StringComparison.Ordinal)) score += 10;
            else return -1;
        }
        if (whole.Length > 0 && name == whole) score += 50; // exactly the name
        return score;
    }
}
