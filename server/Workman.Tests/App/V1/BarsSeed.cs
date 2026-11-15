using Dapper;
using Npgsql;
using Workman.Tests.Store;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App.V1;

/// <summary>A workspace <see cref="BarsSeed"/> made: its slug for the server, and its runs in creation order.</summary>
internal sealed record BarsWorld(string Slug, Guid WorkspaceId, IReadOnlyList<Guid> RunIds, int Items);

/// <summary>
/// A workspace the size of a busy production one, in bulk SQL: ten projects whose runs share their refs (<c>m1</c>,
/// <c>1.1</c>, …) so every ref has to be qualified, and under each run every state a bar draws — verified, done by a
/// worker's word, done with nothing, in progress, a lapsed claim, blocked, waiting on a sibling and on another run,
/// cancelled, moved, an empty phase, a sub-task, a finding. Sixteen items a run: 150 runs is 2,400 items.
/// </summary>
internal static class BarsSeed
{
    public const int Projects = 10;

    private sealed record Row(Guid Id, Guid? Parent, string Kind, string Ref, decimal Seq, string Title, string Status, string? Resolution, int Level);

    public static async Task<BarsWorld> CreateAsync(PostgresFixture fixture, int runCount)
    {
        var workspace = Guid.CreateVersion7();
        var slug = $"bars-{workspace:n}";
        var now = DateTime.UtcNow;

        var rows = new List<Row>();
        var runs = new List<(Guid Run, Guid Project, string Ref)>();
        var verifications = new List<(Guid Item, bool Passed, string RunBy)>();
        var claims = new List<(Guid Item, bool Expired)>();
        var links = new List<(Guid From, Guid To)>();

        Row Add(Guid? parent, string kind, string reference, decimal seq, string status = "open", string? resolution = null, int level = 0)
        {
            var row = new Row(Guid.CreateVersion7(), parent, kind, reference, seq, $"{kind} {reference} of the bars seed", status, resolution, level);
            rows.Add(row);
            return row;
        }

        var projects = Enumerable.Range(0, Projects).Select(p => Add(null, "project", $"bp{p}-{workspace:n}"[..12], p + 1, "in_progress")).ToList();

        Row? previousWaitedOn = null;
        for (var r = 0; r < runCount; r++)
        {
            var project = projects[r % Projects];
            var closedRun = r % 3 == 2;
            var run = Add(project.Id, "run", $"m{(r / Projects) + 1}", (r / Projects) + 1, closedRun ? "done" : "in_progress", closedRun ? "accepted" : null, 1);
            runs.Add((run.Id, project.Id, run.Ref));

            var one = Add(run.Id, "phase", "1", 1, "in_progress", level: 2);
            var two = Add(run.Id, "phase", "2", 2, "open", level: 2);
            Add(run.Id, "phase", "3", 3, level: 2);

            var proven = Add(one.Id, "task", "1.1", 1, "done", "accepted", 3);
            var claimed = Add(one.Id, "task", "1.2", 2, "done", "accepted", 3);
            Add(one.Id, "task", "1.3", 3, "done", "accepted", 3);
            var doing = Add(one.Id, "task", "1.4", 4, "in_progress", level: 3);
            var lapsed = Add(one.Id, "task", "1.5", 5, "in_progress", level: 3);
            Add(two.Id, "task", "2.1", 1, "blocked", level: 3);
            var waits = Add(two.Id, "task", "2.2", 2, level: 3);
            var gate = Add(two.Id, "task", "2.3", 3, level: 3);
            Add(two.Id, "task", "2.4", 4, "cancelled", level: 3);
            Add(two.Id, "task", "2.5", 5, "done", "obsolete", 3);
            var sub = Add(proven.Id, "subtask", "1.1.1", 1, level: 4);
            Add(rows.Single(x => x.Parent == two.Id && x.Ref == "2.1").Id, "finding", "F-1", 1, level: 4);

            verifications.Add((proven.Id, true, "orchestrator"));
            verifications.Add((claimed.Id, false, "orchestrator"));
            verifications.Add((claimed.Id, true, "worker"));
            claims.Add((doing.Id, false));
            claims.Add((lapsed.Id, true));
            if (r % 4 == 1)
            {
                claims.Add((one.Id, false));
            }

            links.Add((gate.Id, waits.Id));
            if (previousWaitedOn is not null)
            {
                links.Add((previousWaitedOn.Id, sub.Id));
            }

            previousWaitedOn = gate;
        }

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await connection.ExecuteAsync("INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);", new { workspace, slug });

        foreach (var level in rows.GroupBy(r => r.Level).OrderBy(g => g.Key))
        {
            var batch = level.ToList();
            await connection.ExecuteAsync(
                """
                INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title, status, resolution, opened_at, started_at,
                                  closed_at, closed_by)
                SELECT u.id, @workspace, u.parent, u.kind, u.ref, u.seq, u.title, u.status::item_status,
                       u.resolution::item_resolution, @opened,
                       CASE WHEN u.status <> 'open' THEN @opened END,
                       CASE WHEN u.status IN ('done','cancelled','superseded') THEN @closed END,
                       CASE WHEN u.status IN ('done','cancelled','superseded') THEN 'seed' END
                FROM   unnest(@ids, @parents, @kinds, @refs, @seqs, @titles, @statuses, @resolutions)
                       AS u(id, parent, kind, ref, seq, title, status, resolution);
                """,
                new
                {
                    workspace,
                    opened = now.AddDays(-20),
                    closed = now.AddDays(-2),
                    ids = batch.Select(r => r.Id).ToArray(),
                    parents = batch.Select(r => r.Parent).ToArray(),
                    kinds = batch.Select(r => r.Kind).ToArray(),
                    refs = batch.Select(r => r.Ref).ToArray(),
                    seqs = batch.Select(r => r.Seq).ToArray(),
                    titles = batch.Select(r => r.Title).ToArray(),
                    statuses = batch.Select(r => r.Status).ToArray(),
                    resolutions = batch.Select(r => r.Resolution).ToArray(),
                });
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO run_config (item_id, project_item_id, branch, serialization_key)
            SELECT u.run, u.project, 'ws/' || u.ref, 'bars/' || u.run
            FROM   unnest(@runs, @projects, @refs) AS u(run, project, ref);

            INSERT INTO verification (id, workspace_id, item_id, command, result_line, passed, run_by, at)
            SELECT gen_random_uuid(), @workspace, u.item, 'dotnet test', 'Passed!', u.passed, u.run_by::verify_runner,
                   @at + (u.n || ' minutes')::interval
            FROM   unnest(@verified, @passed, @runBys) WITH ORDINALITY AS u(item, passed, run_by, n);

            INSERT INTO claim (id, item_id, serialization_key, key_workspace_id, agent_name, role, acquired_at, heartbeat_at,
                               expires_at)
            SELECT gen_random_uuid(), u.item, 'bars-tree/' || u.item, @workspace, 'bars-agent', 'worker'::actor_role,
                   @at, @at, CASE WHEN u.expired THEN now() - interval '1 hour' ELSE now() + interval '1 hour' END
            FROM   unnest(@claimed, @expired) AS u(item, expired);

            INSERT INTO item_link (id, workspace_id, from_item_id, to_item_id, type)
            SELECT gen_random_uuid(), @workspace, u.f, u.t, 'blocks'::link_type
            FROM   unnest(@froms, @tos) AS u(f, t);
            """,
            new
            {
                workspace,
                at = now.AddDays(-3),
                runs = runs.Select(r => r.Run).ToArray(),
                projects = runs.Select(r => r.Project).ToArray(),
                refs = runs.Select(r => r.Ref).ToArray(),
                verified = verifications.Select(v => v.Item).ToArray(),
                passed = verifications.Select(v => v.Passed).ToArray(),
                runBys = verifications.Select(v => v.RunBy).ToArray(),
                claimed = claims.Select(c => c.Item).ToArray(),
                expired = claims.Select(c => c.Expired).ToArray(),
                froms = links.Select(l => l.From).ToArray(),
                tos = links.Select(l => l.To).ToArray(),
            });

        await connection.ExecuteAsync("ANALYZE item; ANALYZE item_link; ANALYZE claim; ANALYZE verification;");

        return new BarsWorld(slug, workspace, [.. runs.Select(r => r.Run)], rows.Count);
    }
}
