using System.Globalization;
using System.IO.BACnet;
using System.Text.Json;
using BACprobe.Core.Browsing;
using BACprobe.Core.Writing;
using Microsoft.Data.Sqlite;

namespace BACprobe.Core.Jobs;

/// <summary>
/// Reads and writes BACprobe job files: one SQLite database per site visit. A save always writes a whole snapshot
/// to a temp file and swaps it in, so a crash or full disk never damages the previous good file.
/// </summary>
public static class JobFile
{
    public const int SchemaVersion = 4; // 2 added objects.priority_slots, 3 state_texts, 4 status_flags + reliability; older files still load
    public const string Extension = ".bacprobe";
    private const string AppMarker = "BACprobe";

    private const string Schema = """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE devices (
            instance INTEGER PRIMARY KEY, name TEXT NOT NULL, address_text TEXT NOT NULL,
            address_type INTEGER NOT NULL, network INTEGER NOT NULL, address_bytes BLOB NOT NULL,
            vendor_id INTEGER NOT NULL, vendor_name TEXT, model_name TEXT, firmware TEXT,
            max_apdu INTEGER NOT NULL, segmentation INTEGER NOT NULL, points_read INTEGER NOT NULL);
        CREATE TABLE objects (
            device_instance INTEGER NOT NULL REFERENCES devices(instance) ON DELETE CASCADE,
            object_type INTEGER NOT NULL, object_instance INTEGER NOT NULL,
            name TEXT, description TEXT, present_value TEXT, units TEXT, units_code INTEGER, priority_slots TEXT, state_texts TEXT,
            status_flags INTEGER, reliability INTEGER,
            PRIMARY KEY (device_instance, object_type, object_instance));
        CREATE TABLE write_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, device_instance INTEGER NOT NULL,
            device_name TEXT, point TEXT, action TEXT, priority INTEGER, success INTEGER NOT NULL, result TEXT);
        """;

    private static string ConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString(); // no pooling: the file must not stay locked

    public static void Save(string path, JobSnapshot job)
    {
        var temp = path + ".tmp";
        try
        {
            File.Delete(temp); // a leftover from an earlier crash
            using (var conn = new SqliteConnection(ConnectionString(temp, SqliteOpenMode.ReadWriteCreate)))
            {
                conn.Open();
                using var tx = conn.BeginTransaction();
                Exec(conn, tx, Schema);
                Exec(conn, tx, $"PRAGMA user_version = {SchemaVersion}");
                WriteMeta(conn, tx, job.Info);
                foreach (var d in job.Devices) WriteDevice(conn, tx, d);
                foreach (var e in job.WriteLog) WriteLogRow(conn, tx, e);
                tx.Commit();
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (SqliteException ex)
        {
            throw new JobFileException($"Could not save the job file: {ex.Message}\n" +
                                       "  Likely cause: the disk is full or the folder is read-only.\n" +
                                       "  Next step:    save to a different folder.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JobFileException($"Could not save the job file: {ex.Message}\n" +
                                       "  Likely cause: the file is open in another program, or the folder is read-only.\n" +
                                       "  Next step:    close whatever has it open, or save to a different folder.", ex);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    public static JobSnapshot Load(string path)
    {
        if (!File.Exists(path))
            throw new JobFileException($"{path} does not exist.\n  Next step: check the file name and folder.");

        try
        {
            using var conn = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly)); // loading never changes the file
            conn.Open();

            var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version"), CultureInfo.InvariantCulture);
            var meta = ReadMeta(conn);
            if (!meta.TryGetValue("app", out var app) || app != AppMarker)
                throw new JobFileException("That file is not a BACprobe job file.\n" +
                                           "  Likely cause: it is a different kind of database, or the wrong file.\n" +
                                           "  Next step:    pick a file saved by BACprobe (.bacprobe).");
            if (version > SchemaVersion)
                throw new JobFileException($"That job file was saved by a newer BACprobe (file format {version}, this version reads up to {SchemaVersion}).\n" +
                                           "  Next step: update BACprobe from https://github.com/lsink/BACprobe, then open it again.");

            var info = new JobInfo(
                meta.GetValueOrDefault("name", ""), meta.GetValueOrDefault("notes", ""),
                ParseTime(meta.GetValueOrDefault("created")), ParseTime(meta.GetValueOrDefault("saved")),
                meta.GetValueOrDefault("bbmd"), meta.GetValueOrDefault("adapter"), meta.GetValueOrDefault("app_version", ""));
            return new JobSnapshot(info, ReadDevices(conn, version), ReadLog(conn));
        }
        catch (SqliteException ex)
        {
            throw new JobFileException($"Could not read that file as a BACprobe job: {ex.Message}\n" +
                                       "  Likely cause: it is damaged, not a database, or open exclusively in another program.\n" +
                                       "  Next step:    try a backup copy of the job file.", ex);
        }
    }

    private static DateTimeOffset ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTimeOffset.MinValue;

    private static void WriteMeta(SqliteConnection c, SqliteTransaction tx, JobInfo i)
    {
        var rows = new Dictionary<string, string?>
        {
            ["app"] = AppMarker, ["name"] = i.Name, ["notes"] = i.Notes,
            ["created"] = i.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
            ["saved"] = i.SavedAt.ToString("o", CultureInfo.InvariantCulture),
            ["bbmd"] = i.BbmdText, ["adapter"] = i.AdapterCidr, ["app_version"] = i.AppVersion,
        };
        foreach (var (k, v) in rows)
        {
            if (v is null) continue;
            Exec(c, tx, "INSERT INTO meta(key, value) VALUES ($k, $v)", ("$k", k), ("$v", v));
        }
    }

    private static void WriteDevice(SqliteConnection c, SqliteTransaction tx, SavedDevice d)
    {
        Exec(c, tx, """
            INSERT INTO devices(instance, name, address_text, address_type, network, address_bytes, vendor_id,
                                vendor_name, model_name, firmware, max_apdu, segmentation, points_read)
            VALUES ($i, $n, $at, $aty, $net, $ab, $vid, $vn, $mn, $fw, $mx, $seg, $pr)
            """,
            ("$i", (long)d.Instance), ("$n", d.Name), ("$at", d.AddressText), ("$aty", (int)d.AddressType),
            ("$net", (int)d.Network), ("$ab", d.AddressBytes), ("$vid", (int)d.VendorId), ("$vn", d.VendorName),
            ("$mn", d.ModelName), ("$fw", d.Firmware), ("$mx", (long)d.MaxApdu), ("$seg", (int)d.Segmentation),
            ("$pr", d.PointsRead ? 1 : 0));

        if (d.Objects.Count == 0) return;

        // One prepared statement for every object: a big site has tens of thousands of rows.
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO objects(device_instance, object_type, object_instance, name, description, present_value, units, units_code, priority_slots, state_texts,
                                status_flags, reliability)
            VALUES ($d, $t, $i, $n, $de, $pv, $u, $uc, $ps, $st, $sf, $rel)
            """;
        string[] names = ["$d", "$t", "$i", "$n", "$de", "$pv", "$u", "$uc", "$ps", "$st", "$sf", "$rel"];
        var p = names.Select(n => cmd.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray(); // type follows each value
        cmd.Prepare();
        foreach (var o in d.Objects)
        {
            object?[] values = [(long)d.Instance, (int)o.Type, (long)o.Instance, o.Name, o.Description, o.PresentValue, o.Units,
                o.UnitsCode is { } uc ? (long)uc : null, SlotsToJson(o.Slots), NamesToJson(o.StateNames),
                o.StatusFlags is { } sf ? (long)sf : null, o.Reliability is { } rel ? (long)rel : null];
            for (var i = 0; i < p.Length; i++) p[i].Value = values[i] ?? DBNull.Value; // always parameters: names come off the network
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteLogRow(SqliteConnection c, SqliteTransaction tx, WriteLogEntry e) =>
        Exec(c, tx, """
            INSERT INTO write_log(time, device_instance, device_name, point, action, priority, success, result)
            VALUES ($t, $d, $dn, $p, $a, $pr, $s, $r)
            """,
            ("$t", e.Time.ToString("o", CultureInfo.InvariantCulture)), ("$d", (long)e.DeviceInstance), ("$dn", e.DeviceName),
            ("$p", e.Point), ("$a", e.Action), ("$pr", e.Priority), ("$s", e.Success ? 1 : 0), ("$r", e.Result));

    private static Dictionary<string, string> ReadMeta(SqliteConnection c)
    {
        var meta = new Dictionary<string, string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM meta";
        using var r = cmd.ExecuteReader();
        while (r.Read()) meta[r.GetString(0)] = r.GetString(1);
        return meta;
    }

    private static List<SavedDevice> ReadDevices(SqliteConnection c, int version)
    {
        var objects = new Dictionary<long, List<SavedObject>>();
        using (var cmd = c.CreateCommand())
        {
            // Version 1 files have no priority_slots column, versions 1 and 2 no state_texts, 1 to 3 no status.
            var slotsColumn = version >= 2 ? "priority_slots" : "NULL";
            var namesColumn = version >= 3 ? "state_texts" : "NULL";
            var statusColumns = version >= 4 ? "status_flags, reliability" : "NULL, NULL";
            cmd.CommandText = $"""
                SELECT device_instance, object_type, object_instance, name, description, present_value, units, units_code, {slotsColumn}, {namesColumn},
                       {statusColumns}
                FROM objects ORDER BY device_instance, object_type, object_instance
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var dev = r.GetInt64(0);
                if (!objects.TryGetValue(dev, out var list)) objects[dev] = list = [];
                list.Add(new SavedObject((BacnetObjectTypes)r.GetInt32(1), (uint)r.GetInt64(2), Str(r, 3), Str(r, 4), Str(r, 5),
                    Str(r, 6), r.IsDBNull(7) ? null : (uint)r.GetInt64(7), SlotsFromJson(Str(r, 8)), NamesFromJson(Str(r, 9)),
                    r.IsDBNull(10) ? null : (BacnetStatusFlags)(r.GetInt64(10) & 0xF), // the four standard flags only: the file may come from anywhere
                    r.IsDBNull(11) ? null : (uint)r.GetInt64(11)));
            }
        }

        var devices = new List<SavedDevice>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT instance, name, address_text, address_type, network, address_bytes, vendor_id, vendor_name,
                       model_name, firmware, max_apdu, segmentation, points_read
                FROM devices ORDER BY instance
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var instance = r.GetInt64(0);
                devices.Add(new SavedDevice((uint)instance, r.GetString(1), r.GetString(2), (BacnetAddressTypes)r.GetInt32(3),
                    (ushort)r.GetInt32(4), (byte[])r.GetValue(5), (ushort)r.GetInt32(6), Str(r, 7), Str(r, 8), Str(r, 9),
                    (uint)r.GetInt64(10), (BacnetSegmentations)r.GetInt32(11), r.GetInt32(12) != 0,
                    objects.GetValueOrDefault(instance) ?? []));
            }
        }
        return devices;
    }

    private static List<WriteLogEntry> ReadLog(SqliteConnection c)
    {
        var list = new List<WriteLogEntry>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time, device_instance, device_name, point, action, priority, success, result FROM write_log ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new WriteLogEntry(ParseTime(r.GetString(0)), (uint)r.GetInt64(1), Str(r, 2) ?? "", Str(r, 3) ?? "",
                Str(r, 4) ?? "", r.GetInt32(5), r.GetInt32(6) != 0, Str(r, 7) ?? ""));
        return list;
    }

    private sealed record SlotDto(int P, string V);

    private static string? SlotsToJson(IReadOnlyList<PrioritySlot>? slots) =>
        slots is null or { Count: 0 } ? null : JsonSerializer.Serialize(slots.Select(s => new SlotDto(s.Priority, s.ValueText)));

    /// <summary>The file may come from anywhere: bad JSON means "no slots", never a crash.</summary>
    private static List<PrioritySlot> SlotsFromJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SlotDto>>(json)?
                .Where(d => d is { P: >= 1 and <= 16, V: not null })
                .Select(d => new PrioritySlot(d.P, d.V)).ToList() ?? [];
        }
        catch (JsonException) { return []; }
    }

    private static string? NamesToJson(IReadOnlyList<string?>? names) =>
        names is null or { Count: 0 } ? null : JsonSerializer.Serialize(names);

    /// <summary>Like the slots: bad JSON in a file from anywhere means "no names", never a crash.</summary>
    private static List<string?>? NamesFromJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<List<string?>>(json) is { Count: > 0 } list ? list : null; }
        catch (JsonException) { return null; }
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value); // always parameters: names come off the network
        cmd.ExecuteNonQuery();
    }
}
