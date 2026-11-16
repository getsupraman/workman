using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Workman.Core.Queries;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.App.V1.V1Http;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App.V1;

/// <summary>
/// <c>/api/v1/overview</c>, <c>/api/v1/inbox</c> and <c>/api/v1/prompts?scopes=</c> read each source once for every
/// project: the same number of SQL commands for 2 projects as for 14, and the same rows, counts and prompts as the
/// per-project reads return for the same data.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1ReadLoopsTests(PostgresFixture fixture)
{
    /// <summary>Every kind of inbox row the seed makes under each project.</summary>
    private static readonly string[] Kinds =
    [
        "assumption_due", "blocked", "blocker_finding", "done_unverified", "expired_claim", "overdue", "question",
        "stale_in_progress", "unconsumed_note",
    ];

    [Fact]
    public async Task Overview_inbox_and_prompts_issue_as_many_commands_for_14_projects_as_for_2()
    {
        var small = await SeedAsync(2);
        var large = await SeedAsync(14);

        var counts = new Dictionary<string, (int Small, int Large)>();
        foreach (var (name, path) in new (string, Func<Seeded, string>)[]
                 {
                     ("/api/v1/overview", _ => "/api/v1/overview"),
                     ("/api/v1/inbox", _ => "/api/v1/inbox"),
                     ("/api/v1/prompts?scopes=", s => $"/api/v1/prompts?scopes={string.Join(',', s.Projects)}"),
                 })
        {
            counts[name] = (await CommandsAsync(small, path(small)), await CommandsAsync(large, path(large)));
            SqlCommands.Log($"{name}: {counts[name].Small} commands for 2 projects, {counts[name].Large} for 14");
        }

        foreach (var (name, (smallCount, largeCount)) in counts)
        {
            Assert.True(smallCount > 0, $"{name}: no command counted, so the counter saw nothing");
            Assert.True(smallCount == largeCount, $"{name}: {smallCount} commands for 2 projects, {largeCount} for 14");
        }
    }

    [Fact]
    public async Task The_batched_inbox_honesty_and_prompts_equal_the_per_project_reads()
    {
        var world = await SeedAsync(14);
        await using var server = await StartAsync(fixture, world.Bars.Slug);
        var reader = await ReaderAsync(server);

        // The inbox of every project is the inbox of each, row for row.
        var all = Array(await GetAsync(reader, "/api/v1/inbox"), "rows");
        var each = new List<JsonElement>();
        foreach (var project in world.Projects)
        {
            each.AddRange(Array(await GetAsync(reader, $"/api/v1/inbox?project={project}"), "rows"));
        }

        Assert.Equal(Keyed(each), Keyed(all));
        Assert.Equal(Kinds, all.Select(r => r.GetProperty("kind").GetString()!).Distinct().Order(StringComparer.Ordinal));

        // The honesty strip is the per-project counts, summed.
        var board = new BoardQueries(fixture.DataSource);
        int doneUnverified = 0, forced = 0, ungated = 0, followups = 0;
        foreach (var project in world.Projects)
        {
            doneUnverified += (await board.DoneUnverifiedAsync(project, Ct)).Count;
            forced += (await board.ForcedTransitionsAsync(project, Ct)).Count;
            ungated += (await board.UngatedPhasesAsync(project, Ct)).Count;
            followups += await board.OpenFollowupsAsync(project, Ct);
        }

        var honesty = (await GetAsync(reader, "/api/v1/overview")).GetProperty("honesty");
        Assert.Equal(
            (doneUnverified, forced, ungated, followups),
            (honesty.GetProperty("doneUnverified").GetInt32(), honesty.GetProperty("forced").GetInt32(),
             honesty.GetProperty("ungated").GetInt32(), honesty.GetProperty("followupsOpen").GetInt32()));
        Assert.True(doneUnverified > 0 && forced > 0 && ungated > 0 && followups > 0, honesty.GetRawText());

        // Each scope's prompts in one read are what scope= returns for it: projects, runs and a task, mixed.
        var scopes = world.Projects.Concat(world.Bars.RunIds.Take(5)).Append(world.Task).ToList();
        var many = Array(await GetAsync(reader, $"/api/v1/prompts?scopes={string.Join(',', scopes)}"), "scopes");
        Assert.Equal(scopes, many.Select(s => s.GetProperty("scope").GetProperty("id").GetGuid()));

        foreach (var (scope, batched) in scopes.Zip(many))
        {
            var single = await GetAsync(reader, $"/api/v1/prompts?scope={scope}");
            Assert.Equal(single.GetProperty("scope").GetRawText(), batched.GetProperty("scope").GetRawText());
            Assert.Equal(single.GetProperty("prompts").GetRawText(), batched.GetProperty("prompts").GetRawText());
        }

        // The seed gives every count a prompt shows something to count.
        var counted = many.Take(world.Projects.Count)
            .SelectMany(s => Array(s, "prompts"))
            .Where(p => p.GetProperty("count").ValueKind == JsonValueKind.Number)
            .Select(p => p.GetProperty("key").GetString())
            .ToHashSet();
        Assert.Contains("cleanup", counted);
        Assert.Contains("triage", counted);
    }

    [Fact]
    public async Task Another_workspaces_scopes_do_not_leak_into_the_prompts_read()
    {
        var other = await SeedAsync(2);
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = await ReaderAsync(server);

        var asked = new[] { v1.World.ProjectId, other.Projects[0], v1.World.RunId, other.Bars.RunIds[0], other.Task };
        var body = await GetAsync(reader, $"/api/v1/prompts?scopes={string.Join(',', asked)}");

        Assert.Equal(
            [v1.World.ProjectId, v1.World.RunId],
            Array(body, "scopes").Select(s => s.GetProperty("scope").GetProperty("id").GetGuid()));
        Assert.DoesNotContain("bars seed", body.GetRawText());
        Assert.DoesNotContain("read-loops", body.GetRawText());
    }

    [Fact]
    public async Task A_project_the_caller_is_not_a_member_of_is_absent_from_the_prompts_read()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var asked = $"/api/v1/prompts?scopes={world.P1Id},{world.P2Id},{world.P2RunId}";

        var u1 = await world.CookieAsync(world.User1);
        var seen = Array(await GetAsync(u1, asked), "scopes").Select(s => s.GetProperty("scope").GetProperty("id").GetGuid());
        Assert.Equal([world.P1Id], seen);

        var u2 = await world.CookieAsync(world.User2);
        var both = Array(await GetAsync(u2, asked), "scopes").Select(s => s.GetProperty("scope").GetProperty("id").GetGuid());
        Assert.Equal([world.P1Id, world.P2Id, world.P2RunId], both);
    }

    [Fact]
    public async Task More_scopes_than_the_cap_a_malformed_id_or_both_parameters_is_a_400()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = await ReaderAsync(server);

        var over = string.Join(',', Enumerable.Range(0, 201).Select(_ => Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync($"/api/v1/prompts?scopes={over}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/api/v1/prompts?scopes=not-a-guid", Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reader.GetAsync($"/api/v1/prompts?scope={v1.World.ProjectId}&scopes={v1.World.ProjectId}", Ct)).StatusCode);
        Assert.Empty(Array(await GetAsync(reader, "/api/v1/prompts?scopes="), "scopes"));
    }

    /// <summary>A <see cref="BarsSeed"/> workspace and its projects in order, with <see cref="EnrichAsync"/>'s rows.</summary>
    private sealed record Seeded(BarsWorld Bars, IReadOnlyList<Guid> Projects, Guid Task);

    /// <summary>Three runs a project, so 2 projects and 14 differ only in how many there are.</summary>
    private async Task<Seeded> SeedAsync(int projects)
    {
        var bars = await BarsSeed.CreateAsync(fixture, projects * 3, projects);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await EnrichAsync(connection, bars.WorkspaceId);

        var ids = (await connection.QueryAsync<Guid>(
            "SELECT id FROM item WHERE workspace_id = @ws AND kind = 'project' ORDER BY seq;",
            new { ws = bars.WorkspaceId })).AsList();
        var task = await connection.QuerySingleAsync<Guid>(
            "SELECT id FROM item WHERE workspace_id = @ws AND ref = 'OD-1' ORDER BY seq, id LIMIT 1;",
            new { ws = bars.WorkspaceId });
        return new Seeded(bars, ids, task);
    }

    /// <summary>
    /// What <see cref="BarsSeed"/> has no row for, under every project: an open follow-up, an open question, a
    /// blocker finding, an overdue task with an unconsumed note addressed to it, an assumption past its revisit_at, a
    /// forced closure, a phase whose every task is done, and a rule scoped to the project.
    /// </summary>
    private static async Task EnrichAsync(NpgsqlConnection connection, Guid workspace)
    {
        await connection.ExecuteAsync(
            """
            CREATE TEMP TABLE first_run AS
            SELECT DISTINCT ON (r.parent_id) r.parent_id AS project, r.id AS run
            FROM   item r
            WHERE  r.workspace_id = @ws AND r.kind = 'run'
            ORDER  BY r.parent_id, r.seq;
            """,
            new { ws = workspace });

        await connection.ExecuteAsync(
            """
            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title, status, opened_at, severity)
            SELECT gen_random_uuid(), @ws, p.id, k.kind, k.ref, k.seq, 'read-loops ' || k.kind || ' of ' || p.ref,
                   'open', now() - interval '5 days', k.severity
            FROM   item p
            CROSS  JOIN (VALUES ('followup', 'FU-1', 101, NULL), ('question', 'Q-1', 102, NULL),
                                ('finding', 'BF-1', 103, 'blocker')) AS k(kind, ref, seq, severity)
            WHERE  p.workspace_id = @ws AND p.kind = 'project';

            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title, status, opened_at, due_on)
            SELECT gen_random_uuid(), @ws, f.run, 'task', 'OD-1', 104, 'read-loops overdue task', 'open',
                   now() - interval '5 days', current_date - 3
            FROM   first_run f;

            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title, status, opened_at)
            SELECT gen_random_uuid(), @ws, f.run, 'phase', 'UG', 105, 'read-loops ungated phase', 'in_progress',
                   now() - interval '5 days'
            FROM   first_run f;

            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title, status, resolution, opened_at,
                              started_at, closed_at, closed_by)
            SELECT gen_random_uuid(), @ws, g.id, 'task', 'UG.1', 1, 'read-loops gated task', 'done', 'accepted',
                   now() - interval '5 days', now() - interval '4 days', now() - interval '3 days', 'seed'
            FROM   item g
            WHERE  g.workspace_id = @ws AND g.ref = 'UG';

            INSERT INTO note (id, workspace_id, from_item_id, to_item_id, kind, body, created_at)
            SELECT gen_random_uuid(), @ws, f.project, t.id, 'decision', 'read-loops note to the overdue task',
                   now() - interval '2 days'
            FROM   first_run f
            JOIN   item t ON t.parent_id = f.run AND t.ref = 'OD-1';

            INSERT INTO assumption (id, workspace_id, scope_item_id, ref, statement, evidence, reversal, state, revisit_at)
            SELECT gen_random_uuid(), @ws, p.id, 'A1', 'read-loops premise of ' || p.ref, 'reasoned', 'medium',
                   'open', now() - interval '1 day'
            FROM   item p
            WHERE  p.workspace_id = @ws AND p.kind = 'project';

            INSERT INTO event (workspace_id, item_id, at, actor, role, kind, summary)
            SELECT @ws, t.id, now() - interval '1 day', 'seed', 'orchestrator', 'forced_transition', 'read-loops force'
            FROM   first_run f
            JOIN   item t ON t.ancestors @> ARRAY[f.run] AND t.ref = '1.3';

            INSERT INTO constraint_rule (id, workspace_id, scope_item_id, ref, kind, text, route)
            SELECT gen_random_uuid(), @ws, p.id, 'RL' || row_number() OVER (ORDER BY p.seq), 'red_line',
                   'read-loops rule', 'ask the controller'
            FROM   item p
            WHERE  p.workspace_id = @ws AND p.kind = 'project';

            DROP TABLE first_run;
            ANALYZE item; ANALYZE note; ANALYZE assumption; ANALYZE event;
            """,
            new { ws = workspace });
    }

    private async Task<int> CommandsAsync(Seeded world, string path)
    {
        await using var server = await StartAsync(fixture, world.Bars.Slug);
        var reader = await ReaderAsync(server);

        // The first request of a host pays for its own start-up; count the second.
        await SqlCommands.GetAsync(reader, "/api/v1/sessions");
        var counted = await SqlCommands.GetAsync(reader, path);
        SqlCommands.Log($"{path[..Math.Min(40, path.Length)]}, {world.Projects.Count} projects: {counted.Commands} commands, {counted.Elapsed.TotalMilliseconds:0} ms");
        SqlCommands.Log(SqlCommands.Summary(counted));
        return counted.Commands;
    }

    private static IReadOnlyList<string> Keyed(IEnumerable<JsonElement> rows)
        => [.. rows.Select(r => r.GetRawText()).Order(StringComparer.Ordinal)];
}
