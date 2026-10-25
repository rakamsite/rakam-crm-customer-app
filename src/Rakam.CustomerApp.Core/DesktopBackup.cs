using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Rakam.CustomerApp.Core;

public static class DesktopBackup
{
    public static string Save(LocalStore core, OfficeStore office, string directory)
    {
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rakam-backup-" + Guid.NewGuid().ToString("N"));
        var destination = System.IO.Path.Combine(directory, "rakam-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
        var incomplete = destination + ".tmp";
        try
        {
            Directory.CreateDirectory(temporary);
            System.IO.File.Copy(core.Backup(temporary), System.IO.Path.Combine(temporary, "customers.db"));
            foreach (var extra in Directory.GetFiles(temporary, "rakam-*.db")) System.IO.File.Delete(extra);
            office.Backup(temporary);
            ZipFile.CreateFromDirectory(temporary, incomplete); System.IO.File.Move(incomplete, destination); return destination;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); if (System.IO.File.Exists(incomplete)) System.IO.File.Delete(incomplete); }
    }
    public static void Restore(string backup, LocalStore core, OfficeStore office)
    {
        if (!backup.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            LocalStore.ValidateBackup(backup);
            using var incoming = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            incoming.Open(); ValidateBinding(incoming, core.Setting("binding"));
            LocalStore.Restore(backup, core.Path); return;
        }
        var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rakam-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            using var zip = ZipFile.OpenRead(backup);
            foreach (var name in new[] { "customers.db", "office.db" })
            {
                var entry = zip.GetEntry(name) ?? throw new InvalidDataException("پشتیبان کامل نیست.");
                if (entry.Length > 1073741824) throw new InvalidDataException("حجم پشتیبان معتبر نیست.");
                entry.ExtractToFile(System.IO.Path.Combine(temporary, name));
            }
            var customerFile = System.IO.Path.Combine(temporary, "customers.db"); var officeFile = System.IO.Path.Combine(temporary, "office.db");
            LocalStore.ValidateBackup(customerFile); OfficeStore.Validate(officeFile);
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = customerFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            { source.Open(); ValidateBinding(source, core.Setting("binding")); }
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = officeFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                source.Open(); using var cmd = source.CreateCommand(); cmd.CommandText = "SELECT value FROM office_settings WHERE key='binding'";
                var binding = cmd.ExecuteScalar() as string;
                if (binding is not null && core.Setting("binding") is { } current && binding != current)
                    throw new InvalidDataException("پشتیبان اتوماسیون متعلق به حساب دیگری است.");
            }
            // The caller creates a safety bundle first. Restore both databases, reverting both if either restore fails.
            var safety = System.IO.Path.Combine(temporary, "safety");
            var oldCore = core.Backup(safety); var oldOffice = office.Backup(safety);
            try { LocalStore.Restore(customerFile, core.Path); Copy(officeFile, office.Path); }
            catch { LocalStore.Restore(oldCore, core.Path); Copy(oldOffice, office.Path); throw; }
        }
        finally { Directory.Delete(temporary, true); }
    }
    private static void ValidateBinding(SqliteConnection source, string? expected)
    {
        using var cmd = source.CreateCommand(); cmd.CommandText = "SELECT value FROM settings WHERE key='binding'";
        var binding = cmd.ExecuteScalar() as string;
        if (expected is not null && expected != binding) throw new InvalidDataException("این پشتیبان متعلق به اتصال فعلی نیست.");
    }
    private static void Copy(string sourcePath, string targetPath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = targetPath, Pooling = false }.ToString());
        source.Open(); target.Open(); source.BackupDatabase(target);
    }
}
