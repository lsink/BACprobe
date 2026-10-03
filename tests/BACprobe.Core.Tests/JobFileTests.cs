using System.IO.BACnet;
using BACprobe.Core.Jobs;
using BACprobe.Core.Writing;
using Microsoft.Data.Sqlite;

namespace BACprobe.Core.Tests;

public sealed class JobFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bacprobe-job-{Guid.NewGuid():N}");

    public JobFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private static SavedDevice Device(uint instance, string name, params SavedObject[] objects) => new(
        instance, name, "192.168.1.5:47808", BacnetAddressTypes.IP, 0, [192, 168, 1, 5, 0xBA, 0xC0],
        999, "Acme", "M1", "1.0", 480, BacnetSegmentations.SEGMENTATION_NONE, PointsRead: objects.Length > 0, objects);

    private static SavedObject Obj(BacnetObjectTypes t, uint i, string? name, string? desc = null, string? pv = null,
        string? units = null, uint? code = null) => new(t, i, name, desc, pv, units, code);

    private static JobSnapshot Job(IReadOnlyList<SavedDevice> devices, IReadOnlyList<WriteLogEntry>? log = null) =>
        new(new JobInfo("City Hall", "Boiler room first", new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.FromHours(-5)),
            new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.FromHours(-5)), "10.1.1.1:47809", "192.168.1.0/24", "0.1.0"),
            devices, log ?? []);

    [Fact]
    public void Round_trip_keeps_everything()
    {
        var log = new[]
        {
            new WriteLogEntry(DateTimeOffset.Parse("2026-10-02T09:00:00-05:00"), 1001, "AHU-1", "Damper (Analog Output 1)",
                "write 25", 8, true, "device accepted the write"),
            new WriteLogEntry(DateTimeOffset.Parse("2026-10-02T09:05:00-05:00"), 1001, "AHU-1", "Damper (Analog Output 1)",
                "release", 8, false, "timeout"),
        };
        var devices = new[]
        {
            Device(1001, "AHU-1",
                Obj(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "Zone Temp", "Zone temperature", "72.4", "°F", 64),
                Obj(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1, "Fan", null, "Active")),
            Device(1002, "AHU-2"), // discovered but points never read
        };
        var path = P("site.bacprobe");
        JobFile.Save(path, Job(devices, log));
        var back = JobFile.Load(path);

        Assert.Equal("City Hall", back.Info.Name);
        Assert.Equal("Boiler room first", back.Info.Notes);
        Assert.Equal("10.1.1.1:47809", back.Info.BbmdText);
        Assert.Equal("192.168.1.0/24", back.Info.AdapterCidr);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.FromHours(-5)).UtcDateTime, back.Info.CreatedAt.UtcDateTime);

        Assert.Equal(2, back.Devices.Count);
        var d1 = back.Devices[0];
        Assert.Equal(1001u, d1.Instance);
        Assert.Equal("Acme", d1.VendorName);
        Assert.True(d1.PointsRead);
        Assert.Equal(2, d1.Objects.Count);
        Assert.Equal("72.4", d1.Objects[0].PresentValue);
        Assert.Equal(64u, d1.Objects[0].UnitsCode);
        Assert.Null(d1.Objects[1].Description);
        Assert.Null(d1.Objects[1].UnitsCode);
        Assert.False(back.Devices[1].PointsRead);
        Assert.Empty(back.Devices[1].Objects);

        Assert.Equal(2, back.WriteLog.Count);
        Assert.Equal("write 25", back.WriteLog[0].Action);
        Assert.True(back.WriteLog[0].Success);
        Assert.False(back.WriteLog[1].Success);
        Assert.Equal(8, back.WriteLog[1].Priority);
    }

    [Fact]
    public void Priority_slots_survive_a_round_trip_and_still_flag_the_override()
    {
        var slots = new[] { new BACprobe.Core.Browsing.PrioritySlot(8, "25"), new BACprobe.Core.Browsing.PrioritySlot(16, "50") };
        var path = P("slots.bacprobe");
        JobFile.Save(path, Job([Device(1, "A",
            Obj(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1, "Damper") with { Slots = slots },
            Obj(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "Temp"))]));

        var back = JobFile.Load(path).Devices[0];
        var damper = back.Objects.Single(o => o.Type == BacnetObjectTypes.OBJECT_ANALOG_OUTPUT);
        var temp = back.Objects.Single(o => o.Type == BacnetObjectTypes.OBJECT_ANALOG_INPUT);
        Assert.Equal([8, 16], damper.Slots!.Select(s => s.Priority));
        Assert.Equal("25", damper.Slots![0].ValueText);
        var summary = damper.ToSummary();
        Assert.True(summary.IsOverridden);
        Assert.Equal("P8 Manual Operator", summary.OverrideText);
        Assert.Empty(temp.Slots ?? []);
    }

    [Fact]
    public void Version_1_job_files_still_open()
    {
        // A file exactly as the first release wrote it: schema without priority_slots, user_version 1.
        var path = P("v1.bacprobe");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE devices (
                    instance INTEGER PRIMARY KEY, name TEXT NOT NULL, address_text TEXT NOT NULL,
                    address_type INTEGER NOT NULL, network INTEGER NOT NULL, address_bytes BLOB NOT NULL,
                    vendor_id INTEGER NOT NULL, vendor_name TEXT, model_name TEXT, firmware TEXT,
                    max_apdu INTEGER NOT NULL, segmentation INTEGER NOT NULL, points_read INTEGER NOT NULL);
                CREATE TABLE objects (
                    device_instance INTEGER NOT NULL, object_type INTEGER NOT NULL, object_instance INTEGER NOT NULL,
                    name TEXT, description TEXT, present_value TEXT, units TEXT, units_code INTEGER,
                    PRIMARY KEY (device_instance, object_type, object_instance));
                CREATE TABLE write_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, device_instance INTEGER NOT NULL,
                    device_name TEXT, point TEXT, action TEXT, priority INTEGER, success INTEGER NOT NULL, result TEXT);
                INSERT INTO meta VALUES ('app', 'BACprobe'), ('name', 'Old job');
                INSERT INTO devices VALUES (1001, 'AHU-1', '192.168.1.5:47808', 1, 0, x'C0A80105BAC0', 999, 'Acme', 'M1', '1.0', 480, 3, 1);
                INSERT INTO objects VALUES (1001, 1, 1, 'Damper', 'desc', '50', '%', 98);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        var job = JobFile.Load(path);
        Assert.Equal("Old job", job.Info.Name);
        var obj = Assert.Single(job.Devices[0].Objects);
        Assert.Equal("Damper", obj.Name);
        Assert.Empty(obj.Slots ?? []);
    }

    [Fact]
    public void Damaged_slot_data_means_no_slots_not_a_crash()
    {
        var path = P("badjson.bacprobe");
        JobFile.Save(path, Job([Device(1, "A", Obj(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1, "Damper"))]));
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE objects SET priority_slots = '{not json at all'";
            cmd.ExecuteNonQuery();
        }
        Assert.Empty(JobFile.Load(path).Devices[0].Objects[0].Slots ?? []);

        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE objects SET priority_slots = '[{\"P\":99,\"V\":\"x\"},{\"P\":8,\"V\":\"ok\"}]'"; // 99 is not a priority
            cmd.ExecuteNonQuery();
        }
        var slots = JobFile.Load(path).Devices[0].Objects[0].Slots!;
        Assert.Equal([8], slots.Select(s => s.Priority));
    }

    [Fact]
    public void Saved_address_rebuilds_into_a_usable_device()
    {
        var path = P("addr.bacprobe");
        JobFile.Save(path, Job([Device(1001, "AHU-1", Obj(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "T"))]));
        var live = JobFile.Load(path).Devices[0].ToDiscovered();
        Assert.Equal(1001u, live.InstanceId);
        Assert.Equal(BacnetAddressTypes.IP, live.Address.type);
        Assert.Equal<byte>([192, 168, 1, 5, 0xBA, 0xC0], live.Address.adr);
        Assert.Equal("AHU-1", live.ObjectName);
    }

    [Fact]
    public void Saved_device_converts_for_offline_export()
    {
        var path = P("exp.bacprobe");
        JobFile.Save(path, Job([Device(1001, "AHU-1", Obj(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "T", pv: "70", units: "°F", code: 64))]));
        var export = JobFile.Load(path).Devices[0].ToExportDevice();
        var point = Assert.Single(export.Objects);
        Assert.Equal("T", point.Name);
        Assert.Equal(64u, point.UnitsCode);
    }

    [Fact]
    public void Hostile_text_is_stored_and_returned_verbatim()
    {
        const string evil = "x'); DROP TABLE devices;--";
        var path = P("evil.bacprobe");
        JobFile.Save(path, Job([Device(1, evil, Obj(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "=cmd|' /C calc'!A0", "日本語 ünï \"quoted\" \n line2"))]));
        var back = JobFile.Load(path);
        Assert.Equal(evil, back.Devices[0].Name);
        Assert.Equal("=cmd|' /C calc'!A0", back.Devices[0].Objects[0].Name);
        Assert.Equal("日本語 ünï \"quoted\" \n line2", back.Devices[0].Objects[0].Description);
    }

    [Fact]
    public void Saving_again_replaces_the_snapshot_and_leaves_no_temp_file()
    {
        var path = P("again.bacprobe");
        JobFile.Save(path, Job([Device(1, "A"), Device(2, "B")]));
        JobFile.Save(path, Job([Device(3, "C")]));
        Assert.Equal([3u], JobFile.Load(path).Devices.Select(d => d.Instance));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Failed_save_throws_a_readable_error_and_leaves_no_temp_file()
    {
        var path = Path.Combine(_dir, "no-such-folder", "x.bacprobe");
        var ex = Assert.Throws<JobFileException>(() => JobFile.Save(path, Job([])));
        Assert.Contains("Next step", ex.Message);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Missing_file_is_a_readable_error()
    {
        var ex = Assert.Throws<JobFileException>(() => JobFile.Load(P("nope.bacprobe")));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_rejected()
    {
        var path = P("junk.bacprobe");
        File.WriteAllText(path, "this is not sqlite at all, just some text that is long enough to look like a file");
        var ex = Assert.Throws<JobFileException>(() => JobFile.Load(path));
        Assert.Contains("Next step", ex.Message);
    }

    [Fact]
    public void Some_other_sqlite_database_is_rejected_as_not_a_job_file()
    {
        var path = P("other.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE meta (key TEXT, value TEXT); INSERT INTO meta VALUES ('app', 'SomethingElse')";
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<JobFileException>(() => JobFile.Load(path));
        Assert.Contains("not a BACprobe job file", ex.Message);
    }

    [Fact]
    public void State_names_survive_a_save_and_load()
    {
        var path = P("states.bacprobe");
        JobFile.Save(path, Job([Device(1001, "AHU-1",
            new SavedObject(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, 1, "Occupancy Mode", null, "3", null, null, null,
                ["Occupied", "Unoccupied", "Standby"]),
            Obj(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "Zone Temp", pv: "72"))]));
        var objects = JobFile.Load(path).Devices[0].Objects;
        Assert.Equal(["Occupied", "Unoccupied", "Standby"], objects[1].StateNames!); // ordered by type: AI 1 first
        Assert.Null(objects[0].StateNames);
        Assert.Equal("Standby (3)", objects[1].ToSummary().ValueText);
    }

    [Fact]
    public void Point_status_survives_a_save_and_load()
    {
        var path = P("status.bacprobe");
        JobFile.Save(path, Job([Device(1001, "AHU-1",
            new SavedObject(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2, "Discharge Temp", null, "-40", null, null,
                StatusFlags: BacnetStatusFlags.STATUS_FLAG_FAULT, Reliability: (uint)BacnetReliability.RELIABILITY_OPEN_LOOP))]));
        var back = JobFile.Load(path).Devices[0].Objects[0].ToSummary();
        Assert.Equal("Fault: open loop", back.ProblemText);
    }

    [Fact]
    public void Job_from_a_newer_version_asks_for_an_update()
    {
        var path = P("future.bacprobe");
        JobFile.Save(path, Job([]));
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99";
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<JobFileException>(() => JobFile.Load(path));
        Assert.Contains("newer BACprobe", ex.Message);
        Assert.Contains("update", ex.Message);
    }

    [Fact]
    public void Loading_does_not_modify_the_file()
    {
        var path = P("ro.bacprobe");
        JobFile.Save(path, Job([Device(1, "A")]));
        var before = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        JobFile.Load(path);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void File_is_not_left_locked_after_load()
    {
        var path = P("lock.bacprobe");
        JobFile.Save(path, Job([Device(1, "A")]));
        JobFile.Load(path);
        File.Delete(path); // throws if a pooled connection still holds the file
        Assert.False(File.Exists(path));
    }
}
