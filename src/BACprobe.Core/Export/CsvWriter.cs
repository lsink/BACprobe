using System.Text;

namespace BACprobe.Core.Export;

/// <summary>RFC 4180 CSV with spreadsheet-formula neutralising for text that came off the network.</summary>
public static class CsvWriter
{
    /// <summary>
    /// Names and descriptions are chosen by whoever programmed the device. Excel treats a cell starting with
    /// = + - @ (or tab/CR) as a formula, so such text is prefixed with an apostrophe.
    /// </summary>
    public static string SafeText(string? text)
    {
        var t = StripControl(text ?? "");
        return t.Length > 0 && t[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + t : t;
    }

    /// <summary>Remove characters that are not valid in XML/CSV text (device strings can contain anything).</summary>
    public static string StripControl(string text)
    {
        if (!text.Any(c => char.IsControl(c) && c is not '\t' and not '\n' and not '\r')) return text;
        return new string([.. text.Where(c => !char.IsControl(c) || c is '\t' or '\n' or '\r')]);
    }

    public static string Quote(string field, char delimiter)
    {
        var needs = field.Contains(delimiter) || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
                    || field.StartsWith(' ') || field.EndsWith(' ');
        return needs ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }

    public static string Line(IEnumerable<string> fields, char delimiter = ',') =>
        string.Join(delimiter, fields.Select(f => Quote(f, delimiter)));

    /// <summary>UTF-8 with a BOM so Excel opens accented characters correctly; CRLF line endings.</summary>
    public static void WriteFile(string path, IEnumerable<string> lines)
    {
        using var w = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        foreach (var l in lines) w.Write(l + "\r\n");
    }
}
