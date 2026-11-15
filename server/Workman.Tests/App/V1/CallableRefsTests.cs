using Dapper;
using Npgsql;
using Workman.Core.Queries;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App.V1;

/// <summary>
/// <see cref="BoardQueries.CallableRefsAsync"/> is <see cref="BoardQueries.CallableRefAsync"/> for many items at once:
/// item by item the same string, over refs that collide across runs and projects, runs that number their own refs,
/// and projects.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class CallableRefsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_batch_prints_what_the_single_item_read_prints_for_every_item()
    {
        var world = await BarsSeed.CreateAsync(fixture, 24);
        var board = new BoardQueries(fixture.DataSource);
        var api = new ApiQueries(fixture.DataSource);

        List<Guid> ids;
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            ids = [.. await connection.QueryAsync<Guid>("SELECT id FROM item WHERE workspace_id = @ws;", new { ws = world.WorkspaceId })];
        }

        var rows = await api.RefsAsync(ids, Ct);
        Assert.Equal(world.Items, rows.Count);
        Assert.Contains(rows, r => r.RefShared && r.Kind == "run");
        Assert.Contains(rows, r => r.RefShared && r.Kind == "task");

        var batch = await board.CallableRefsAsync([.. rows.Select(r => (r.Scope, r.RefShared))], Ct);

        var forms = new HashSet<string>();
        foreach (var row in rows)
        {
            var single = await board.CallableRefAsync(row.Scope, row.RefShared, Ct);
            Assert.True(single == batch[row.Id], $"{row.Kind} {row.Ref}: one at a time '{single}', batched '{batch[row.Id]}'");
            forms.Add(single.Count(c => c == '/').ToString());
        }

        // The comparison covered bare, run-qualified and project-qualified forms.
        Assert.Equal(["0", "1", "2"], forms.Order());
    }

    [Fact]
    public async Task No_items_is_no_command_and_an_empty_answer()
        => Assert.Empty(await new BoardQueries(fixture.DataSource).CallableRefsAsync([], Ct));
}
