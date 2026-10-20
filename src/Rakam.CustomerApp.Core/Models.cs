using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rakam.CustomerApp.Core;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException("اطلاعات فایل معتبر نیست.");
}

public record Contact(long Id, string FullName, string Mobile);
public record UserInfo(long Id, string Name);
public record FieldOption(string Value, string Label);
public record FieldDefinition(string Key, string Label, string Type, bool Required,
    List<FieldOption> Options, List<string> ContactTypes);
public record Metadata(int Protocol, UserInfo User, Dictionary<string, string> Sources,
    Dictionary<string, string> Channels, Dictionary<string, string> Purposes, List<FieldDefinition> Fields)
{
    public static Metadata Default => new(1, new(0, "ثبت آفلاین"),
        new() { ["instagram"] = "اینستاگرام", ["google"] = "گوگل", ["referral"] = "معرفی دیگران",
            ["passing"] = "تابلو و عبور", ["ikd_website"] = "سایت ایران‌خودرو دیزل", ["website"] = "سایت نمایندگی",
            ["other"] = "سایر", ["unknown"] = "نامشخص" },
        new() { ["in_person"] = "حضوری", ["phone"] = "تلفنی", ["messenger"] = "پیام‌رسان" },
        new() { ["vehicle"] = "خرید خودرو", ["parts"] = "قطعات", ["service"] = "خدمات", ["other"] = "سایر" }, []);
}
public record Visit
{
    public string Uuid { get; init; } = Guid.NewGuid().ToString();
    public string FullName { get; init; } = "";
    public string? Mobile { get; init; }
    public string AcquisitionSource { get; init; } = "unknown";
    public string EntryChannel { get; init; } = "in_person";
    public string Purpose { get; init; } = "vehicle";
    public string Subject { get; init; } = "";
    public string Notes { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public bool NeedsFollowUp { get; init; }
    public DateTimeOffset? FollowUpAt { get; init; }
    public Dictionary<string, JsonElement> Fields { get; init; } = [];
}
public record VisitRow(Visit Visit, string State, string Error, long? ServerId);
public record Receipt(string Uuid, long VisitId, long? ContactId, long? LeadId);
public record ContactPage(List<Contact> Items, long? Next);

public static partial class Validation
{
    public static string Digits(string value)
    {
        const string fa = "۰۱۲۳۴۵۶۷۸۹", ar = "٠١٢٣٤٥٦٧٨٩";
        return string.Concat(value.Select(c => fa.Contains(c) ? (char)('0' + fa.IndexOf(c)) :
            ar.Contains(c) ? (char)('0' + ar.IndexOf(c)) : c));
    }
    public static string? Mobile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(Digits(value).Where(c => c is >= '0' and <= '9').ToArray());
        if (digits.Length == 12 && digits.StartsWith("98", StringComparison.Ordinal)) digits = "0" + digits[2..];
        else if (digits.Length == 10 && digits[0] == '9') digits = "0" + digits;
        if (!MobilePattern().IsMatch(digits)) throw new InvalidDataException("شماره موبایل معتبر نیست.");
        return digits;
    }
    [GeneratedRegex("^09[0-9]{9}$")]
    private static partial Regex MobilePattern();

    public static Visit Normalize(Visit visit, Metadata meta)
    {
        if (!Guid.TryParse(visit.Uuid, out _)) throw new InvalidDataException("شناسه مراجعه معتبر نیست.");
        if (visit.FullName.Length > 200 || visit.Subject.Length > 255 || visit.Notes.Length > 2000)
            throw new InvalidDataException("نام، موضوع یا توضیحات بیش از حد طولانی است.");
        if (!meta.Sources.ContainsKey(visit.AcquisitionSource) || !meta.Channels.ContainsKey(visit.EntryChannel)
            || !meta.Purposes.ContainsKey(visit.Purpose)) throw new InvalidDataException("گزینه انتخاب‌شده معتبر نیست.");
        if (visit.OccurredAt > DateTimeOffset.UtcNow.AddDays(1)) throw new InvalidDataException("زمان مراجعه معتبر نیست.");
        if (visit.NeedsFollowUp && Mobile(visit.Mobile) is null)
            throw new InvalidDataException("برای ساخت سرنخ پیگیری، شماره موبایل را وارد کنید.");
        return visit with { Mobile = Mobile(visit.Mobile), FullName = visit.FullName.Trim(),
            Subject = visit.Subject.Trim(), Notes = visit.Notes.Trim() };
    }

    public static DateTimeOffset? PersianDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = Digits(value).Split('/');
        if (parts.Length != 3) throw new InvalidDataException("تاریخ را به صورت ۱۴۰۵/۰۷/۲۸ وارد کنید.");
        try
        {
            var date = new PersianCalendar().ToDateTime(int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture), 9, 0, 0, 0);
            return new DateTimeOffset(date, TimeSpan.FromMinutes(210));
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        { throw new InvalidDataException("تاریخ شمسی معتبر نیست."); }
    }
}
