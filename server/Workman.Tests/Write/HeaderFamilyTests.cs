using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Workman.Api.App;
using Workman.Api.App.V1;
using Workman.Mcp;
using Workman.Tests.App;
using Workman.Tests.Read;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Write;

/// <summary>
/// Server 2.0 closed the rename's compatibility window: every request header is read under its <c>X-Workman-*</c>
/// name only, a request that still sends the 1.x <c>X-WorkState-*</c> one is treated as sending none, and the
/// header the server sends goes out under the new name alone.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class HeaderFamilyTests(PostgresFixture fixture)
{
    private const string OldPrefix = "X-WorkState-";

    public enum Family
    {
        /// <summary>The 1.x name only, which nothing reads any more.</summary>
        Old,
        Workman,
    }

    public static TheoryData<string, Family> EveryHeaderInEveryFamily()
    {
        var data = new TheoryData<string, Family>();
        foreach (var header in WorkmanHeaders.All)
        {
            foreach (var family in Enum.GetValues<Family>())
            {
                data.Add(header, family);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryHeaderInEveryFamily))]
    public void The_helper_reads_each_header_under_its_workman_name_only(string header, Family family)
    {
        var headers = new HeaderDictionary();
        Put(headers, header, family, "value-1");

        var expected = family is Family.Workman ? "value-1" : null;
        Assert.Equal(expected ?? string.Empty, WorkmanHeaders.Read(headers, header).ToString());
        Assert.Equal(expected, WorkmanHeaders.Text(headers, header));
    }

    [Fact]
    public void Every_header_is_an_x_workman_one()
    {
        Assert.Equal(
            ["X-Workman-Agent", "X-Workman-Session", "X-Workman-Launched-By", "X-Workman-Origin", "X-Workman-Cwd",
             "X-Workman-Foreman-Role", "X-Workman-Client", "X-Workman-Write", "X-Workman-Api"],
            WorkmanHeaders.All);
        Assert.Null(WorkmanHeaders.Text(new HeaderDictionary(), WorkmanHeaders.Agent));
    }

    /// <summary>Agent, Session, Launched-By, Origin, Cwd and Foreman-Role, over the real transport.</summary>
    [Theory]
    [InlineData(Family.Old)]
    [InlineData(Family.Workman)]
    public async Task The_registration_headers_fill_the_session_row_under_the_workman_names_only(Family family)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        var headers = new Dictionary<string, string>();
        Put(headers, WorkmanHeaders.Agent, family, "aw-family");
        Put(headers, WorkmanHeaders.Session, family, "claude-family");
        Put(headers, WorkmanHeaders.LaunchedBy, family, "foreman");
        Put(headers, WorkmanHeaders.Origin, family, "foreman/x/X");
        Put(headers, WorkmanHeaders.Cwd, family, "P:/worktrees/family");
        Put(headers, WorkmanHeaders.ForemanRole, family, "repo");

        await using (var client = await server.ConnectAsync(await server.OAuthTokenAsync(Ct), Ct, headers))
        {
            await ReadServer.CallAsync(client, "work_brief", null, Ct);
        }

        await using var db = fixture.NewContext();
        var sessions = await db.AgentSessions.Where(s => s.WorkspaceId == world.WorkspaceId).ToListAsync(Ct);
        if (family is Family.Old)
        {
            Assert.DoesNotContain(sessions, s => s.Name == "aw-family" || s.ClaudeSessionId == "claude-family"
                || s.LaunchedBy == "foreman" || s.Origin == "foreman/x/X" || s.Cwd == "P:/worktrees/family" || s.ExternalRole == "repo");
            return;
        }

        var session = Assert.Single(sessions);
        Assert.Equal("aw-family", session.Name);
        Assert.Equal("claude-family", session.ClaudeSessionId);
        Assert.Equal("foreman", session.LaunchedBy);
        Assert.Equal("foreman/x/X", session.Origin);
        Assert.Equal("P:/worktrees/family", session.Cwd);
        Assert.Equal("repo", session.ExternalRole);
    }

    /// <summary>Client: what lists work_session (an OAuth token) and what work_session binds.</summary>
    [Theory]
    [InlineData(Family.Old)]
    [InlineData(Family.Workman)]
    public async Task The_client_key_is_read_under_the_workman_name_only(Family family)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var key = $"host/{Random.Shared.Next(1000, 99999)}/{Guid.NewGuid():n}";

        var headers = new Dictionary<string, string>();
        Put(headers, WorkmanHeaders.Client, family, key);

        await using (var client = await server.ConnectAsync(await server.OAuthTokenAsync(Ct), Ct, headers))
        {
            var listed = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
            if (family is Family.Workman)
            {
                Assert.Contains("work_session", listed);
                await ReadServer.CallAsync(client, "work_session", new Dictionary<string, object?> { ["name"] = "skills-family" }, Ct);
            }
            else if (listed.Contains("work_session"))
            {
                await ReadServer.CallEitherAsync(client, "work_session", new Dictionary<string, object?> { ["name"] = "skills-family" }, Ct);
            }
        }

        await using var db = fixture.NewContext();
        if (family is Family.Old)
        {
            Assert.False(await db.AgentSessions.AnyAsync(s => s.WorkspaceId == world.WorkspaceId && s.ClientKey == key, Ct));
            return;
        }

        var session = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("skills-family", session.Name);
        Assert.Equal(key, session.ClientKey);
    }

    /// <summary>Write: the dashboard's guard admits the workman name, and refuses the old one as it refuses none.</summary>
    [Theory]
    [InlineData(Family.Old)]
    [InlineData(Family.Workman)]
    public async Task The_write_guard_takes_the_workman_name_only(Family family)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using (var agent = await server.ConnectAsync(
                         await server.OAuthTokenAsync(Ct), Ct, new Dictionary<string, string> { [WorkmanHeaders.Agent] = "aw-release" }))
        {
            await ReadServer.CallAsync(agent, "work_brief", null, Ct);
        }

        Guid id;
        await using (var db = fixture.NewContext())
        {
            id = (await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct)).Id;
        }

        var cookie = await server.SignInAsync(Ct);

        using (var bare = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{id}/release"))
        {
            var refused = await cookie.SendAsync(bare, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains(ProjectEndpoints.WriteHeader, await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using var guarded = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{id}/release");
        guarded.Headers.Add(family is Family.Workman ? WorkmanHeaders.Write : Old(WorkmanHeaders.Write), "1");

        Assert.Equal(
            family is Family.Workman ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            (await cookie.SendAsync(guarded, Ct)).StatusCode);
    }

    /// <summary>Api, the one header the server sends: the workman name only, on a v1 answer and on the stream.</summary>
    [Fact]
    public async Task The_api_header_goes_out_under_the_workman_name_only_on_v1_and_on_the_stream()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var cookie = await server.SignInAsync(Ct);

        var overview = await cookie.GetAsync("/api/v1/overview", Ct);
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        AssertWorkmanOnly(overview);

        using var open = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        open.CancelAfter(TimeSpan.FromMinutes(1));
        var stream = await cookie.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, open.Token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        AssertWorkmanOnly(stream);

        static void AssertWorkmanOnly(HttpResponseMessage response)
        {
            Assert.Equal("1", Assert.Single(response.Headers.GetValues("X-Workman-Api")));
            Assert.False(response.Headers.Contains(Old(ApiV1.Header)));
            Assert.Equal("X-Workman-Api", ApiV1.Header);
        }
    }

    private static void Put(IDictionary<string, string> headers, string header, Family family, string value)
    {
        headers[family is Family.Workman ? header : Old(header)] = value;
    }

    /// <summary><c>X-Workman-Agent</c> becomes <c>X-WorkState-Agent</c>, the name 1.x also read.</summary>
    private static string Old(string header) => OldPrefix + header["X-Workman-".Length..];

    private static void Put(IHeaderDictionary headers, string header, Family family, string value)
    {
        var plain = new Dictionary<string, string>();
        Put(plain, header, family, value);
        foreach (var (name, text) in plain)
        {
            headers[name] = text;
        }
    }
}
