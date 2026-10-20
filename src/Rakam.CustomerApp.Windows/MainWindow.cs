using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Rakam.CustomerApp.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rakam.CustomerApp.Windows;

public sealed class MainWindow : Form
{
    private const string Origin = "https://app.rakam.local";
    private readonly string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rakam", "CustomerApp");
    private readonly LocalStore store;
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SyncClient sync;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 60000 };
    private bool syncing;
    private string syncMessage = "اطلاعات روی این دستگاه ذخیره می‌شود";
    private readonly CancellationTokenSource shutdown = new();

    public MainWindow()
    {
        Text = "ثبت مراجعان | راکام"; Width = 1220; Height = 850; MinimumSize = new Size(850, 650);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(243, 246, 249);
        store = new LocalStore(Path.Combine(folder, "customers.db")); sync = new SyncClient(http, store);
        DailyBackup();
        Controls.Add(web);
        Load += async (_, _) => await Initialize();
        timer.Tick += async (_, _) => await Synchronize();
        FormClosing += (_, _) => { shutdown.Cancel(); timer.Stop(); http.Dispose(); };
    }
    private void DailyBackup()
    {
        try
        {
            var backupFolder = Path.Combine(folder, "backups");
            if (store.Setting("backup_day") == DateTime.Today.ToString("yyyy-MM-dd")) return;
            store.Backup(backupFolder);
            store.Set("backup_day", DateTime.Today.ToString("yyyy-MM-dd"));
            foreach (var old in new DirectoryInfo(backupFolder).GetFiles("*.db").OrderByDescending(x => x.LastWriteTimeUtc).Skip(14)) old.Delete();
        }
        catch (IOException) { syncMessage = "پشتیبان خودکار ساخته نشد؛ مسیر ذخیره را بررسی کنید."; }
    }
    private async Task Initialize()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(folder, "webview"));
            await web.EnsureCoreWebView2Async(environment);
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping("app.rakam.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(Origin + "/", StringComparison.Ordinal)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += Message;
            core.Navigate(Origin + "/index.html");
            timer.Start();
            await Synchronize();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("WebView2 Runtime روی ویندوز نصب نیست. فایل نصب آفلاین آن را از بخش دانلود مایکروسافت نصب کنید و برنامه را دوباره باز کنید.", "پیش‌نیاز راکام");
            Close();
        }
    }
    private object State() => new
    {
        metadata = store.Metadata, contacts = store.Contacts(), visits = store.Visits(),
        server = store.Setting("server") ?? "https://crm.safaeico.com",
        configured = File.Exists(Path.Combine(folder, "connection.bin")),
        last_sync = store.Setting("last_sync"), syncing, sync_message = syncMessage,
        data_folder = folder
    };
    private void Publish()
    {
        if (web.CoreWebView2 is not null && !IsDisposed)
            web.CoreWebView2.PostWebMessageAsJson(Wire.Encode(new { type = "state", data = State() }));
    }
    private string? Token()
    {
        var path = Path.Combine(folder, "connection.bin");
        if (!File.Exists(path)) return null;
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
    }
    private async Task Synchronize()
    {
        if (syncing || shutdown.IsCancellationRequested) return;
        syncing = true;
        try
        {
            var token = Token(); var server = store.Setting("server");
            if (token is null || server is null) { syncMessage = "ثبت آفلاین فعال است؛ اتصال CRM را در تنظیمات وارد کنید."; return; }
            syncMessage = "در حال همگام‌سازی…"; Publish();
            await sync.Run(server, token, shutdown.Token);
            syncMessage = store.Visits().Any(x => x.State == "error") ? "برخی ثبت‌ها نیاز به بررسی دارند" : "آخرین تغییرات همگام شد";
        }
        catch (SyncFailure e) { syncMessage = e.Message; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        { syncMessage = "ارتباط برقرار نیست؛ ثبت محلی ادامه دارد."; }
        catch (CryptographicException) { syncMessage = "کلید اتصال نیاز به ثبت مجدد دارد."; }
        catch (Exception) { syncMessage = "همگام‌سازی کامل نشد؛ اطلاعات محلی محفوظ است."; }
        finally { syncing = false; Publish(); }
    }
    private async void Message(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin + "/", StringComparison.Ordinal)) return;
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetString();
            var action = root.GetProperty("action").GetString();
            var payload = root.GetProperty("payload");
            object? result = null;
            switch (action)
            {
                case "init": result = State(); break;
                case "save":
                    var visit = Wire.Decode<Visit>(payload.GetRawText());
                    var followDate = payload.TryGetProperty("follow_up_date", out var dateValue) ? dateValue.GetString() : null;
                    visit = visit with { FollowUpAt = visit.NeedsFollowUp ? Validation.PersianDate(followDate) : null };
                    foreach (var definition in store.Metadata.Fields.Where(x => x.Type == "date"))
                    {
                        if (visit.Fields.TryGetValue(definition.Key, out var fieldDate) && !string.IsNullOrWhiteSpace(fieldDate.GetString()))
                            visit.Fields[definition.Key] = JsonSerializer.SerializeToElement(Validation.PersianDate(fieldDate.GetString())?.ToString("yyyy-MM-dd"));
                    }
                    store.Save(visit); result = State(); Publish();
                    _ = Synchronize(); break;
                case "pair":
                    if (syncing) throw new InvalidOperationException("پس از پایان همگام‌سازی دوباره تلاش کنید.");
                    var server = SyncClient.ServerUrl(payload.GetProperty("server").GetString() ?? "");
                    var token = payload.GetProperty("token").GetString()?.Trim() ?? "";
                    await sync.Pair(server, token, shutdown.Token);
                    var secretPath = Path.Combine(folder, "connection.bin");
                    File.WriteAllBytes(secretPath + ".tmp", ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
                    File.Move(secretPath + ".tmp", secretPath, true);
                    result = State(); _ = Synchronize(); break;
                case "sync": _ = Synchronize(); result = State(); break;
                case "export":
                    using (var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"rakam-visits-{DateTime.Today:yyyy-MM-dd}.csv" })
                    { if (dialog.ShowDialog(this) == DialogResult.OK) { store.ExportCsv(dialog.FileName); result = "خروجی ذخیره شد؛ وضعیت انتقال تغییری نکرد."; } }
                    break;
                case "backup":
                    using (var dialog = new FolderBrowserDialog { Description = "پوشه نگهداری پشتیبان را انتخاب کنید" })
                    { if (dialog.ShowDialog(this) == DialogResult.OK) { store.Backup(dialog.SelectedPath); result = "پشتیبان ذخیره شد."; } }
                    break;
                case "restore":
                    if (syncing) throw new InvalidOperationException("پس از پایان همگام‌سازی دوباره تلاش کنید.");
                    using (var dialog = new OpenFileDialog { Filter = "Rakam backup (*.db)|*.db" })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) break;
                        LocalStore.ValidateBackup(dialog.FileName);
                        var backupStore = new LocalStore(dialog.FileName);
                        var oldBinding = store.Setting("binding"); var incomingBinding = backupStore.Setting("binding");
                        if (oldBinding != incomingBinding && (oldBinding is not null || File.Exists(Path.Combine(folder, "connection.bin"))))
                            throw new InvalidDataException("این پشتیبان متعلق به اتصال فعلی نیست.");
                        if (MessageBox.Show("اطلاعات فعلی با پشتیبان جایگزین شود؟ ابتدا نسخه ایمنی خودکار ذخیره می‌شود.", "بازیابی پشتیبان", MessageBoxButtons.YesNo) != DialogResult.Yes) break;
                        store.Backup(Path.Combine(folder, "before-restore"));
                        LocalStore.Restore(dialog.FileName, store.Path); result = State(); Publish();
                    }
                    break;
                default: throw new InvalidOperationException("عملیات معتبر نیست.");
            }
            web.CoreWebView2.PostWebMessageAsJson(Wire.Encode(new { id, ok = true, result }));
        }
        catch (Exception ex)
        {
            var message = ex is InvalidDataException or InvalidOperationException or SyncFailure ? ex.Message :
                ex is HttpRequestException or TaskCanceledException ? "ارتباط با CRM برقرار نشد؛ اطلاعات محلی حفظ شده است." :
                "عملیات کامل نشد؛ فضای دیسک و دسترسی پوشه اطلاعات را بررسی کنید.";
            web.CoreWebView2.PostWebMessageAsJson(Wire.Encode(new { id, ok = false, error = message }));
        }
    }
}
