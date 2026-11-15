using Workman.Core.Queries;
using Workman.Core.Rendering;
using Workman.Core.Writing;

namespace Workman.Core.Board;

public sealed partial class BoardReader
{
    /// <summary>Rows per agenda section; the rest are counted.</summary>
    private const int AgendaSectionCap = 20;

    /// <summary>work_agenda's question, as of the injected clock: the record GET /api/v1/agenda renders too.</summary>
    public async Task<Agenda> AgendaOfAsync(
        ScopeResult.Found scope,
        DateValue until,
        DateValue? since,
        CancellationToken cancellationToken = default)
    {
        var home = await board.RunOfAsync(scope.Item.Id, cancellationToken);
        return await board.AgendaAsync(
            scope.WorkspaceId,
            scope.Item.Id,
            home?.Id ?? scope.Item.Id,
            until,
            since,
            _clock.GetUtcNow(),
            cancellationToken);
    }

    /// <summary>
    /// work_agenda: overdue, due by <paramref name="until"/>, and became actionable since <paramref name="since"/>
    /// (the start of today in the workspace's zone when none is given).
    /// </summary>
    public async Task<string> AgendaAsync(
        ScopeResult.Found scope,
        DateValue until,
        DateValue? since,
        CancellationToken cancellationToken = default)
    {
        var agenda = await AgendaOfAsync(scope, until, since, cancellationToken);
        var lines = new List<string>
        {
            $"AGENDA  {scope.Item.Ref}  ·  zone {agenda.Zone}  ·  as of {agenda.Now.UtcDateTime:yyyy-MM-dd HH:mm} UTC",
        };

        Section(lines, "OVERDUE", agenda.Overdue);
        Section(lines, $"DUE BY {until.Text}", agenda.DueBy);
        Section(lines, $"ACTIONABLE SINCE {since?.Text ?? "the start of today"}", agenda.Actionable);

        var first = agenda.Overdue.Concat(agenda.DueBy).Concat(agenda.Actionable).FirstOrDefault();
        lines.Add(string.Empty);
        lines.Add(first is null
            ? $"Next: nothing dated needs you — work_brief(scope:\"{scope.Item.Ref}\") to resume."
            : $"Next: work_get(refs:[\"{first.Ref}\"]) for the full brief.");

        return string.Join("\n", lines);
    }

    private static void Section(List<string> lines, string title, IReadOnlyList<AgendaRow> rows)
    {
        lines.Add(string.Empty);
        lines.Add($"{title} ({rows.Count})");
        if (rows.Count == 0)
        {
            lines.Add("  none");
            return;
        }

        foreach (var row in rows.Take(AgendaSectionCap))
        {
            var parts = new List<string> { $"  {row.Ref}  {Glyph.Flat(row.Title)}" };
            if (row.Due is { Length: > 0 } due)
            {
                parts.Add($"due {due}");
            }

            parts.Add(row.Status);
            if (row.HeldBy is { Length: > 0 } holder)
            {
                parts.Add($"held by {Glyph.Flat(holder)}");
            }

            if (row.Blockers.Length > 0)
            {
                parts.Add($"blocked by {string.Join(", ", row.Blockers)}");
            }

            lines.Add(string.Join("  ·  ", parts));
        }

        if (rows.Count > AgendaSectionCap)
        {
            lines.Add($"  … {rows.Count - AgendaSectionCap} more");
        }
    }
}
