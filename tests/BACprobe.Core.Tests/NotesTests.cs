using System.IO.BACnet;
using BACprobe.Core.Jobs;
using Microsoft.Data.Sqlite;

namespace BACprobe.Core.Tests;

public sealed class NotesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bacprobe-notes-{Guid.NewGuid():N}");
    public NotesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly BacnetObjectId Bv7 = new(BacnetObjectTypes.OBJECT_BINARY_VALUE, 7);
    private static readonly BacnetObjectId Ai1 = new(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);

    [Fact]
    public void A_note_is_per_device_and_per_point()
    {
        var n = new SessionNotes();
        n.Set(1001, null, "Belt slipping");
        n.Set(1001, Bv7, "Wired backwards, left overridden on purpose");
        n.Set(1002, Bv7, "different device, same point");
        Assert.Equal("Belt slipping", n.Get(1001));
        Assert.Equal("Wired backwards, left overridden on purpose", n.Get(1001, Bv7));
        Assert.Equal("", n.Get(1001, Ai1));
        Assert.Equal(2, n.CountFor(1001));
        Assert.Equal(3, n.Count);
    }

    [Fact]
    public void Empty_text_deletes_the_note_and_text_is_trimmed()
    {
        var n = new SessionNotes();
        n.Set(1, Ai1, "  keep me  ");
        Assert.Equal("keep me", n.Get(1, Ai1));
        n.Set(1, Ai1, "   ");
        Assert.False(n.Has(1, Ai1));
        Assert.Equal(0, n.Count);
    }

    [Fact]
    public void A_very_long_note_is_cut()
    {
        var n = new SessionNotes();
        n.Set(1, null, new string('x', SessionNotes.MaxLength + 500));
        Assert.Equal(SessionNotes.MaxLength, n.Get(1).Length);
    }

    [Fact]
    public void Notes_survive_a_job_file_round_trip()
    {
        var notes = new SessionNotes();
        notes.Set(1001, null, "AHU-1: told the site contact about the belt");
        notes.Set(1001, Bv7, "line one\nline two with 'quotes' and \"double\" and ; drop table");
        var path = Path.Combine(_dir, "n.bacprobe");
        JobFile.Save(path, new JobSnapshot(new JobInfo("Site", "", DateTimeOffset.Now, DateTimeOffset.Now), [], [], notes.All));

        var back = new SessionNotes();
        back.Load(JobFile.Load(path).AllNotes);
        Assert.Equal(notes.Get(1001), back.Get(1001));
        Assert.Equal("line one\nline two with 'quotes' and \"double\" and ; drop table", back.Get(1001, Bv7));
        Assert.Equal(2, back.Count);
    }

    [Fact]
    public void A_file_from_before_notes_loads_with_none()
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
                INSERT INTO meta VALUES ('app', 'BACprobe'), ('name', 'Schema four job');
                PRAGMA user_version = 4;
                """;
            cmd.ExecuteNonQuery();
        }
        var job = JobFile.Load(path);
        Assert.Equal("Schema four job", job.Info.Name);
        Assert.Empty(job.AllNotes);
    }

    [Fact]
    public void Loading_replaces_what_was_there()
    {
        var n = new SessionNotes();
        n.Set(9, null, "old");
        n.Load([new NoteEntry(1, null, "new")]);
        Assert.Equal("", n.Get(9));
        Assert.Equal("new", n.Get(1));
    }
}
