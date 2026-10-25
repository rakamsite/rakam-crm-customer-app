using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace Rakam.CustomerApp.Core;

public sealed class LocalStore
{
    public string Path { get; }
    public LocalStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var db = Open();
        Execute(db, """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS contacts (id INTEGER PRIMARY KEY, full_name TEXT NOT NULL, mobile TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS contacts_mobile ON contacts(mobile);
            CREATE TABLE IF NOT EXISTS visits (
                uuid TEXT PRIMARY KEY, payload TEXT NOT NULL, state TEXT NOT NULL,
                error TEXT NOT NULL DEFAULT '', server_id INTEGER, revision INTEGER NOT NULL DEFAULT 1);
            """);
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, DefaultTimeout = 10 }.ToString());
        db.Open();
        return db;
    }
    private static void Execute(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public string? Setting(string key)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }
    public void Set(string key, string value)
    {
        using var db = Open();
        Execute(db, "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value",
            ("$key", key), ("$value", value));
    }
    public Metadata Metadata => Setting("metadata") is { } json ? Wire.Decode<Metadata>(json) : Metadata.Default;
    public void Save(Visit visit)
    {
        visit = Validation.Normalize(visit, Metadata);
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO visits(uuid,payload,state) VALUES($id,$payload,'queued')
            ON CONFLICT(uuid) DO UPDATE SET payload=$payload,state='queued',error='',revision=revision+1
            WHERE state='error' AND server_id IS NULL
            """;
        cmd.Parameters.AddWithValue("$id", visit.Uuid); cmd.Parameters.AddWithValue("$payload", Wire.Encode(visit));
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("مراجعه ارسال‌شده قابل ویرایش نیست؛ مراجعه جدید ثبت کنید.");
        transaction.Commit();
    }
    public List<VisitRow> Visits()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload,state,error,server_id FROM visits ORDER BY rowid DESC";
        using var rows = cmd.ExecuteReader(); var list = new List<VisitRow>();
        while (rows.Read()) list.Add(new(Wire.Decode<Visit>(rows.GetString(0)), rows.GetString(1), rows.GetString(2),
            rows.IsDBNull(3) ? null : rows.GetInt64(3)));
        return list;
    }
    public void MarkSynced(string uuid, string sentPayload, long id)
    {
        using var db = Open();
        // If the user edited a pending record during HTTP, never silently discard that edit.
        Execute(db, """
            UPDATE visits SET state=CASE WHEN payload=$sent THEN 'synced' ELSE 'error' END,
            error=CASE WHEN payload=$sent THEN '' ELSE 'نسخه قبلی ارسال شد؛ تغییرات جدید را به صورت مراجعه جدید ثبت کنید.' END,
            server_id=$server WHERE uuid=$uuid
            """, ("$sent", sentPayload), ("$server", id), ("$uuid", uuid));
    }
    public void MarkError(string uuid, string sentPayload, string message)
    {
        using var db = Open();
        Execute(db, "UPDATE visits SET state='error',error=$error WHERE uuid=$id AND payload=$sent",
            ("$error", message), ("$id", uuid), ("$sent", sentPayload));
    }
    public List<Contact> Contacts()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,full_name,mobile FROM contacts ORDER BY full_name";
        using var rows = cmd.ExecuteReader(); var list = new List<Contact>();
        while (rows.Read()) list.Add(new(rows.GetInt64(0), rows.GetString(1), rows.GetString(2)));
        return list;
    }
    public void ReplaceContacts(IEnumerable<Contact> contacts)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM contacts"; cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO contacts VALUES($id,$name,$mobile)";
        foreach (var c in contacts)
        {
            cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", c.Id);
            cmd.Parameters.AddWithValue("$name", c.FullName); cmd.Parameters.AddWithValue("$mobile", c.Mobile);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public string Backup(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"rakam-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.db");
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        target.Open(); source.BackupDatabase(target); return path;
    }
    public static void ValidateBackup(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        if ((string?)cmd.ExecuteScalar() != "ok") throw new InvalidDataException("پشتیبان سالم نیست.");
        cmd.CommandText = "SELECT payload,state FROM visits";
        using var rows = cmd.ExecuteReader();
        while (rows.Read()) _ = Wire.Decode<Visit>(rows.GetString(0));
    }
    public static void Restore(string backup, string target)
    {
        ValidateBackup(backup);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target }.ToString());
        source.Open(); destination.Open(); source.BackupDatabase(destination);
    }
    public void ExportCsv(string path)
    {
        var metadata = Metadata;
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("شناسه مراجعه,نام و نام خانوادگی,موبایل,منبع آشنایی,روش مراجعه,موضوع مراجعه,درخواست,توضیحات,زمان مراجعه,وضعیت انتقال");
        foreach (var row in Visits())
        {
            var v = row.Visit;
            string[] values = [v.Uuid, v.FullName, v.Mobile ?? "", metadata.Sources.GetValueOrDefault(v.AcquisitionSource, v.AcquisitionSource),
                metadata.Channels.GetValueOrDefault(v.EntryChannel, v.EntryChannel), metadata.Purposes.GetValueOrDefault(v.Purpose, v.Purpose),
                v.Subject, v.Notes, v.OccurredAt.ToOffset(TimeSpan.FromMinutes(210)).ToString("O"), row.State];
            writer.WriteLine(string.Join(",", values.Select(CsvCell)));
        }
        Set("last_export", DateTimeOffset.UtcNow.ToString("O"));
    }
    public static string CsvCell(string value)
    {
        // A customer's text must not become a formula when opened in Excel.
        if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

