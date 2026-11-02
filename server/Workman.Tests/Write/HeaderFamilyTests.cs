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
/// The rename's compatibility window: every request header is read under its <c>X-Workman-*</c> name and,
/// until server 2.0, under its old <c>X-WorkState-*</c> one, the new name winning when both arrive; the
/// header the server sends goes out under both.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class HeaderFamilyTests(PostgresFixture fixture)
{
    public enum Family
    {
        Legacy,
        Workman,

        /// <summary>Both names, the old one carrying a decoy the new one must beat.</summary>
        Both,
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
    public void The_helper_reads_each_header_under_either_name_and_prefers_the_new_one(string header, Family family)
    {
        var headers = new HeaderDictionary();
        Put(headers, header, family, "value-1");

        Assert.Equal("value-1", WorkmanHeaders.Read(headers, header).ToString());
        Assert.Equal("value-1", WorkmanHeaders.Text(headers, header));
    }

    [Fact]
    public void Each_header_has_its_old_name_and_only_the_two_prefixes_exist()
    {
        Assert.Equal(
            ["X-WorkState-Agent", "X-WorkState-Session", "X-WorkState-Launched-By", "X-WorkState-Origin", "X-WorkState-Cwd",
             "X-WorkState-Foreman-Role", "X-WorkState-Client", "X-WorkState-Write", "X-WorkState-Api"],
            WorkmanHeaders.All.Select(WorkmanHeaders.Legacy));
        Assert.All(WorkmanHeaders.All, header => Assert.StartsWith("X-Workman-", header, StringComparison.Ordinal));
        Assert.Null(WorkmanHeaders.Text(new HeaderDictionary(), WorkmanHeaders.Agent));
    }

    /// <summary>Agent, Session, Launched-By, Origin, Cwd and Foreman-Role, over the real transport.</summary>
    [Theory]
    [InlineData(Family.Legacy)]
    [InlineData(Family.Workman)]
    [InlineData(Family.Both)]
    public async Task The_registration_headers_fill_the_session_row_under_either_name(Family family)
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

        await using (var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct, headers))
        {
            await ReadServer.CallAsync(client, "work_brief", null, Ct);
        }

        await using var db = fixture.NewContext();
        var session = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("aw-family", session.Name);
        Assert.Equal("claude-family", session.ClaudeSessionId);
        Assert.Equal("foreman", session.LaunchedBy);
        Assert.Equal("foreman/x/X", session.Origin);
        Assert.Equal("P:/worktrees/family", session.Cwd);
        Assert.Equal("repo", session.ExternalRole);
    }

    /// <summary>Client: what lists work_session (the static scheme) and what work_session binds.</summary>
    [Theory]
    [InlineData(Family.Legacy)]
    [InlineData(Family.Workman)]
    [InlineData(Family.Both)]
    public async Task The_client_key_is_read_under_either_name(Family family)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var key = $"host/{Random.Shared.Next(1000, 99999)}/{Guid.NewGuid():n}";

        var headers = new Dictionary<string, string>();
        Put(headers, WorkmanHeaders.Client, family, key);

        await using (var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct, headers))
        {
            Assert.Contains("work_session", (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name));
            await ReadServer.CallAsync(client, "work_session", new Dictionary<string, object?> { ["name"] = "skills-family" }, Ct);
        }

        await using var db = fixture.NewContext();
        var session = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("skills-family", session.Name);
        Assert.Equal(key, session.ClientKey);
    }

    /// <summary>Write: the dashboard's guard admits either name and still refuses neither.</summary>
    [Theory]
    [InlineData(Family.Legacy)]
    [InlineData(Family.Workman)]
    [InlineData(Family.Both)]
    public async Task The_write_guard_takes_either_name(Family family)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using (var agent = await server.ConnectAsync(
                         ReadServer.WorkerToken, Ct, new Dictionary<string, string> { [WorkmanHeaders.Agent] = "aw-release" }))
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
        if (family is Family.Legacy or Family.Both)
        {
            guarded.Headers.Add(WorkmanHeaders.Legacy(WorkmanHeaders.Write), "1");
        }

        if (family is Family.Workman or Family.Both)
        {
            guarded.Headers.Add(WorkmanHeaders.Write, "1");
        }

        Assert.Equal(HttpStatusCode.OK, (await cookie.SendAsync(guarded, Ct)).StatusCode);
    }

    /// <summary>Api, the one header the server sends: both names on a v1 answer and on the stream (Supraman reads the old one).</summary>
    [Fact]
    public async Task The_api_header_goes_out_under_both_names_on_v1_and_on_the_stream()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var cookie = await server.SignInAsync(Ct);

        var overview = await cookie.GetAsync("/api/v1/overview", Ct);
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        AssertBoth(overview);

        using var open = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        open.CancelAfter(TimeSpan.FromMinutes(1));
        var stream = await cookie.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, open.Token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        AssertBoth(stream);

        static void AssertBoth(HttpResponseMessage response)
        {
            Assert.Equal("1", Assert.Single(response.Headers.GetValues("X-Workman-Api")));
            Assert.Equal("1", Assert.Single(response.Headers.GetValues("X-WorkState-Api")));
            Assert.Equal(ApiV1.Header, "X-Workman-Api");
            Assert.Equal("X-WorkState-Api", ApiV1.LegacyHeader);
        }
    }

    private static void Put(IDictionary<string, string> headers, string header, Family family, string value)
    {
        switch (family)
        {
            case Family.Legacy:
                headers[WorkmanHeaders.Legacy(header)] = value;
                break;
            case Family.Workman:
                headers[header] = value;
                break;
            default:
                headers[WorkmanHeaders.Legacy(header)] = "decoy-" + value;
                headers[header] = value;
                break;
        }
    }

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
