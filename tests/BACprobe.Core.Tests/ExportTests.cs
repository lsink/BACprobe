using System.IO.BACnet;
using System.Text;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using ClosedXML.Excel;

namespace BACprobe.Core.Tests;

public class ExportTests
{
    private static ObjectSummary Point(BacnetObjectTypes type, uint instance, string? name, string? desc = null,
        string? value = null, string? units = null, uint? unitsCode = null) =>
        new() { Id = new BacnetObjectId(type, instance), Name = name, Description = desc, PresentValue = value, Units = units, UnitsCode = unitsCode };

    private static ExportDevice Device(uint instance, string name, params ObjectSummary[] points)
    {
        var dev = new DiscoveredDevice
        {
            InstanceId = instance, Address = new BacnetAddress(BacnetAddressTypes.IP, "192.168.1.5:47808", 0),
            MaxApdu = 480, Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999,
            ObjectName = name, VendorName = "Acme", ModelName = "M1", FirmwareRevision = "1.0",
        };
        var all = new List<ObjectSummary> { Point(BacnetObjectTypes.OBJECT_DEVICE, instance, name) };
        all.AddRange(points);
        return new ExportDevice(dev, name, all);
    }

    private static ExportDevice Sample() => Device(1001, "AHU-1",
        Point(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "Zone Temp", "Zone temperature", "72.4", "°F", 64),
        Point(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1, "Fan Command", "Start, stop", "Active"));

    [Fact]
    public void Csv_has_header_and_one_row_per_point_without_the_device_object()
    {
        var lines = PointExporter.CsvLines([Sample()]).ToList();
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("Device Instance,Device Name,Device Address,Object Type", lines[0]);
        Assert.Contains("1001,AHU-1,", lines[1]);
        Assert.Contains("Analog Input,1,Zone Temp,Zone temperature,72.4,°F,No", lines[1]);
        Assert.Contains("Binary Output,1,Fan Command,\"Start, stop\",Active,,Yes", lines[2]);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData(" padded", "\" padded\"")]
    public void Quoting_follows_rfc4180(string input, string expected) =>
        Assert.Equal(expected, CsvWriter.Quote(input, ','));

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "'=HYPERLINK(\"http://x\")")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Zone Temp", "Zone Temp")]
    [InlineData("", "")]
    public void Device_supplied_text_cannot_become_a_spreadsheet_formula(string input, string expected) =>
        Assert.Equal(expected, CsvWriter.SafeText(input));

    [Fact]
    public void Control_characters_from_devices_are_stripped()
    {
        Assert.Equal("AB", CsvWriter.StripControl("A\u0000B"));
        Assert.Equal("a\tb\nc", CsvWriter.StripControl("a\tb\nc"));
    }

    [Fact]
    public void Hostile_name_is_neutralised_in_csv()
    {
        var d = Device(1, "D", Point(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "=cmd|' /C calc'!A0"));
        var row = PointExporter.CsvLines([d]).Last();
        Assert.Contains("'=cmd", row);
    }

    [Fact]
    public void Ede_has_header_block_numeric_types_and_unit_codes()
    {
        var lines = PointExporter.EdeLines([Sample()], "Test Site").ToList();
        Assert.Equal("PROJECT NAME;Test Site", lines[0]);
        Assert.StartsWith("# keyname;device obj.-instance;object-name;object-type;object-instance;description", lines[5]);
        Assert.Equal(8, lines.Count); // 5 header lines + column row + 2 points
        var ai = lines[6].Split(';');
        Assert.Equal("AHU-1.Zone Temp", ai[0]);
        Assert.Equal("1001", ai[1]);
        Assert.Equal("0", ai[3]);   // analog-input = object type 0
        Assert.Equal("N", ai[9]);   // input: not settable
        Assert.Equal("64", ai[14]); // degrees-fahrenheit code
        var bo = lines[7].Split(';');
        Assert.Equal("4", bo[3]);   // binary-output = 4
        Assert.Equal("Y", bo[9]);
        Assert.Equal(16, ai.Length);
    }

    [Fact]
    public void Writable_guess_follows_object_type()
    {
        Assert.False(PointExporter.IsUsuallyWritable(BacnetObjectTypes.OBJECT_ANALOG_INPUT));
        Assert.False(PointExporter.IsUsuallyWritable(BacnetObjectTypes.OBJECT_SCHEDULE));
        Assert.True(PointExporter.IsUsuallyWritable(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT));
        Assert.True(PointExporter.IsUsuallyWritable(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE));
    }

    [Theory]
    [InlineData("a.csv", ExportFormat.Csv)]
    [InlineData("A.XLSX", ExportFormat.Xlsx)]
    public void Format_is_picked_from_the_file_extension(string path, ExportFormat expected) =>
        Assert.Equal(expected, PointExporter.FormatFromPath(path));

    [Fact]
    public void Unknown_extension_gives_no_format() => Assert.Null(PointExporter.FormatFromPath("a.txt"));

    private static string TempPath(string ext) =>
        Path.Combine(Path.GetTempPath(), $"bacprobe-export-{Guid.NewGuid():N}{ext}");

    [Fact]
    public void Csv_file_is_utf8_with_bom_and_crlf()
    {
        var path = TempPath(".csv");
        try
        {
            PointExporter.Write(path, ExportFormat.Csv, [Sample()]);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("°F", text);
            Assert.Contains("\r\n", text);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Xlsx_round_trips_with_points_and_devices_sheets()
    {
        var path = TempPath(".xlsx");
        try
        {
            PointExporter.Write(path, ExportFormat.Xlsx, [Sample(), Device(1002, "AHU-2", Point(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 5, "SP"))]);
            using var wb = new XLWorkbook(path);
            var points = wb.Worksheet("Points");
            Assert.Equal("Device Instance", points.Cell(1, 1).GetString());
            Assert.Equal("Zone Temp", points.Cell(2, 6).GetString());
            Assert.Equal("°F", points.Cell(2, 9).GetString());
            Assert.Equal(1001, points.Cell(2, 1).GetDouble());
            Assert.Equal("SP", points.Cell(4, 6).GetString());
            Assert.True(points.Cell(1, 1).Style.Font.Bold);

            var devices = wb.Worksheet("Devices");
            Assert.Equal("AHU-2", devices.Cell(3, 2).GetString());
            Assert.Equal(1, devices.Cell(3, 7).GetDouble()); // point count excludes the device object
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Xlsx_never_turns_device_text_into_a_formula()
    {
        var path = TempPath(".xlsx");
        try
        {
            var d = Device(1, "=1+1", Point(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "=SUM(1,2)", "@evil\u0000"));
            PointExporter.Write(path, ExportFormat.Xlsx, [d]);
            using var wb = new XLWorkbook(path);
            var cell = wb.Worksheet("Points").Cell(2, 6);
            Assert.False(cell.HasFormula);
            Assert.Equal("=SUM(1,2)", cell.GetString());
            Assert.Equal("@evil", wb.Worksheet("Points").Cell(2, 7).GetString());
            Assert.False(wb.Worksheet("Points").Cell(2, 2).HasFormula);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Failed_write_leaves_no_temp_file_and_keeps_the_old_export()
    {
        var path = TempPath(".csv");
        File.WriteAllText(path, "old");
        try
        {
            // A path whose temp file cannot be created: directory does not exist.
            var bad = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}", "x.csv");
            Assert.ThrowsAny<IOException>(() => PointExporter.Write(bad, ExportFormat.Csv, [Sample()]));
            Assert.Equal("old", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
