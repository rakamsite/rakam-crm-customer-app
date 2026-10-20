using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Rakam.CustomerApp.Core;

public sealed class SyncFailure(string message, bool permanent = false) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}

public sealed class SyncClient(HttpClient http, LocalStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static string ServerUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.AbsolutePath != "/" || uri.UserInfo != ""
            || uri.Query != "" || uri.Fragment != "")
            throw new InvalidDataException("آدرس ریشه CRM را با https وارد کنید؛ مثلاً https://crm.safaeico.com");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    private async Task<T> Request<T>(string server, string token, string path, object? body, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
            server + "/api/customer-app/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = new StringContent(Wire.Encode(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SyncFailure("کلید اتصال منقضی، لغو یا فاقد دسترسی است؛ اتصال را در تنظیمات بررسی کنید.");
            if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
            {
                var text = await response.Content.ReadAsStringAsync(cancel);
                string message = "اطلاعات نیاز به اصلاح دارد؛ در CRM بررسی کنید.";
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(text);
                    if (json.RootElement.TryGetProperty("errors", out var errors))
                        message = string.Join(" — ", errors.EnumerateObject().SelectMany(x => x.Value.EnumerateArray()).Select(x => x.GetString()));
                    else if (json.RootElement.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
                }
                catch (System.Text.Json.JsonException) { }
                throw new SyncFailure(message, true);
            }
            throw new SyncFailure($"CRM در دسترس نیست (کد {(int)response.StatusCode})؛ ثبت محلی ادامه دارد.");
        }
        return Wire.Decode<T>(await response.Content.ReadAsStringAsync(cancel));
    }
    public async Task<Metadata> Pair(string server, string token, CancellationToken cancel = default)
    {
        server = ServerUrl(server);
        if (token.Length != 64 || token.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new InvalidDataException("کلید اتصال معتبر نیست.");
        var meta = await Request<Metadata>(server, token, "metadata", null, cancel);
        if (meta.Protocol != 1) throw new SyncFailure("نسخه برنامه با CRM سازگار نیست.");
        var binding = server + "|" + meta.User.Id;
        var previous = store.Setting("binding");
        if (previous is not null && previous != binding)
            throw new SyncFailure("این فضای محلی به حساب دیگری متصل است؛ برای حساب دیگر از کاربر ویندوز جدا استفاده کنید.");
        store.Set("binding", binding); store.Set("server", server); store.Set("metadata", Wire.Encode(meta));
        return meta;
    }
    public async Task Run(string server, string token, CancellationToken cancel = default)
    {
        if (!await gate.WaitAsync(0, cancel)) return;
        try
        {
            await Pair(server, token, cancel);
            foreach (var row in store.Visits().Where(x => x.State == "queued"))
            {
                var sent = Wire.Encode(row.Visit);
                try
                {
                    var receipt = await Request<Receipt>(server, token, "visits", row.Visit, cancel);
                    if (receipt.Uuid != row.Visit.Uuid) throw new SyncFailure("پاسخ همگام‌سازی معتبر نیست.");
                    store.MarkSynced(row.Visit.Uuid, sent, receipt.VisitId);
                }
                catch (SyncFailure e) when (e.Permanent) { store.MarkError(row.Visit.Uuid, sent, e.Message); }
            }
            var contacts = new List<Contact>(); long after = 0;
            while (true)
            {
                var page = await Request<ContactPage>(server, token, $"contacts?after={after}", null, cancel);
                contacts.AddRange(page.Items);
                if (page.Next is null) break;
                if (page.Next <= after) throw new SyncFailure("صفحه‌بندی CRM معتبر نیست.");
                after = page.Next.Value;
                if (contacts.Count > 100000) throw new SyncFailure("حجم اطلاعات نیاز به بررسی دارد.");
            }
            store.ReplaceContacts(contacts);
            store.Set("last_sync", DateTimeOffset.UtcNow.ToString("O"));
        }
        finally { gate.Release(); }
    }
}
