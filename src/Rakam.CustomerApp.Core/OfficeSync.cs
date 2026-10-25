using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rakam.CustomerApp.Core;

public sealed class OfficeSync(HttpClient http, OfficeStore office, SyncClient connection)
{
    private async Task<string> Upload(string server, string token, string id, CancellationToken cancel)
    {
        var file = office.File(id);
        if (file.Token is not null) return file.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, server + "/api/customer-app/v1/office/uploads");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(file.Key), "upload_key");
        var content = new ByteArrayContent(file.Bytes); content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", file.Name); request.Content = form;
        using var response = await http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            var permanent = response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge;
            throw new SyncFailure(permanent ? "پیوست پذیرفته نشد؛ حجم و نوع فایل را بررسی و آن را جایگزین کنید." : "بارگذاری کامل نشد؛ فایل روی دستگاه محفوظ است و دوباره تلاش می‌شود.", permanent, (int)response.StatusCode);
        }
        var result = Wire.Decode<JsonElement>(await response.Content.ReadAsStringAsync(cancel));
        var uploaded = result.GetProperty("token").GetString() ?? throw new SyncFailure("رسید فایل معتبر نیست.");
        if (!Guid.TryParse(uploaded, out _)) throw new SyncFailure("رسید فایل معتبر نیست.");
        office.Uploaded(id, file.Key, uploaded); return uploaded;
    }
    public async Task Run(string server, string token, Metadata meta, CancellationToken cancel = default)
    {
        office.Bind(server + "|" + meta.User.Id);
        if (!meta.Office) { office.ClearCache(); return; }
        foreach (var row in office.Rows().Where(x => x.State == "queued"))
        {
            try
            {
                var data = JsonNode.Parse(row.Command.Data.GetRawText())!.AsObject();
                var tokens = new JsonArray();
                foreach (var file in row.Command.Files) tokens.Add(await Upload(server, token, file, cancel));
                if (row.Command.Kind is "topic.create" or "topic.act") data["upload_tokens"] = tokens;
                var receipt = await connection.Request<OfficeReceipt>(server, token, "office/commands", new {
                    uuid = row.Command.Uuid, kind = row.Command.Kind, target_id = row.Command.TargetId,
                    version = row.Command.Version, data
                }, cancel);
                if (receipt.Uuid != row.Command.Uuid) throw new SyncFailure("رسید همگام‌سازی معتبر نیست.");
                office.Complete(row.Command.Uuid); office.ReleaseFiles(row.Command.Files);
            }
            catch (SyncFailure failure) when (failure.Permanent)
            {
                if (row.Command.Kind.EndsWith(".read", StringComparison.Ordinal)) office.Complete(row.Command.Uuid);
                else office.Error(row.Command.Uuid, failure.Message);
            }
        }
        var sections = new Dictionary<string, List<JsonElement>>();
        foreach (var section in new[] { "settings", "topics", "entries", "reports", "announcements" })
        {
            var items = new List<JsonElement>(); long after = 0;
            do
            {
                var page = await connection.Request<OfficePage>(server, token, $"office/snapshot?section={section}&after={after}", null, cancel);
                items.AddRange(page.Items);
                if (page.Next is null) break;
                if (page.Next <= after || items.Count > 100000) throw new SyncFailure("دریافت اتوماسیون کامل نشد؛ نسخه قبلی محفوظ است.");
                after = page.Next.Value;
            } while (true);
            if (section == "settings")
            {
                var old = office.State().Sections["settings"].FirstOrDefault();
                var current = items.FirstOrDefault();
                if (old.ValueKind == JsonValueKind.Object && current.ValueKind == JsonValueKind.Object
                    && old.GetProperty("manager").GetBoolean() && !current.GetProperty("manager").GetBoolean()) office.ClearCache();
            }
            sections[section] = items;
        }
        office.Replace(sections);
    }
    public async Task Download(string server, string token, long id, string destination, CancellationToken cancel = default)
    {
        var bytes = office.CachedAttachment(id);
        if (bytes is null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, server + "/api/customer-app/v1/office/attachments/" + id);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("دریافت فایل ممکن نیست؛ اتصال و دسترسی را بررسی کنید.");
            if (response.Content.Headers.ContentLength > 52428800) throw new InvalidDataException("حجم فایل برای دریافت در برنامه بیش از حد مجاز است.");
            await using var input = await response.Content.ReadAsStreamAsync(cancel); using var output = new MemoryStream();
            var buffer = new byte[65536]; int read;
            while ((read = await input.ReadAsync(buffer, cancel)) > 0)
            {
                if (output.Length + read > 52428800) throw new InvalidDataException("حجم فایل بیش از حد مجاز است.");
                await output.WriteAsync(buffer.AsMemory(0,read),cancel);
            }
            bytes = output.ToArray(); office.CacheAttachment(id,bytes);
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await System.IO.File.WriteAllBytesAsync(temporary, bytes, cancel);
            System.IO.File.Move(temporary, destination, true);
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }
}
