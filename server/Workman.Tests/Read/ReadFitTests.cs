using System.Text.RegularExpressions;
using Dapper;
using ModelContextProtocol.Client;
using Workman.Core;
using Workman.Core.Board;
using Workman.Core.Domain;
using Workman.Mcp.Tools;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Read;

/// <summary>
/// mcp-fit/2. Four reads that could not be read whole: work_history had no times and no way past its
/// newest 100 events, work_search stopped at 50 hits whatever the limit, work_threads printed one
/// scope's every thread in one block the client refused, and no output named the server's release.
/// Every cursor a result prints is followed here, because a cursor that does not lead on is a dead
/// end printed as if it were a way forward.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class ReadFitTests(PostgresFixture fixture)
{
    private const int PageLimit = 20_000;

    [Fact]
    public async Task Work_history_prints_each_event_time_and_its_older_cursor_reaches_the_first_event()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        const int seeded = 130;
        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO event (workspace_id, item_id, at, actor, role, kind, summary)
                SELECT @ws, @task, timestamptz '2026-11-20 10:00:00+00' + make_interval(mins => g),
                       'seed', 'orchestrator', 'probe', 'probe event ' || g
                FROM   generate_series(0, @last) g
                ORDER  BY g;
                """,
                new { ws = world.WorkspaceId, task = world.TaskId, last = seeded - 1 });
        }

        long[] all;
        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            all = [.. await connection.QueryAsync<long>(
                "SELECT seq FROM event WHERE workspace_id = @ws ORDER BY seq DESC;", new { ws = world.WorkspaceId })];
        }

        Assert.True(all.Length >= seeded);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var seen = new List<long>();
        var args = new Dictionary<string, object?> { ["limit"] = 100 };
        var pages = new List<string>();
        for (var turn = 0; turn < 10; turn++)
        {
            var page = await ReadServer.CallAsync(client, "work_history", args, Ct);
            pages.Add(page);
            var rows = HistoryRow().Matches(page);
            Assert.NotEmpty(rows);
            seen.AddRange(rows.Select(r => long.Parse(r.Groups["seq"].Value)));

            if (OlderCursor().Match(page) is not { Success: true } older)
            {
                break;
            }

            args = new Dictionary<string, object?> { ["limit"] = 100, ["beforeSeq"] = long.Parse(older.Groups["seq"].Value) };
        }

        Assert.Equal(2, pages.Count);
        Assert.Equal(all, seen);
        Assert.Contains("2026-11-20 10:00Z", pages[^1], StringComparison.Ordinal);
        Assert.Contains("2026-11-20 12:09Z", pages[0], StringComparison.Ordinal);

        // Every event row carries its time; a wrapped summary line is the only row without one.
        foreach (var line in pages.SelectMany(p => p.Split('\n')).Where(l => Regex.IsMatch(l, @"^\d+\s")))
        {
            Assert.Matches(HistoryRow(), line);
        }
    }

    [Fact]
    public async Task Work_history_before_the_first_event_says_so()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var text = await world.Reader.HistoryAsync(world.WorkspaceId, null, 0, 1, 20, Ct);
        Assert.StartsWith("HISTORY  no events before seq 1.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_search_returns_up_to_200_hits_and_says_when_more_exist()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using (var db = fixture.NewContext())
        {
            db.Items.AddRange(Enumerable.Range(1, 210).Select(i =>
                Seed.NewItem(world.WorkspaceId, world.PhaseId, "task", $"w{i}", 100m + i, $"Wombat burrow {i}")));
            db.Items.AddRange(Enumerable.Range(1, 3).Select(i =>
                Seed.NewItem(world.WorkspaceId, world.PhaseId, "task", $"n{i}", 400m + i, $"Numbat nest {i}")));
            await db.SaveChangesAsync(Ct);
        }

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        async Task<string> SearchAsync(string q, int limit)
            => await ReadServer.CallAsync(
                client, "work_search", new Dictionary<string, object?> { ["q"] = q, ["limit"] = limit }, Ct);

        var capped = await SearchAsync("wombat", 300);
        Assert.StartsWith($"SEARCH \"wombat\" — {WorkReadTools.SearchCap} hits, more hits exist", capped, StringComparison.Ordinal);
        Assert.Equal(200, WombatRow().Matches(capped).Count);
        Assert.Contains("More hits exist past these 200", capped, StringComparison.Ordinal);

        var asked = await SearchAsync("wombat", 120);
        Assert.StartsWith("SEARCH \"wombat\" — 120 hits, more hits exist", asked, StringComparison.Ordinal);
        Assert.Equal(120, WombatRow().Matches(asked).Count);

        var few = await SearchAsync("numbat", 300);
        Assert.StartsWith("SEARCH \"numbat\" — 3 hits\n", few, StringComparison.Ordinal);
        Assert.DoesNotContain("more hits exist", few, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Work_threads_over_a_large_scope_prints_counts_by_kind_and_every_thread_is_reached_in_bounded_pages()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var filler = string.Join(' ', Enumerable.Repeat("the export endpoint answers within budget", 9));
        await using (var db = fixture.NewContext())
        {
            db.Assumptions.AddRange(Enumerable.Range(1, 60).Select(i => new Assumption
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = world.WorkspaceId,
                ScopeItemId = world.RunId,
                Ref = $"A{i}",
                Statement = $"premise {i}: {filler}",
                Evidence = EvidenceLevel.Reasoned,
                Reversal = ReversalCost.Medium,
                State = AssumptionState.Open,
            }));
            db.Items.AddRange(Enumerable.Range(1, 30).Select(i =>
                Seed.NewItem(world.WorkspaceId, world.PhaseId, "finding", $"F-{i:00}", 100m + i, $"finding {i}")));
            await db.SaveChangesAsync(Ct);
        }

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var summary = await ReadServer.CallAsync(
            client, "work_threads", new Dictionary<string, object?> { ["scope"] = "run-a" }, Ct);
        Assert.StartsWith("THREADS  90 unclosed under run-a — too many for one page", summary, StringComparison.Ordinal);
        Assert.True(summary.Length < PageLimit, $"summary is {summary.Length} characters");
        Assert.Matches(@"\n  assumptions\s+60  — work_threads\(scope:""run-a"", include:""assumptions""\)", summary);
        Assert.Matches(@"\n  findings\s+30  — work_threads\(scope:""run-a"", include:""findings""\)", summary);

        var assumptions = new HashSet<string>();
        var findings = new HashSet<string>();
        var pages = 0;
        foreach (Match kind in SummaryCall().Matches(summary))
        {
            var args = Args(kind.Groups["args"].Value);
            for (var turn = 0; turn < 20; turn++)
            {
                var page = await ReadServer.CallAsync(client, "work_threads", args, Ct);
                pages++;
                Assert.True(page.Length < PageLimit, $"a page is {page.Length} characters:\n{page[..200]}");
                Assert.DoesNotContain("too many for one page", page, StringComparison.Ordinal);
                foreach (var line in page.Split('\n'))
                {
                    if (Regex.Match(line, @"^(A\d+)\s+assum") is { Success: true } a)
                    {
                        Assert.True(assumptions.Add(a.Groups[1].Value), $"{a.Groups[1].Value} printed twice");
                    }

                    if (Regex.Match(line, @"^(F-\d\d)\s+find") is { Success: true } f)
                    {
                        Assert.True(findings.Add(f.Groups[1].Value), $"{f.Groups[1].Value} printed twice");
                    }
                }

                if (MoreCall().Match(page) is not { Success: true } more)
                {
                    break;
                }

                args = Args(more.Groups["args"].Value);
            }
        }

        Assert.Equal(60, assumptions.Count);
        Assert.Equal(30, findings.Count);
        Assert.True(pages > 2, "the assumptions alone should take more than one page");
    }

    [Fact]
    public async Task Work_threads_over_a_small_scope_prints_exactly_what_it_did_before()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "F-01", kind: "finding");
        await WriteSeed.ChildAsync(world, world.PhaseId, "F-02", kind: "finding");
        var scope = await world.ScopeAsync("run-a");

        var bounded = await world.Reader.ThreadsAsync(scope, [], 100, 0, Ct);

        Assert.Equal(await world.Reader.ThreadsAsync(scope, [], Ct), bounded);
        Assert.StartsWith("THREADS  2 unclosed under run-a", bounded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_brief_and_server_info_name_the_same_release_as_the_changelog()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        Assert.Equal(ServerVersion.Text, client.ServerInfo.Version);

        var brief = await ReadServer.CallAsync(
            client, "work_brief", new Dictionary<string, object?> { ["scope"] = "run-a" }, Ct);
        var lines = brief.Split('\n');
        Assert.Contains($"server   {ServerVersion.Text}", lines);

        // The line closes the header block: it comes before the first blank line.
        Assert.True(Array.IndexOf(lines, BoardReader.ServerLine) < Array.IndexOf(lines, string.Empty));

        var changelog = await File.ReadAllTextAsync(Changelog(), Ct);
        var released = Regex.Match(changelog, @"^## \[(\d+\.\d+\.\d+)\]", RegexOptions.Multiline);
        Assert.True(released.Success, "CHANGELOG.md names no released version");
        Assert.Equal(ServerVersion.Text, released.Groups[1].Value);
    }

    private static string Changelog()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "CHANGELOG.md");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("CHANGELOG.md above the test output");
    }

    private static Dictionary<string, object?> Args(string text)
        => ArgPair().Matches(text).ToDictionary(
            m => m.Groups["name"].Value,
            m => m.Groups["text"].Success ? m.Groups["text"].Value : (object?)int.Parse(m.Groups["number"].Value));

    [GeneratedRegex(@"^(?<seq>\d+)\s+\d{4}-\d\d-\d\d \d\d:\d\dZ  ", RegexOptions.Multiline)]
    private static partial Regex HistoryRow();

    [GeneratedRegex(@"^Older: work_history\(beforeSeq:(?<seq>\d+)\)", RegexOptions.Multiline)]
    private static partial Regex OlderCursor();

    [GeneratedRegex(@"^w\d+\s", RegexOptions.Multiline)]
    private static partial Regex WombatRow();

    [GeneratedRegex(@"^  \w+\s+\d+  — work_threads\((?<args>[^)]*)\)", RegexOptions.Multiline)]
    private static partial Regex SummaryCall();

    [GeneratedRegex(@"^More: work_threads\((?<args>[^)]*)\)", RegexOptions.Multiline)]
    private static partial Regex MoreCall();

    [GeneratedRegex(@"(?<name>\w+):(?:""(?<text>[^""]*)""|(?<number>\d+))")]
    private static partial Regex ArgPair();
}
