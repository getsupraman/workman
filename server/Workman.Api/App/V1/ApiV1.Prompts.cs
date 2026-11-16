using Microsoft.AspNetCore.Mvc;
using Workman.Core.Board;
using Workman.Core.Queries;
using Workman.Core.Rendering;
using Workman.Mcp;

namespace Workman.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>The most ids one <c>scopes=</c> read takes.</summary>
    private const int PromptsMaxScopes = 200;

    /// <summary>
    /// The prompt actions that apply to one scope, or with <c>scopes</c> to each of many, built by
    /// <see cref="BoardReader.PromptsAsync(Guid, IReadOnlyCollection{ScopeRef}, CancellationToken)"/> so the dashboard
    /// and the skills copy one wording. Many scopes cost the commands one does. Read-only: nothing here sweeps or
    /// consumes.
    /// </summary>
    private static async Task<IResult> PromptsAsync(
        [FromQuery] Guid? scope,
        [FromQuery] string? scopes,
        BoardReader reader,
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        if (scopes is not null)
        {
            return scope is null
                ? await ManyPromptsAsync(scopes, reader, board, api, tenant, cancellationToken)
                : Results.BadRequest(new { error = "scope= and scopes= are exclusive" });
        }

        if (scope is not { } id)
        {
            return Results.BadRequest(new { error = "scope= is required: the id of a project, run or item" });
        }

        var book = new V1Book(api, board);
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);
        if (!await KnownAsync(book, snapshot, id, cancellationToken))
        {
            return NoItem(id);
        }

        var prompts = await reader.PromptsAsync(
            new ScopeResult.Found(snapshot.WorkspaceId, book.Row(id).Scope), cancellationToken);

        return Results.Ok(new PromptsView(
            snapshot.AsOf,
            snapshot.Seq,
            book.Item(id),
            [.. prompts.Select(Prompt)]));
    }

    /// <summary>
    /// <c>scopes=a,b,…</c>: each id's prompts as <c>scope=</c> returns them, in the order asked, leaving out an id that
    /// is no item the caller sees. A malformed id or more than <see cref="PromptsMaxScopes"/> is a 400.
    /// </summary>
    private static async Task<IResult> ManyPromptsAsync(
        string scopes,
        BoardReader reader,
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        var asked = new List<Guid>();
        foreach (var part in scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var parsed))
            {
                return Results.BadRequest(new { error = $"scopes: '{part}' is not an id" });
            }

            asked.Add(parsed);
        }

        asked = [.. asked.Distinct()];
        if (asked.Count > PromptsMaxScopes)
        {
            return Results.BadRequest(new { error = $"scopes: at most {PromptsMaxScopes} ids, got {asked.Count}" });
        }

        var book = new V1Book(api, board);
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);
        await book.PlacedAsync(asked, cancellationToken);
        var known = asked.Where(id => book.Has(id) && book.Row(id).WorkspaceId == snapshot.WorkspaceId).ToList();

        var prompts = await reader.PromptsAsync(
            snapshot.WorkspaceId, [.. known.Select(id => book.Row(id).Scope)], cancellationToken);

        return Results.Ok(new ManyPromptsView(
            snapshot.AsOf,
            snapshot.Seq,
            [.. known.Select(id => new ScopePromptsView(book.Item(id), [.. prompts[id].Select(Prompt)]))]));
    }

    private static PromptView Prompt(PromptAction p) => new(p.Key, p.Label, p.Count, p.Text);
}
