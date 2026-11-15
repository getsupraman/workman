using System.Text;
using Workman.Api.App;
using Xunit;

namespace Workman.Tests.App;

/// <summary>deadlines/7: the feed's iCalendar text on its own — RFC 5545 escaping, folding and line ends.</summary>
public sealed class ICalendarTests
{
    [Fact]
    public void Calendar_text_values_escape_backslash_semicolon_comma_and_newlines_and_drop_other_controls()
    {
        Assert.Equal(@"a\, b\; c\\ d\ne\nf\ng" + "\tend", ICalendar.Escape("a, b; c\\ d\r\ne\nf\rg\u0007\tend"));
        Assert.Equal("plain", ICalendar.Escape("plain"));
    }

    [Fact]
    public void Calendar_lines_fold_at_75_octets_never_inside_a_utf8_character()
    {
        var line = "SUMMARY:" + new string('x', 66) + "é€😀" + new string('y', 200);
        var folded = ICalendar.Fold(line);

        var physical = folded.Split("\r\n");
        Assert.True(physical.Length > 3);
        Assert.All(physical, p => Assert.True(Encoding.UTF8.GetByteCount(p) <= 75, $"{Encoding.UTF8.GetByteCount(p)} octets: {p}"));
        Assert.All(physical.Skip(1), p => Assert.StartsWith(" ", p, StringComparison.Ordinal));
        Assert.DoesNotContain('�', folded);
        Assert.Equal(line, folded.Replace("\r\n ", string.Empty, StringComparison.Ordinal));

        Assert.Equal("SHORT:x", ICalendar.Fold("SHORT:x"));
        var exact = new string('z', 75);
        Assert.Equal(exact, ICalendar.Fold(exact));
        Assert.Equal(new string('z', 75) + "\r\n z", ICalendar.Fold(exact + "z"));
    }

    [Fact]
    public void Calendar_renders_an_all_day_and_a_timed_event_with_crlf_line_ends()
    {
        var text = ICalendar.Render(
            "Workman demo",
            "Europe/Rome",
            new DateTimeOffset(2026, 11, 15, 9, 30, 0, TimeSpan.FromHours(1)),
            [
                new CalendarEvent("a@example.test", "20261201", "20261202", null, "1.1 Ship, then; rest", "https://example.test/r/p/1.1", "open"),
                new CalendarEvent("b@example.test", null, null, "20261203T163000Z", "2.1 Call", "https://example.test/r/p/2.1", "in_progress"),
            ]);

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("END:VCALENDAR\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n\r\n", text, StringComparison.Ordinal);
        Assert.Equal(text.Split('\n').Length - 1, text.Split("\r\n").Length - 1);

        Assert.Contains("DTSTAMP:20261115T083000Z\r\n", text, StringComparison.Ordinal);
        Assert.Contains("DTSTART;VALUE=DATE:20261201\r\nDTEND;VALUE=DATE:20261202\r\n", text, StringComparison.Ordinal);
        Assert.Contains("DTSTART:20261203T163000Z\r\n", text, StringComparison.Ordinal);
        Assert.Contains(@"SUMMARY:1.1 Ship\, then\; rest" + "\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-WR-TIMEZONE:Europe/Rome\r\n", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("BEGIN:VEVENT").Length - 1);
    }
}
