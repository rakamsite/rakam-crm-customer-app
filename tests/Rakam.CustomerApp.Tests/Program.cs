using Rakam.CustomerApp.Core;
using System.Net;
using System.Text;
using System.Text.Json;

var passed = 0;
var root = Path.Combine(Path.GetTempPath(), "rakam-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    passed++; Console.WriteLine("PASS " + name);
}
void Throws(Action action, string name)
{
    try { action(); throw new Exception("Expected exception: " + name); }
    catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { passed++; Console.WriteLine("PASS " + name); }
}
LocalStore Store(string name) => new(Path.Combine(root, name + ".db"));
try
{
    Check(Validation.Mobile("+۹۸ ۹۱۲ ۳۴۵ ۶۷۸۹") == "09123456789", "Persian and international mobile normalization");
    Throws(() => Validation.Mobile("0123"), "invalid mobile rejected");
    Check(Validation.PersianDate("۱۴۰۵/۰۷/۲۸")?.ToString("yyyy-MM-dd") == "2026-10-20", "Persian follow-up conversion");
    Throws(() => Validation.PersianDate("۱۴۰۵/۱۳/۰۱"), "invalid Persian date rejected");
    Throws(() => SyncClient.ServerUrl("http://crm.example.com"), "HTTP refused");
    Throws(() => SyncClient.ServerUrl("https://user:secret@crm.example.com/path"), "credential and path URL refused");
    var store = Store("main");
    var visit = new Visit { FullName = "مراجع تست", Mobile = "+989123456789", Notes = "=HYPERLINK(\"bad\")" };
    store.Save(visit);
    Check(new LocalStore(store.Path).Visits()[0].Visit.Mobile == "09123456789", "offline record survives restart");
    Throws(() => store.Save(visit with { Notes = "changed" }), "pending UUID immutable across uncertain delivery");
    Throws(() => store.Save(new Visit { NeedsFollowUp = true }), "anonymous follow-up rejected");
    var backup = store.Backup(Path.Combine(root, "backups"));
    LocalStore.ValidateBackup(backup);
    var restored = Store("restore");
    LocalStore.Restore(backup, restored.Path);
    Check(restored.Visits().Count == 1, "SQLite backup and restore");
    var csv = Path.Combine(root, "visits.csv"); store.ExportCsv(csv);
    var bytes = File.ReadAllBytes(csv);
    Check(bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "CSV UTF-8 BOM");
    Check(File.ReadAllText(csv).Contains("'=HYPERLINK"), "CSV formula neutralized");
    Check(store.Visits()[0].State == "queued", "export is not sync acknowledgment");
    var token = new string('a', 64);
    var attempts = 0;
    var seen = new HashSet<string>();
    var handler = new FakeHttp(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/metadata"))
            return Json(Metadata.Default with { User = new UserInfo(7, "کارشناس تست") });
        if (request.RequestUri.AbsolutePath.EndsWith("/contacts"))
            return Json(new ContactPage([new Contact(5, "مخاطب سرور", "09120000000")], null));
        var payload = Wire.Decode<Visit>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        seen.Add(payload.Uuid); attempts++;
        if (attempts == 1) throw new HttpRequestException("response lost after server commit");
        return Json(new Receipt(payload.Uuid, 42, 5, null));
    });
    var sync = new SyncClient(new HttpClient(handler), store);
    try { await sync.Run("https://crm.example.com", token); } catch (HttpRequestException) { }
    Check(store.Visits()[0].State == "queued", "lost HTTP response retains durable queue");
    await sync.Run("https://crm.example.com", token);
    Check(attempts == 2 && seen.Count == 1, "retry reuses same UUID");
    Check(store.Visits()[0].State == "synced" && store.Visits()[0].ServerId == 42, "only server receipt marks sync");
    Check(store.Contacts().Single().Id == 5, "CRM contacts downloaded");
    Throws(() => store.Save(visit), "synced record immutable");
    var failedStore = Store("page-failure"); failedStore.ReplaceContacts([new Contact(9, "old", "09129999999")]);
    var badPages = new SyncClient(new HttpClient(new FakeHttp(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/metadata")) return Json(Metadata.Default);
        if (request.RequestUri.Query.Contains("after=0")) return Json(new ContactPage([new Contact(10, "new", "09127777777")], 10));
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
    })), failedStore);
    try { await badPages.Run("https://crm.example.com", token); } catch (SyncFailure) { }
    Check(failedStore.Contacts().Single().Id == 9, "partial snapshot never replaces local cache");
    var rejected = Store("rejected"); rejected.Save(visit);
    var validationSync = new SyncClient(new HttpClient(new FakeHttp(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/metadata") ? Json(Metadata.Default) :
        request.RequestUri.AbsolutePath.EndsWith("/visits") ?
        new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("{\"errors\":{\"mobile\":[\"اصلاح شماره\"]}}") } :
        Json(new ContactPage([], null)))), rejected);
    await validationSync.Run("https://crm.example.com", token);
    Check(rejected.Visits()[0].State == "error" && rejected.Visits()[0].Error == "اصلاح شماره", "validation rejects retained for correction");
    rejected.Save(visit with { Subject = "اصلاح" });
    Check(rejected.Visits()[0].State == "queued", "rejected record can be corrected and requeued");
    var otherUser = new SyncClient(new HttpClient(new FakeHttp(_ => Json(Metadata.Default with { User = new(8, "other") }))), store);
    try { await otherUser.Pair("https://crm.example.com", token); throw new Exception("binding bypass"); }
    catch (SyncFailure) { Check(true, "cross-account binding refused"); }
    Console.WriteLine($"All {passed} checks passed.");
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(Wire.Encode(value), Encoding.UTF8, "application/json") };
sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(callback(request));
}
