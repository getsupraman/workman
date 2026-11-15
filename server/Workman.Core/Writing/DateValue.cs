using System.Globalization;
using System.Text.RegularExpressions;

namespace Workman.Core.Writing;

/// <summary>
/// A <c>due</c> or <c>not_before</c> as an agent writes it: a calendar day, or an ISO date-time with an offset. A day
/// stays a day — the workspace's time zone gives it its bounds at read time, in the SQL functions of the
/// <c>ItemDates</c> migration — so a day is never turned into an instant here.
/// </summary>
public readonly partial record struct DateValue(DateOnly? Day, DateTimeOffset? At)
{
    /// <summary>The SQL that reads an item's due as an instant in its workspace's zone; <c>i</c> is the item alias.</summary>
    public static string DueEndSql(string alias) => $"item_due_end({alias})";

    /// <summary>The SQL that reads an item's not_before as an instant in its workspace's zone.</summary>
    public static string NotBeforeStartSql(string alias) => $"item_not_before_start({alias})";

    /// <summary>The form the journal and work_get print, and <c>date_text_end</c> reads back.</summary>
    public string Text => Day is { } day
        ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : At!.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateValue? From(DateOnly? day, DateTimeOffset? at)
        => day is not null || at is not null ? new DateValue(day, at) : null;

    /// <summary>The value, or the reason it is not one. Blank is not parsed here: the caller decides what blank means.</summary>
    public static (DateValue? Value, string? Problem) Parse(string text)
    {
        var trimmed = text.Trim();
        if (DayForm().IsMatch(trimmed))
        {
            return DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? (new DateValue(day, null), null)
                : (null, $"\"{trimmed}\" is not a calendar day");
        }

        if (InstantForm().IsMatch(trimmed))
        {
            return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? (new DateValue(null, at.ToUniversalTime()), null)
                : (null, $"\"{trimmed}\" is not a real date-time");
        }

        return LocalForm().IsMatch(trimmed)
            ? (null, $"\"{trimmed}\" has no offset, so it names no instant")
            : (null, $"\"{trimmed}\" is neither a date (YYYY-MM-DD) nor an ISO date-time");
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DayForm();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex InstantForm();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?$")]
    private static partial Regex LocalForm();
}
