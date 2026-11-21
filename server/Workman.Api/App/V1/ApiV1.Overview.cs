using Workman.Core.Queries;
using Workman.Mcp;

namespace Workman.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>Everything a human needs on landing, in one call.</summary>
    private static async Task<IResult> OverviewAsync(
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);

        var book = new V1Book(api, board);
        var projects = await board.ProjectsAsync(snapshot.WorkspaceId, cancellationToken);
        var scopes = projects.Select(p => p.Id).ToList();

        var runs = (await api.RunsAsync(scopes, cancellationToken)).ToLookup(r => r.ProjectId);

        var allRuns = scopes.SelectMany(p => runs[p]).Select(r => r.Id).ToList();
        await book.ItemsAsync(scopes.Concat(allRuns), cancellationToken);

        var rollups = (await api.RollupsAsync([.. scopes, .. allRuns], cancellationToken)).ToDictionary(r => r.Id);
        var inbox = await InboxRowsAsync(snapshot, scopes, book, board, api, webBase: null, clock.GetUtcNow(), cancellationToken);
        var inboxByRun = inbox.Where(r => r.Run is not null).GroupBy(r => r.Run!.Id).ToDictionary(g => g.Key, g => g.Count());

        // The honesty strip, summed over projects exactly as work_audit counts it: each source reads every project in
        // one command, through the query the per-project call runs.
        var doneUnverified = (await board.DoneUnverifiedAsync(scopes, cancellationToken)).Sum(g => g.Count());
        var confirmed = (await board.DoneConfirmedAsync(scopes, cancellationToken)).Sum(g => g.Count());
        var forced = (await board.ForcedTransitionsAsync(scopes, cancellationToken)).Sum(g => g.Count());
        var ungated = (await board.UngatedPhasesAsync(scopes, cancellationToken)).Sum(g => g.Count());
        var followupsOpen = (await board.OpenFollowupsAsync(scopes, cancellationToken)).Values.Sum();

        var verified = (await api.LastVerificationsAsync(allRuns, cancellationToken)).ToDictionary(v => v.ScopeId);
        var repos = (await board.ProjectReposAsync(scopes, cancellationToken)).ToLookup(r => r.ProjectId);
        var sharing = (await board.ProjectSharingAsync(scopes, cancellationToken)).ToDictionary(s => s.ProjectId);
        var projectViews = new List<ProjectOverviewView>();
        foreach (var project in projects)
        {
            var runViews = new List<RunOverviewView>();
            foreach (var run in runs[project.Id])
            {
                var last = verified.GetValueOrDefault(run.Id);
                runViews.Add(new RunOverviewView(
                    book.Item(run.Id),
                    Rollup(rollups.GetValueOrDefault(run.Id)),
                    run.Branch,
                    run.Worktree,
                    run.ActiveClaims,
                    run.LastEventAt,
                    inboxByRun.GetValueOrDefault(run.Id),
                    last is null ? null : new LastVerificationView(last.Passed, last.At, last.Confirmed)));
            }

            projectViews.Add(new ProjectOverviewView(
                book.Item(project.Id),
                Rollup(rollups.GetValueOrDefault(project.Id)),
                runViews,
                [.. repos[project.Id].Select(r => new RepoView(r.Kind, r.Key))],
                project.ArchivedAt,
                project.LastActivityAt,
                project.OpenRuns,
                sharing.GetValueOrDefault(project.Id) is { } shared
                    ? new SharingView(shared.Members, shared.You, shared.Names)
                    : new SharingView(0, false, [])));
        }

        var claims = await book.ClaimsAsync(
            [.. (await api.HeldClaimsAsync(scopes, cancellationToken)).OrderByDescending(c => c.AcquiredAt).ThenBy(c => c.Id)],
            snapshot.AsOf,
            cancellationToken);

        return Results.Ok(new OverviewView(
            snapshot.AsOf,
            snapshot.Seq,
            projectViews,
            claims,
            inbox.Count,
            new HonestyView(doneUnverified, forced, ungated, followupsOpen, confirmed)));
    }
}
