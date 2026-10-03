using System.Globalization;
using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using ClosedXML.Excel;

namespace BACprobe.Core.Export;

public enum ExportFormat { Csv, Xlsx, Ede }

/// <summary>One device and its points, ready to export.</summary>
public sealed record ExportDevice(DiscoveredDevice Device, string Name, IReadOnlyList<ObjectSummary> Objects)
{
    /// <summary>Points only: the device's own object is described by the device columns/sheet.</summary>
    public IEnumerable<ObjectSummary> Points => Objects.Where(o => o.Id.type != BacnetObjectTypes.OBJECT_DEVICE);
}

public static class PointExporter
{
    private static readonly string[] CsvHeader =
    [
        "Device Instance", "Device Name", "Device Address", "Object Type", "Object Instance", "Object Name",
        "Description", "Present Value", "Units", "Writable (by type)",
    ];

    public static ExportFormat? FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => ExportFormat.Csv,
        ".xlsx" => ExportFormat.Xlsx,
        _ => null,
    };

    public static string Extension(ExportFormat f) => f == ExportFormat.Xlsx ? ".xlsx" : ".csv";

    /// <summary>Read the object list and summaries of one device.</summary>
    public static async Task<ExportDevice> CollectAsync(DeviceBrowser browser, DiscoveredDevice device,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var label = device.ObjectName ?? $"device {device.InstanceId}";
        progress?.Report($"Reading the object list of {label}...");
        var ids = await browser.ReadObjectListAsync(ct);
        var counter = new Progress<int>(n => progress?.Report($"Reading {label}: {n} of {ids.Count} objects..."));
        var summaries = await browser.ReadSummariesAsync(ids, counter, ct);
        return new ExportDevice(device, device.ObjectName ?? $"Device {device.InstanceId}", summaries);
    }

    /// <summary>
    /// Guess from the object type only; BACprobe has not read each point's priority array here.
    /// Inputs are not writable; outputs and values normally are.
    /// </summary>
    public static bool IsUsuallyWritable(BacnetObjectTypes type) => type is
        BacnetObjectTypes.OBJECT_ANALOG_OUTPUT or BacnetObjectTypes.OBJECT_ANALOG_VALUE or
        BacnetObjectTypes.OBJECT_BINARY_OUTPUT or BacnetObjectTypes.OBJECT_BINARY_VALUE or
        BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE;

    /// <summary>Write the file. Goes to a temp file first so a failure never leaves a half-written export.</summary>
    public static void Write(string path, ExportFormat format, IReadOnlyList<ExportDevice> devices, string projectName = "BACprobe export")
    {
        var temp = path + ".tmp";
        try
        {
            switch (format)
            {
                case ExportFormat.Csv: CsvWriter.WriteFile(temp, CsvLines(devices)); break;
                case ExportFormat.Ede: CsvWriter.WriteFile(temp, EdeLines(devices, projectName)); break;
                case ExportFormat.Xlsx: WriteXlsx(temp, devices); break;
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static IEnumerable<string> CsvLines(IReadOnlyList<ExportDevice> devices)
    {
        yield return CsvWriter.Line(CsvHeader);
        foreach (var d in devices)
            foreach (var o in d.Points)
                yield return CsvWriter.Line(PointRow(d, o, CsvWriter.SafeText).Select(c => c is string s ? s : Convert.ToString(c, CultureInfo.InvariantCulture) ?? ""));
    }

    /// <param name="text">How to clean device-supplied text: CSV neutralises formulas, xlsx stores text cells as-is.</param>
    private static object[] PointRow(ExportDevice d, ObjectSummary o, Func<string?, string> text) =>
    [
        d.Device.InstanceId, text(d.Name), d.Device.AddressText,
        BacnetNames.ObjectTypeName(o.Id.type), o.Id.instance, text(o.Name),
        text(o.Description), text(o.DisplayValue), text(o.Units),
        IsUsuallyWritable(o.Id.type) ? "Yes" : "No",
    ];

    /// <summary>
    /// EDE (Engineering Data Exchange) layout: semicolon-separated, a short header block, then one row per point.
    /// State texts, limits and COV support are not read by BACprobe, so those columns are left empty.
    /// </summary>
    public static IEnumerable<string> EdeLines(IReadOnlyList<ExportDevice> devices, string projectName)
    {
        const char sep = ';';
        yield return CsvWriter.Line(["PROJECT NAME", CsvWriter.SafeText(projectName)], sep);
        yield return CsvWriter.Line(["VERSION OF REFERENCEFILE", "1"], sep);
        yield return CsvWriter.Line(["TIMESTAMP OF LAST CHANGE", DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)], sep);
        yield return CsvWriter.Line(["AUTHOR OF LAST CHANGE", "BACprobe"], sep);
        yield return CsvWriter.Line(["VERSION OF LAYOUT", "2.1"], sep);
        yield return CsvWriter.Line(
        [
            "# keyname", "device obj.-instance", "object-name", "object-type", "object-instance", "description",
            "present-value-default", "min-present-value", "max-present-value", "settable", "supports COV",
            "hi-limit", "low-limit", "state-text-reference", "unit-code", "vendor-specific-address",
        ], sep);

        foreach (var d in devices)
            foreach (var o in d.Points)
            {
                var name = CsvWriter.SafeText(o.Name ?? BacnetNames.ObjectLabel(o.Id));
                yield return CsvWriter.Line(
                [
                    $"{CsvWriter.SafeText(d.Name)}.{name}",
                    d.Device.InstanceId.ToString(CultureInfo.InvariantCulture),
                    name,
                    ((int)o.Id.type).ToString(CultureInfo.InvariantCulture),
                    o.Id.instance.ToString(CultureInfo.InvariantCulture),
                    CsvWriter.SafeText(o.Description),
                    "", "", "",
                    IsUsuallyWritable(o.Id.type) ? "Y" : "N",
                    "", "", "", "",
                    o.UnitsCode?.ToString(CultureInfo.InvariantCulture) ?? "",
                    "",
                ], sep);
            }
    }

    private static void WriteXlsx(string path, IReadOnlyList<ExportDevice> devices)
    {
        using var wb = new XLWorkbook();

        var points = wb.AddWorksheet("Points");
        for (var c = 0; c < CsvHeader.Length; c++) points.Cell(1, c + 1).Value = CsvHeader[c];
        var row = 2;
        foreach (var d in devices)
            foreach (var o in d.Points)
            {
                var values = PointRow(d, o, t => CsvWriter.StripControl(t ?? ""));
                for (var c = 0; c < values.Length; c++) SetCell(points.Cell(row, c + 1), values[c]);
                row++;
            }
        Style(points, CsvHeader.Length, row - 1);

        var sheet = wb.AddWorksheet("Devices");
        string[] devHeader = ["Device Instance", "Device Name", "Address", "Vendor", "Model", "Firmware", "Points"];
        for (var c = 0; c < devHeader.Length; c++) sheet.Cell(1, c + 1).Value = devHeader[c];
        var r = 2;
        foreach (var d in devices)
        {
            object[] vals =
            [
                d.Device.InstanceId, d.Name, d.Device.AddressText, d.Device.VendorName ?? $"vendor {d.Device.VendorId}",
                d.Device.ModelName ?? "", d.Device.FirmwareRevision ?? "", d.Points.Count(),
            ];
            for (var c = 0; c < vals.Length; c++) SetCell(sheet.Cell(r, c + 1), vals[c]);
            r++;
        }
        Style(sheet, devHeader.Length, r - 1);

        using var stream = File.Create(path); // a stream, because ClosedXML rejects the .tmp extension
        wb.SaveAs(stream);
    }

    /// <summary>Text is always stored as text: a device name like "=1+1" must never become a formula.</summary>
    private static void SetCell(IXLCell cell, object value)
    {
        switch (value)
        {
            case string s:
                cell.SetValue(CsvWriter.StripControl(s));
                cell.Style.NumberFormat.Format = "@"; // text format, so Excel never evaluates it
                break;
            case uint u: cell.SetValue((double)u); break;
            case int i: cell.SetValue(i); break;
            default: cell.SetValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
        }
    }

    private static void Style(IXLWorksheet ws, int columns, int lastRow)
    {
        var header = ws.Range(1, 1, 1, columns);
        header.Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        if (lastRow >= 2) ws.Range(1, 1, lastRow, columns).SetAutoFilter();
        ws.Columns(1, columns).AdjustToContents(1, Math.Max(lastRow, 1), 8, 60);
    }
}
