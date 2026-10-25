using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Rakam.CustomerApp.Core;

public record OfficeCommand(string Uuid, string Kind, long? TargetId, JsonElement? Version, JsonElement Data, List<string> Files);
public record OfficeRow(OfficeCommand Command, string State, string Error);
public record OfficeFile(string Id, string Name, long Size);
public record OfficeUpload(string Key, string? Token, string Name, byte[] Bytes);
public record OfficePage(List<JsonElement> Items, long? Next);
public record OfficeReceipt(string Uuid, long? RecordId);
public record OfficeState(Dictionary<string, List<JsonElement>> Sections, List<OfficeRow> Queue, string? LastSync);

public sealed class OfficeStore
{
    public string Path { get; }
    public OfficeStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var db = Open();
        Sql(db, """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS office_received_files(id INTEGER PRIMARY KEY, bytes BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS office_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS office_commands(id TEXT PRIMARY KEY,payload TEXT NOT NULL,state TEXT NOT NULL,error TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS office_cache(section TEXT NOT NULL,id INTEGER NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(section,id));
            CREATE TABLE IF NOT EXISTS office_files(id TEXT PRIMARY KEY,name TEXT NOT NULL,bytes BLOB NOT NULL,upload_key TEXT NOT NULL,token TEXT,key_at TEXT NOT NULL);
            """);
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, DefaultTimeout = 10, Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static void Sql(SqliteConnection db, string sql, params (string, object?)[] values)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public void Bind(string binding)
    {
        using var db = Open(); Sql(db, "BEGIN IMMEDIATE");
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM office_settings WHERE key='binding'";
        var old = cmd.ExecuteScalar() as string;
        if (old is not null && old != binding) throw new InvalidDataException("اطلاعات اتوماسیون متعلق به حساب یا شرکت دیگری است.");
        Sql(db, "INSERT OR IGNORE INTO office_settings VALUES('binding',$value)", ("$value", binding)); Sql(db, "COMMIT");
    }
    public OfficeFile AddFile(string path, long maxBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > maxBytes) throw new InvalidDataException("حجم فایل از سقف مجاز بیشتر است یا فایل خالی است.");
        if (!new[] { ".pdf", ".jpg", ".jpeg", ".png", ".webp", ".txt", ".docx", ".xlsx" }.Contains(file.Extension.ToLowerInvariant()))
            throw new InvalidDataException("تصویر، PDF، متن، Word یا Excel انتخاب کنید.");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(length(bytes)),0) FROM office_files";
        if (Convert.ToInt64(cmd.ExecuteScalar()) + file.Length > 268435456) throw new InvalidDataException("فضای فایل‌های ارسال‌نشده پر شده است؛ ابتدا همگام‌سازی کنید.");
        var id = Guid.NewGuid().ToString(); var bytes = File.ReadAllBytes(file.FullName);
        Sql(db, "INSERT INTO office_files VALUES($id,$name,$bytes,$key,NULL,$at)", ("$id", id), ("$name", file.Name), ("$bytes", bytes),
            ("$key", Guid.NewGuid().ToString()), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        return new(id, file.Name, bytes.LongLength);
    }
    public void RemoveFile(string id)
    {
        if (Rows().Any(x => x.Command.Files.Contains(id))) return;
        using var db = Open(); Sql(db, "DELETE FROM office_files WHERE id=$id", ("$id", id));
    }
    public OfficeUpload File(string id)
    {
        using var db = Open();
        Sql(db, "UPDATE office_files SET upload_key=$key,token=NULL,key_at=$now WHERE id=$id AND key_at<$old", ("$key", Guid.NewGuid().ToString()),
            ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", id), ("$old", DateTimeOffset.UtcNow.AddHours(-47).ToString("O")));
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT upload_key,token,name,bytes FROM office_files WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) throw new InvalidDataException("پیوست محلی پیدا نشد؛ فایل را دوباره انتخاب کنید.");
        return new(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1), row.GetString(2), (byte[])row[3]);
    }
    public void Uploaded(string id, string key, string token)
    {
        using var db = Open(); Sql(db, "UPDATE office_files SET token=$token WHERE id=$id AND upload_key=$key", ("$token", token), ("$id", id), ("$key", key));
    }
    public void Save(OfficeCommand command)
    {
        if (!Guid.TryParse(command.Uuid, out _) || !new[] { "topic.create", "topic.act", "topic.read", "report.save", "report.reply", "report.read", "notice.create", "notice.update", "notice.delete", "notice.read" }.Contains(command.Kind))
            throw new InvalidDataException("عملیات معتبر نیست.");
        if (command.Data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("اطلاعات فرم معتبر نیست.");
        foreach (var id in command.Files) _ = File(id);
        using var db = Open();
        // Even after an uncertain HTTP result, an existing UUID remains immutable.
        Sql(db, "INSERT OR IGNORE INTO office_commands VALUES($id,$payload,'queued','')", ("$id", command.Uuid), ("$payload", Wire.Encode(command)));
    }
    public List<OfficeRow> Rows()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload,state,error FROM office_commands ORDER BY rowid";
        using var rows = cmd.ExecuteReader(); var result = new List<OfficeRow>();
        while (rows.Read()) result.Add(new(Wire.Decode<OfficeCommand>(rows.GetString(0)), rows.GetString(1), rows.GetString(2)));
        return result;
    }
    public void Complete(string uuid)
    {
        using var db = Open(); Sql(db, "BEGIN IMMEDIATE");
        Sql(db, "UPDATE office_commands SET state='delivered',error='' WHERE id=$id", ("$id", uuid));
        // Files for other pending operations or form drafts remain available.
        Sql(db, "COMMIT");
    }
    public void ReleaseFiles(IEnumerable<string> ids)
    {
        foreach (var id in ids) RemoveFile(id);
    }
    public void Error(string uuid, string message)
    {
        using var db = Open(); Sql(db, "UPDATE office_commands SET state='error',error=$error WHERE id=$id", ("$error", message), ("$id", uuid));
    }
    public void Dismiss(string uuid)
    {
        var row = Rows().FirstOrDefault(x => x.Command.Uuid == uuid);
        if (row?.State != "error") throw new InvalidDataException("ارسال در انتظار را نمی‌توان حذف کرد؛ ممکن است سرور آن را ثبت کرده باشد.");
        using (var db = Open()) Sql(db, "DELETE FROM office_commands WHERE id=$id", ("$id", uuid));
        ReleaseFiles(row.Command.Files);
    }
    public OfficeState State()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT section,payload FROM office_cache ORDER BY id";
        using var rows = cmd.ExecuteReader(); var sections = new Dictionary<string, List<JsonElement>>();
        foreach (var section in new[] { "topics", "entries", "reports", "announcements", "settings" }) sections[section] = [];
        while (rows.Read()) sections[rows.GetString(0)].Add(Wire.Decode<JsonElement>(rows.GetString(1)));
        rows.Close(); cmd.CommandText = "SELECT value FROM office_settings WHERE key='last_sync'";
        return new(sections, Rows(), cmd.ExecuteScalar() as string);
    }
    public void Replace(Dictionary<string, List<JsonElement>> sections)
    {
        var deliveredFiles = Rows().Where(x => x.State == "delivered").SelectMany(x => x.Command.Files).ToList();
        using var db = Open(); Sql(db, "BEGIN IMMEDIATE");
        Sql(db, "DELETE FROM office_cache");
        foreach (var (section, items) in sections)
            foreach (var item in items) Sql(db, "INSERT INTO office_cache VALUES($s,$id,$p)", ("$s", section), ("$id", item.GetProperty("id").GetInt64()), ("$p", item.GetRawText()));
        Sql(db, "INSERT INTO office_settings VALUES('last_sync',$at) ON CONFLICT(key) DO UPDATE SET value=$at", ("$at", DateTimeOffset.UtcNow.ToString("O")));
        var accessible = sections.GetValueOrDefault("entries", []).SelectMany(x => x.GetProperty("attachments").EnumerateArray()).Select(x => x.GetProperty("id").GetInt64()).ToHashSet();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM office_received_files"; using var rows = cmd.ExecuteReader(); var removed = new List<long>();
            while (rows.Read()) if (!accessible.Contains(rows.GetInt64(0))) removed.Add(rows.GetInt64(0)); rows.Close();
            foreach (var id in removed) Sql(db,"DELETE FROM office_received_files WHERE id=$id",("$id",id));
        }
        Sql(db, "DELETE FROM office_commands WHERE state='delivered'"); Sql(db, "COMMIT");
        ReleaseFiles(deliveredFiles);
    }
    public void ClearCache() { using var db = Open(); Sql(db, "DELETE FROM office_cache; DELETE FROM office_received_files;"); }
    public byte[]? CachedAttachment(long id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT bytes FROM office_received_files WHERE id=$id"; cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteScalar() as byte[];
    }
    public void CacheAttachment(long id, byte[] bytes)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COALESCE(SUM(length(bytes)),0) FROM office_received_files";
        if (Convert.ToInt64(cmd.ExecuteScalar()) + bytes.LongLength > 268435456) return;
        Sql(db,"INSERT OR REPLACE INTO office_received_files VALUES($id,$bytes)",("$id",id),("$bytes",bytes));
    }
    public string Backup(string directory)
    {
        Directory.CreateDirectory(directory); var path = System.IO.Path.Combine(directory, "office.db");
        using var source = Open(); using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        target.Open(); source.BackupDatabase(target); return path;
    }
    public static void Validate(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check";
        if ((string?)cmd.ExecuteScalar() != "ok") throw new InvalidDataException("پشتیبان اتوماسیون سالم نیست.");
        cmd.CommandText = "SELECT payload FROM office_commands"; using var rows = cmd.ExecuteReader();
        while (rows.Read()) _ = Wire.Decode<OfficeCommand>(rows.GetString(0));
    }
}
