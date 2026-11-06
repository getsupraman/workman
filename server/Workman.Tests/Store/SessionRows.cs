using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Workman.Core.Domain;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Store;

/// <summary>
/// What a call did to a workspace's <c>agent_session</c> rows, as a delta: the ids before it, then the rows after.
/// A test role token is a session of its own, made on the token's first use (<c>Read.RoleTokens</c>), so
/// <see cref="BeforeAsync"/> presents each token the test is about to use before it reads the ids.
/// </summary>
internal sealed class SessionRows
{
    private readonly PostgresFixture _fixture;
    private readonly Guid _workspace;
    private readonly HashSet<Guid> _before;

    private SessionRows(PostgresFixture fixture, Guid workspace, HashSet<Guid> before)
    {
        _fixture = fixture;
        _workspace = workspace;
        _before = before;
    }

    public static async Task<SessionRows> BeforeAsync(
        PostgresFixture fixture,
        Guid workspace,
        string baseAddress,
        params string[] callers)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseAddress) };
        foreach (var token in callers)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await http.SendAsync(request, Ct)).EnsureSuccessStatusCode();
        }

        await using var db = fixture.NewContext();
        var ids = await db.AgentSessions.Where(s => s.WorkspaceId == workspace).Select(s => s.Id).ToListAsync(Ct);
        return new SessionRows(fixture, workspace, [.. ids]);
    }

    /// <summary>The rows the calls since <see cref="BeforeAsync"/> created; the rows that were there are all still there.</summary>
    public async Task<List<AgentSession>> NewAsync()
    {
        await using var db = _fixture.NewContext();
        var after = await db.AgentSessions.Where(s => s.WorkspaceId == _workspace).ToListAsync(Ct);
        Assert.Subset(after.Select(s => s.Id).ToHashSet(), _before);
        return after.Where(s => !_before.Contains(s.Id)).ToList();
    }

    public async Task AssertNoneNewAsync() => Assert.Empty(await NewAsync());

    public async Task<AgentSession> SingleNewAsync() => Assert.Single(await NewAsync());
}
