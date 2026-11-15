using System.Globalization;
using System.Text;

namespace Workman.Api.App;

/// <summary>
/// One event of the calendar feed. A day is all-day (<c>DTSTART;VALUE=DATE</c>, <c>DTEND</c> the day after, both
/// <c>YYYYMMDD</c>); an instant is timed, <c>YYYYMMDDTHHMMSSZ</c> in UTC.
/// </summary>
public sealed record CalendarEvent(
    string Uid,
    string? Day,
    string? DayAfter,
    string? At,
    string Summary,
    string Url,
    string Description);

/// <summary>
/// The iCalendar text of the feed (RFC 5545): CRLF line ends, lines folded at 75 octets without splitting a UTF-8
/// character, and TEXT values escaped (<c>\ ; ,</c> and newlines). Pure: no database, no request.
/// </summary>
public static class ICalendar
{
    public const string ContentType = "text/calendar; charset=utf-8";

    private const int MaxOctets = 75;

    public static string Render(string name, string zone, DateTimeOffset stamp, IEnumerable<CalendarEvent> events)
    {
        var text = new StringBuilder();
        var dtstamp = stamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        Line(text, "BEGIN:VCALENDAR");
        Line(text, "VERSION:2.0");
        Line(text, "PRODID:-//Workman//Calendar feed//EN");
        Line(text, "CALSCALE:GREGORIAN");
        Line(text, "METHOD:PUBLISH");
        Line(text, "X-WR-CALNAME:" + Escape(name));
        Line(text, "X-WR-TIMEZONE:" + Escape(zone));

        foreach (var e in events)
        {
            Line(text, "BEGIN:VEVENT");
            Line(text, "UID:" + Escape(e.Uid));
            Line(text, "DTSTAMP:" + dtstamp);
            if (e.Day is { } day)
            {
                Line(text, "DTSTART;VALUE=DATE:" + day);
                Line(text, "DTEND;VALUE=DATE:" + e.DayAfter);
            }
            else
            {
                Line(text, "DTSTART:" + e.At);
            }

            Line(text, "SUMMARY:" + Escape(e.Summary));
            Line(text, "URL:" + e.Url);
            Line(text, "DESCRIPTION:" + Escape(e.Description));
            Line(text, "END:VEVENT");
        }

        Line(text, "END:VCALENDAR");
        return text.ToString();
    }

    /// <summary>A TEXT value (RFC 5545 §3.3.11): backslash, semicolon, comma and newlines escaped, other controls dropped.</summary>
    public static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '\\':
                    escaped.Append(@"\\");
                    break;
                case ';':
                    escaped.Append(@"\;");
                    break;
                case ',':
                    escaped.Append(@"\,");
                    break;
                case '\r':
                    escaped.Append(@"\n");
                    if (i + 1 < value.Length && value[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case '\n':
                    escaped.Append(@"\n");
                    break;
                default:
                    if (c == '\t' || !char.IsControl(c))
                    {
                        escaped.Append(c);
                    }

                    break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>
    /// A content line folded (RFC 5545 §3.1): no physical line longer than 75 octets, CRLF and one space before each
    /// continuation, and never inside a UTF-8 sequence or a surrogate pair.
    /// </summary>
    public static string Fold(string line)
    {
        var folded = new StringBuilder(line.Length + 8);
        var octets = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > MaxOctets)
            {
                folded.Append("\r\n ");
                octets = 1;
            }

            folded.Append(rune.ToString());
            octets += size;
        }

        return folded.ToString();
    }

    private static void Line(StringBuilder text, string line) => text.Append(Fold(line)).Append("\r\n");
}
