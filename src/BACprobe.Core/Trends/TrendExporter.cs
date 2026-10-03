using System.Globalization;
using BACprobe.Core.Export;
using ClosedXML.Excel;

namespace BACprobe.Core.Trends;

public static class TrendExporter
{
    private static string[] Header(bool pcClock) =>
        [pcClock ? "Time (this PC)" : "Time (device clock)", "Value", "Units", "Meaning", "Status flags"];

    /// <summary>Save the records as CSV or Excel. Goes to a temp file first, so a failure never leaves a half-written export.</summary>
    /// <param name="pcClock">True for a live trend sampled by BACprobe: its times are this PC's clock, not the device's.</param>
    public static void Write(string path, ExportFormat format, string deviceName, uint deviceInstance, string logName,
        TrendLogInfo? info, IReadOnlyList<TrendRecord> records, bool pcClock = false)
    {
        var temp = path + ".tmp";
        try
        {
            switch (format)
            {
                case ExportFormat.Csv: CsvWriter.WriteFile(temp, CsvLines(records, info?.Units, pcClock)); break;
                case ExportFormat.Xlsx: WriteXlsx(temp, deviceName, deviceInstance, logName, info, records, pcClock); break;
                default: throw new ArgumentException("Trend data can be exported as CSV or Excel.", nameof(format));
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static IEnumerable<string> CsvLines(IReadOnlyList<TrendRecord> records, string? units, bool pcClock = false)
    {
        yield return CsvWriter.Line(Header(pcClock));
        foreach (var r in records)
            yield return CsvWriter.Line(
            [
                r.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                r.IsValue ? r.Text : "",
                r.IsValue ? CsvWriter.SafeText(units) : "",
                r.IsValue ? "" : CsvWriter.SafeText(r.Text),
                CsvWriter.SafeText(r.Flags),
            ]);
    }

    private static void WriteXlsx(string path, string deviceName, uint deviceInstance, string logName, TrendLogInfo? info,
        IReadOnlyList<TrendRecord> records, bool pcClock)
    {
        using var wb = new XLWorkbook();
        var header = Header(pcClock);

        var sheet = wb.AddWorksheet("Trend");
        for (var c = 0; c < header.Length; c++) sheet.Cell(1, c + 1).Value = header[c];
        var row = 2;
        foreach (var r in records)
        {
            sheet.Cell(row, 1).SetValue(r.Time);
            sheet.Cell(row, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
            if (r.IsValue && r.Number is { } n) sheet.Cell(row, 2).SetValue(n); // a real number, so Excel can chart it
            else if (r.IsValue) sheet.Cell(row, 2).SetValue(CsvWriter.StripControl(r.Text));
            if (r.IsValue && !string.IsNullOrEmpty(info?.Units)) sheet.Cell(row, 3).SetValue(CsvWriter.StripControl(info!.Units!));
            if (!r.IsValue) sheet.Cell(row, 4).SetValue(CsvWriter.StripControl(r.Text));
            sheet.Cell(row, 5).SetValue(CsvWriter.StripControl(r.Flags));
            row++;
        }
        sheet.Range(1, 1, 1, header.Length).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        if (row > 2) sheet.Range(1, 1, row - 1, header.Length).SetAutoFilter();
        sheet.Columns(1, header.Length).AdjustToContents(1, Math.Min(row, 500), 8, 40);

        var about = wb.AddWorksheet("Log");
        void Line(int r, string label, string? value)
        {
            about.Cell(r, 1).SetValue(label);
            about.Cell(r, 1).Style.Font.Bold = true;
            about.Cell(r, 2).SetValue(CsvWriter.StripControl(value ?? ""));
        }
        var s = TrendStats.Summarize(records);
        Line(1, "Device", $"{deviceName} (device {deviceInstance})");
        Line(2, "Trend log", logName);
        Line(3, "Logging", info?.Source ?? "");
        Line(4, "Records exported", records.Count.ToString(CultureInfo.InvariantCulture));
        Line(5, "Records in device buffer", info?.RecordCount?.ToString(CultureInfo.InvariantCulture) ?? "");
        Line(6, "Log interval", info?.LogInterval is { } i ? $"{i.TotalSeconds:0.##} s" : "on change / unknown");
        if (s is not null)
        {
            Line(7, "Minimum", s.Min.ToString("0.##", CultureInfo.InvariantCulture));
            Line(8, "Maximum", s.Max.ToString("0.##", CultureInfo.InvariantCulture));
            Line(9, "Average", s.Average.ToString("0.##", CultureInfo.InvariantCulture));
        }
        if (pcClock) Line(11, "Times are this PC's clock", "sampled live by BACprobe while its window was open");
        else Line(11, "Times are the device's own clock", "BACnet logs carry no time zone");
        about.Columns(1, 2).AdjustToContents();

        using var stream = File.Create(path);
        wb.SaveAs(stream);
    }
}
