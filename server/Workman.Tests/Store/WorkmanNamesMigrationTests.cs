using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Workman.Core.Data;
using Workman.Core.Migrations;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Store;

/// <summary>
/// WorkmanNames, over a database at server 1.2.0's last migration with live rows: the request path's role, the
/// project-access functions, the revision setting and the notify channel all move to their workman names, row-level
/// security keeps holding under the new role, and down puts the 1.x names back.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class WorkmanNamesMigrationTests(PostgresFixture fixture)
{
    /// <summary>The last migration server 1.2.0 shipped.</summary>
    private const string Release120 = "20261029120443_TaskUnderTask";

    private static readonly string[] OldFunctions =
        [ProjectAccess.HiddenFunction, ProjectAccessSeq.AccessFunction, ProjectAccessSeq.BumpFunction];

    private static readonly string[] NewFunctions =
        [WorkmanNames.HiddenFunction, WorkmanNames.AccessFunction, WorkmanNames.BumpFunction];

    [Fact]
    public async Task WorkmanNames_moves_a_120_database_to_the_workman_names_and_rls_still_holds()
    {
        var database = $"mig_{Guid.CreateVersion7():n}";
        await using (var admin = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database};");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        var world = new World();

        try
        {
            await MigrateAsync(connectionString, Release120);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(new Names(WorkmanNames.OldAppRole, OldFunctions, "workstate.actor", "'workstate'"), await NamesAsync(connection, WorkmanNames.OldAppRole));
                await world.SeedAsync(connection);
            }

            await MigrateAsync(connectionString, null);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(new Names(WorkmanConnections.AppRole, NewFunctions, "workman.actor", "'workman'"), await NamesAsync(connection, WorkmanConnections.AppRole));
                Assert.False(await GrantsAnythingAsync(connection, WorkmanNames.OldAppRole));
                Assert.Equal(
                    ["scp:workman.read", "scp:workman.sessions", "scp:workstate.read", "scp:workstate.sessions"],
                    (await connection.QuerySingleAsync<string>(
                        "SELECT string_agg(p, ' ' ORDER BY p) FROM openiddict_application, jsonb_array_elements_text(permissions::jsonb) p WHERE client_id = 'workman-names-client' AND p LIKE 'scp:work%';"))
                    .Split(' '));
            }

            await AssertRlsAsync(connectionString, world);

            await MigrateAsync(connectionString, Release120);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(new Names(WorkmanNames.OldAppRole, OldFunctions, "workstate.actor", "'workstate'"), await NamesAsync(connection, WorkmanNames.OldAppRole));
                Assert.False(await GrantsAnythingAsync(connection, WorkmanConnections.AppRole));
            }

            await MigrateAsync(connectionString, null);
            await AssertRlsAsync(connectionString, world);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    /// <summary>
    /// The upgrade as the live install takes it: a 1.2.0 database, rows and OAuth keys included, and the 2.0 API
    /// started on it, which migrates it itself. The keys a 1.2.0 server stored are unreadable under 2.0's
    /// data-protection names; the API replaces them rather than failing every request, and answers.
    /// </summary>
    [Fact]
    public async Task WorkmanNames_the_20_api_starts_on_a_120_database_and_answers_with_the_same_rows()
    {
        var database = $"mig_{Guid.CreateVersion7():n}";
        await using (var admin = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database};");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        var world = new World();

        try
        {
            await MigrateAsync(connectionString, Release120);
            long items;
            string[] staleKeys;
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await world.SeedAsync(connection);

                // What a 1.2.0 server left: its keys, protected under the names 2.0 no longer uses. Any payload
                // the 2.0 ring did not protect fails the same way.
                staleKeys = ["signing-1x", "encryption-1x"];
                await connection.ExecuteAsync(
                    """
                    INSERT INTO oauth_key (purpose, key_id, protected_key)
                    VALUES ('signing', 'signing-1x', @stale), ('encryption', 'encryption-1x', @stale);
                    """,
                    new { stale = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(256)) });
                items = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM item;");
            }

            await using (var server = await App.AppServer.StartAsync(connectionString, world.SlugA, Ct))
            {
                var anonymous = server.NewClient();
                Assert.Equal(System.Net.HttpStatusCode.OK, (await anonymous.GetAsync("/health", Ct)).StatusCode);
                Assert.Equal(System.Net.HttpStatusCode.OK, (await anonymous.GetAsync("/.well-known/oauth-authorization-server", Ct)).StatusCode);

                var controller = server.NewClient();
                controller.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Read.ReadServer.ControllerToken);
                var overview = await controller.GetAsync("/api/v1/overview", Ct);
                Assert.Equal(System.Net.HttpStatusCode.OK, overview.StatusCode);
                var body = await overview.Content.ReadAsStringAsync(Ct);
                Assert.Contains("Mine", body, StringComparison.Ordinal);
                Assert.Contains("Theirs", body, StringComparison.Ordinal);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(items, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM item;"));
                Assert.Equal(new Names(WorkmanConnections.AppRole, NewFunctions, "workman.actor", "'workman'"), await NamesAsync(connection, WorkmanConnections.AppRole));
                var keys = (await connection.QueryAsync<string>("SELECT key_id FROM oauth_key;")).ToArray();
                Assert.Equal(2, keys.Length);
                Assert.Empty(keys.Intersect(staleKeys));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    /// <summary>
    /// The request path on the migrated database, as the API runs it: tenant A sees its own rows only, and its user
    /// does not see the project they are no member of, through the renamed access function and the policies that
    /// hold the renamed hidden-projects function. A doc block revised there records its revision.
    /// </summary>
    private static async Task AssertRlsAsync(string connectionString, World world)
    {
        await using var source = WorkmanDataSource.Create(connectionString);
        var connections = new WorkmanConnections(source);

        using (TenantScope.Enter(world.A))
        await using (var tenant = await connections.TenantAsync(Ct))
        {
            Assert.Equal(WorkmanConnections.AppRole, await tenant.Connection.ExecuteScalarAsync<string>("SELECT current_user::text;"));
            Assert.Equal(
                ["mine", "theirs"],
                (await tenant.Connection.QueryAsync<string>("SELECT ref FROM item ORDER BY ref;")).ToArray());
            Assert.Equal(0, await tenant.Connection.ExecuteScalarAsync<long>("SELECT count(*) FROM item WHERE workspace_id = @b;", new { b = world.B }));
        }

        using (TenantScope.EnterAs(world.A, world.User))
        await using (var tenant = await connections.TenantAsync(Ct))
        {
            Assert.Equal(["mine"], (await tenant.Connection.QueryAsync<string>("SELECT ref FROM item ORDER BY ref;")).ToArray());
            await tenant.Connection.ExecuteAsync("UPDATE doc_block SET body = body || '+' WHERE id = @block;", new { block = world.Block });
            await tenant.CommitAsync(Ct);
        }

        using (TenantScope.Enter(world.B))
        await using (var tenant = await connections.TenantAsync(Ct))
        {
            Assert.Equal(["elsewhere"], (await tenant.Connection.QueryAsync<string>("SELECT ref FROM item ORDER BY ref;")).ToArray());
        }

        await using var owner = new NpgsqlConnection(connectionString);
        Assert.True(await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM doc_block_revision WHERE block_id = @block;", new { block = world.Block }) > 0);
    }

    /// <summary>The names the database uses: the role the request path's tables are granted to, and the rest.</summary>
    private static async Task<Names> NamesAsync(NpgsqlConnection connection, string role)
    {
        Assert.True(await GrantsAnythingAsync(connection, role), $"{role} holds no grant in this database");
        Assert.True(await connection.ExecuteScalarAsync<bool>(
            "SELECT NOT has_table_privilege(@role, 'data_protection_key', 'SELECT');", new { role }));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM pg_default_acl d JOIN pg_namespace n ON n.oid = d.defaclnamespace
            WHERE n.nspname = 'public' AND d.defaclacl::text LIKE '%' || @role || '=%';
            """,
            new { role }));

        var functions = (await connection.QueryAsync<string>(
            "SELECT proname::text FROM pg_proc WHERE proname LIKE 'work%' ORDER BY proname;")).ToArray();
        var revise = await connection.ExecuteScalarAsync<string>("SELECT prosrc FROM pg_proc WHERE proname = 'doc_block_revise';");
        var notify = await connection.ExecuteScalarAsync<string>("SELECT prosrc FROM pg_proc WHERE proname = 'event_notify';");

        return new Names(
            role,
            [.. functions.Order()],
            revise!.Contains("current_setting('workman.actor'", StringComparison.Ordinal) ? "workman.actor"
                : revise.Contains("current_setting('workstate.actor'", StringComparison.Ordinal) ? "workstate.actor" : revise,
            notify!.Contains("pg_notify('workman'", StringComparison.Ordinal) ? "'workman'"
                : notify.Contains("pg_notify('workstate'", StringComparison.Ordinal) ? "'workstate'" : notify);
    }

    private static Task<bool> GrantsAnythingAsync(NpgsqlConnection connection, string role)
        => connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)
               AND has_table_privilege(@role, 'item', 'SELECT')
               AND has_table_privilege(@role, 'project_member', 'SELECT');
            """,
            new { role });

    private static async Task MigrateAsync(string connectionString, string? target)
    {
        await using var context = new WorkmanDbContext(WorkmanDbContextOptions.Build(connectionString));
        if (target is null)
        {
            await context.Database.MigrateAsync(Ct);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
        }
        else
        {
            await context.GetService<IMigrator>().MigrateAsync(target, Ct);
        }
    }

    private sealed record Names(string Role, string[] Functions, string ActorSetting, string Channel)
    {
        public bool Equals(Names? other)
            => other is not null && Role == other.Role && Functions.Order().SequenceEqual(other.Functions.Order())
               && ActorSetting == other.ActorSetting && Channel == other.Channel;

        public override int GetHashCode() => HashCode.Combine(Role, ActorSetting, Channel);

        public override string ToString() => $"{Role} [{string.Join(", ", Functions)}] {ActorSetting} {Channel}";
    }

    /// <summary>Two workspaces; in A, a project the user is a member of and one only someone else is.</summary>
    private sealed class World
    {
        public Guid A { get; } = Guid.CreateVersion7();

        public Guid B { get; } = Guid.CreateVersion7();

        public Guid User { get; } = Guid.CreateVersion7();

        public Guid Block { get; } = Guid.CreateVersion7();

        public string SlugA => $"names-a-{A:n}";

        public Task SeedAsync(NpgsqlConnection connection)
        {
            var (other, mine, theirs, doc) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            return connection.ExecuteAsync(
                """
                INSERT INTO workspace (id, slug) VALUES (@a, @slugA), (@b, @slugB);
                INSERT INTO app_user (id, name, password_hash) VALUES (@user, @userName, 'x'), (@other, @otherName, 'x');
                INSERT INTO tenant_membership (user_id, workspace_id, role) VALUES (@user, @a, 'member'), (@other, @a, 'member');
                INSERT INTO item (id, workspace_id, kind, ref, seq, title)
                VALUES (@mine, @a, 'project', 'mine', 1, 'Mine'), (@theirs, @a, 'project', 'theirs', 2, 'Theirs'),
                       (gen_random_uuid(), @b, 'project', 'elsewhere', 1, 'Elsewhere');
                INSERT INTO project_member (project_item_id, user_id, workspace_id) VALUES (@mine, @user, @a), (@theirs, @other, @a);
                INSERT INTO doc (id, workspace_id, item_id, slug, title) VALUES (@doc, @a, @mine, 'names', 'names');
                INSERT INTO doc_block (id, doc_id, seq, short_id, body) VALUES (@block, @doc, 1, 'b1', 'v1');
                INSERT INTO openiddict_application (id, client_id, client_type, permissions)
                VALUES (gen_random_uuid()::text, 'workman-names-client', 'public',
                        '["ept:authorization","ept:token","scp:mcp","scp:workstate.read","scp:workstate.sessions"]');
                """,
                new
                {
                    a = A, b = B, user = User, other, mine, theirs, doc, block = Block,
                    slugA = SlugA, slugB = $"names-b-{B:n}", userName = $"names-u-{User:n}", otherName = $"names-o-{other:n}",
                });
        }
    }
}
