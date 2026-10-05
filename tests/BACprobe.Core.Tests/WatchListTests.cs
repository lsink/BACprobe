using System.IO.BACnet;
using BACprobe.Core.Jobs;
using BACprobe.Core.Live;
using Microsoft.Data.Sqlite;

namespace BACprobe.Core.Tests;

public sealed class WatchListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bacprobe-watch-{Guid.NewGuid():N}");
    public WatchListTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly BacnetObjectId Ai1 = new(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);
    private static readonly BacnetObjectId Bo1 = new(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1);

    [Fact]
    public void A_point_goes_on_the_list_once_and_keeps_its_order()
    {
        var w = new WatchList();
        Assert.True(w.Add(1001, Ai1, "Zone Temp"));
        Assert.True(w.Add(1002, Bo1, "Fan Command"));
        Assert.False(w.Add(1001, Ai1, "Zone Temp again"));
        Assert.True(w.Add(1002, Ai1, "Zone Temp"));   // same point number on another device is a different point
        Assert.Equal(["Zone Temp", "Fan Command", "Zone Temp"], w.Items.Select(e => e.Label));
        Assert.True(w.Contains(1001, Ai1));
        Assert.False(w.Contains(1003, Ai1));
    }

    [Fact]
    public void Removing_and_clearing_work()
    {
        var w = new WatchList();
        w.Add(1, Ai1, "a");
        w.Add(2, Ai1, "b");
        Assert.True(w.Remove(1, Ai1));
        Assert.False(w.Remove(1, Ai1));
        Assert.Equal(1, w.Count);
        w.Clear();
        Assert.Equal(0, w.Count);
    }

    [Fact]
    public void The_list_stops_at_its_cap()
    {
        var w = new WatchList();
        for (uint i = 0; i < WatchList.MaxEntries + 20; i++) w.Add(1, new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, i), "x");
        Assert.Equal(WatchList.MaxEntries, w.Count);
        Assert.False(w.Add(2, Ai1, "one too many"));
    }

    [Fact]
    public void Grouping_by_device_keeps_first_seen_order()
    {
        var w = new WatchList();
        w.Add(1002, Ai1, "a");
        w.Add(1001, Ai1, "b");
        w.Add(1002, Bo1, "c");
        var groups = w.ByDevice();
        Assert.Equal([1002u, 1001u], groups.Select(g => g.Key));
        Assert.Equal(2, groups[0].Count());
    }

    [Fact]
    public void The_watch_list_survives_a_job_file_round_trip_in_order()
    {
        var w = new WatchList();
        w.Add(1002, Bo1, "Fan Command 'quoted'");
        w.Add(1001, Ai1, "Zone Temp");
        var path = Path.Combine(_dir, "w.bacprobe");
        JobFile.Save(path, new JobSnapshot(new JobInfo("Site", "", DateTimeOffset.Now, DateTimeOffset.Now), [], [], null, w.Items));

        var back = new WatchList();
        back.Load(JobFile.Load(path).AllWatch);
        Assert.Equal(w.Items, back.Items);
    }

    [Fact]
    public void A_file_from_before_watch_lists_loads_with_an_empty_list()
    {
        var path = Path.Combine(_dir, "old.bacprobe");
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE devices (instance INTEGER PRIMARY KEY, name TEXT NOT NULL, address_text TEXT NOT NULL, address_type INTEGER NOT NULL,
                    network INTEGER NOT NULL, address_bytes BLOB NOT NULL, vendor_id INTEGER NOT NULL, vendor_name TEXT, model_name TEXT, firmware TEXT,
                    max_apdu INTEGER NOT NULL, segmentation INTEGER NOT NULL, points_read INTEGER NOT NULL);
                CREATE TABLE objects (device_instance INTEGER NOT NULL, object_type INTEGER NOT NULL, object_instance INTEGER NOT NULL,
                    name TEXT, description TEXT, present_value TEXT, units TEXT, units_code INTEGER, priority_slots TEXT, state_texts TEXT,
                    status_flags INTEGER, reliability INTEGER, PRIMARY KEY (device_instance, object_type, object_instance));
                CREATE TABLE write_log (id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, device_instance INTEGER NOT NULL,
                    device_name TEXT, point TEXT, action TEXT, priority INTEGER, success INTEGER NOT NULL, result TEXT);
                CREATE TABLE notes (device_instance INTEGER NOT NULL, object_type INTEGER NOT NULL, object_instance INTEGER NOT NULL, text TEXT NOT NULL,
                    PRIMARY KEY (device_instance, object_type, object_instance));
                INSERT INTO meta VALUES ('app', 'BACprobe'), ('name', 'Schema five job');
                PRAGMA user_version = 5;
                """;
            cmd.ExecuteNonQuery();
        }
        var job = JobFile.Load(path);
        Assert.Equal("Schema five job", job.Info.Name);
        Assert.Empty(job.AllWatch);
    }
}
