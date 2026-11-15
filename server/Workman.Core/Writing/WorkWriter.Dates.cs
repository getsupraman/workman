using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Workman.Core.Rendering;

namespace Workman.Core.Writing;

/// <summary>
/// deadlines/2 — <c>due</c> and <c>not_before</c> on work_create and work_update. A day is stored as a day and read in
/// the workspace's zone by the <c>ItemDates</c> SQL functions; the ordering check reads it the same way, so the write
/// and every later read agree on what a day means.
/// </summary>
public sealed partial class WorkWriter
{
    /// <summary>One date argument: <see cref="Set"/> false leaves the column alone; set with a null value clears it.</summary>
    private readonly record struct DateChange(bool Set, DateValue? Value);

    /// <summary>
    /// The two arguments parsed, or the refusal for the first that is not a date. On work_update an empty string
    /// clears (null is "unchanged", as for every other field); on work_create blank is simply absent.
    /// </summary>
    private static (DateChange Due, DateChange NotBefore, string? Refusal) ParseDates(
        string call, string example, string? due, string? notBefore, string close)
    {
        var parsed = new DateChange[2];
        var given = new[] { ("due", due), ("not_before", notBefore) };

        for (var i = 0; i < given.Length; i++)
        {
            var (field, text) = given[i];
            if (text is null)
            {
                continue;
            }

            if (Blank(text))
            {
                parsed[i] = new DateChange(true, null);
                continue;
            }

            var (value, problem) = DateValue.Parse(text);
            if (value is null)
            {
                return (default, default, Refusal.Blocked(
                    $"{call} — {field} \"{Glyph.Flat(text.Trim())}\" is not a date.",
                    $"{problem}: {field} is a day, YYYY-MM-DD (that whole day in the workspace's time zone), "
                    + "or an ISO date-time with Z or an offset",
                    $"{example}{field}:\"2026-11-20\"{close}",
                    $"{example}{field}:\"2026-11-20T17:00:00+01:00\"{close}"));
            }

            parsed[i] = new DateChange(true, value);
        }

        return (parsed[0], parsed[1], null);
    }

    /// <summary>
    /// The refusal for a not_before after the due, both read in the workspace's zone. A not_before on the due day
    /// itself is fine; one that starts at or after the end of the due day is not.
    /// </summary>
    private static async Task<string?> DateOrderRefusalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        DateValue? due,
        DateValue? notBefore,
        string call,
        string example,
        string close,
        CancellationToken cancellationToken)
    {
        if (due is not { } d || notBefore is not { } n)
        {
            return null;
        }

        const string sql =
            """
            WITH z AS (SELECT timezone_of(@workspace) AS zone)
            SELECT CASE WHEN @due_on::date IS NOT NULL
                        THEN date_value_start(@nb_on::date, @nb_at::timestamptz, z.zone) >= date_value_end(@due_on::date, NULL, z.zone)
                        ELSE date_value_start(@nb_on::date, @nb_at::timestamptz, z.zone) > @due_at::timestamptz END
            FROM z;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        Bind(command, "workspace", workspaceId);
        Bind(command, "due_on", d.Day);
        Bind(command, "due_at", d.At);
        Bind(command, "nb_on", n.Day);
        Bind(command, "nb_at", n.At);
        var after = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;

        return after
            ? Refusal.Blocked(
                $"{call} — not_before {n.Text} is after due {d.Text}.",
                "an item cannot become actionable only after it must be done; a day is read in the workspace's time zone",
                $"{example}not_before:\"<on or before {d.Text}>\"{close}",
                $"{example}due:\"<on or after {n.Text}>\"{close}")
            : null;
    }

    private static async Task<(DateValue? Due, DateValue? NotBefore)> DatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT due_on, due_at, not_before_on, not_before_at FROM item WHERE id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        Bind(command, "id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (null, null);
        }

        return (
            DateValue.From(Day(reader, 0), Instant(reader, 1)),
            DateValue.From(Day(reader, 2), Instant(reader, 3)));

        static DateOnly? Day(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);

        static DateTimeOffset? Instant(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
    }

    /// <summary>One <c>dates_changed</c> event per field whose value moved: {field, from, to}, each the printed form or null.</summary>
    private static async Task JournalDatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid itemId,
        string reference,
        Actor actor,
        (string Field, DateValue? From, DateChange Change)[] changes,
        CancellationToken cancellationToken)
    {
        foreach (var (field, from, change) in changes)
        {
            var to = change.Value;
            if (!change.Set || from?.Text == to?.Text)
            {
                continue;
            }

            await EventAsync(
                connection, transaction, workspaceId, itemId, actor, "dates_changed",
                $"{reference} {field} {from?.Text ?? "—"} → {to?.Text ?? "—"}",
                new { field, from = from?.Text, to = to?.Text },
                cancellationToken);
        }
    }

    /// <summary>The created event's payload with the dates it was created with; unchanged when it has none.</summary>
    private static object WithDates(object payload, DateValue? due, DateValue? notBefore)
    {
        if (due is null && notBefore is null)
        {
            return payload;
        }

        var node = JsonSerializer.SerializeToNode(payload)!.AsObject();
        if (due is { } d)
        {
            node["due"] = d.Text;
        }

        if (notBefore is { } n)
        {
            node["not_before"] = n.Text;
        }

        return node;
    }
}
