using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Mcp;

namespace Workman.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>
    /// What the sidebar's and the overview's run bars draw, for the runs <see cref="BoardsAsync"/> would return: each
    /// run's counted leaves with the inputs the dashboard's <c>state()</c> takes, the first row holding a claim, and
    /// the run's close. A fixed number of commands for any number of runs; the colours stay the client's.
    /// </summary>
    private static async Task<IResult> BarsAsync(
        string? runs,
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        var (refused, snapshot, book, known) = await RunsAskedAsync(runs, board, api, tenant, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var held = await api.HeldClaimsAsync(known, cancellationToken);
        var rows = await api.BarsAsync(known, [.. held.Select(c => c.ItemId).Distinct()], cancellationToken);

        var leafIds = rows.Where(r => r.Counted).Select(r => r.Id).Distinct().ToList();
        var links = leafIds.Count == 0 ? [] : await board.LinksAsync(leafIds, cancellationToken);
        var blockedBy = links
            .Where(l => l.Type == LinkType.Blocks && !Closed(l.FromStatus))
            .ToLookup(l => l.ToId, l => l.FromId);

        await book.ItemsAsync(leafIds.Concat(links.Select(l => l.FromId)), cancellationToken);
        var claims = (await book.ClaimsAsync(held, snapshot.AsOf, cancellationToken)).ToDictionary(c => c.Item.Id);

        var byRun = rows.ToLookup(r => r.Root);
        return Results.Ok(new BarsView(
            snapshot.AsOf,
            snapshot.Seq,
            [.. known.Select(run =>
            {
                var tree = byRun[run].ToList();
                var holding = tree.Select(r => claims.GetValueOrDefault(r.Id)).FirstOrDefault(c => c is not null);

                return new RunBarsView(
                    run,
                    tree.FirstOrDefault(r => r.Id == run)?.ClosedAt,
                    holding is null ? null : new BarHolderView(holding.Item, holding.Session?.Name, holding.Role),
                    [.. tree.Where(r => r.Counted).Select(r =>
                    {
                        var item = book.Item(r.Id);
                        return new BarLeafView(
                            r.Id,
                            item.QualifiedRef,
                            item.Title,
                            r.Kind,
                            V1Text.Status(r.Status),
                            V1Text.Resolution(r.Resolution),
                            claims.GetValueOrDefault(r.Id) is { Expired: true },
                            r.Proof,
                            [.. blockedBy[r.Id].Distinct().Select(id => book.Item(id).QualifiedRef)]);
                    })]);
            })]));
    }
}
