using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Npgsql;
using Workman.Core.Data;
using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Core.Writing;

namespace Workman.Tests.Read;

/// <summary>
/// <see cref="ReadServer.WorkerToken"/>, <see cref="ReadServer.OrchestratorToken"/> and
/// <see cref="ReadServer.ControllerToken"/> as real <c>wss_</c> session tokens. The first request on a test server
/// that presents one binds it: a session named <c>test-&lt;role&gt;</c> is minted in the server's workspace by
/// <see cref="SessionStore.MintAsync"/>, as <c>POST /api/agent-sessions</c> mints one, and only its hash is then set
/// to the fixed token's, taken off whichever session an earlier server bound it to. A token never presented makes
/// no session, so a test sees only the rows of the tokens it used. Test plumbing ahead of the host's pipeline; the
/// request is then authenticated by the server exactly as any minted token is.
/// </summary>
internal sealed class RoleTokens(string? slug)
{
    private static readonly (string Token, ActorRole Role)[] Roles =
    [
        (ReadServer.WorkerToken, ActorRole.Worker),
        (ReadServer.OrchestratorToken, ActorRole.Orchestrator),
        (ReadServer.ControllerToken, ActorRole.Controller),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _bound = [];
    private Guid? _workspace;

    public static void Add(WebApplicationBuilder builder, string? slug)
    {
        builder.Services.AddSingleton(new RoleTokens(string.IsNullOrWhiteSpace(slug) ? null : slug.Trim()));
        builder.Services.AddSingleton<IStartupFilter, Filter>();
    }

    /// <summary>The workspace the role tokens act in: the slug's, created when missing; no slug, the oldest one.</summary>
    public static Task<Guid> WorkspaceAsync(IServiceProvider services, CancellationToken cancellationToken)
        => services.GetRequiredService<RoleTokens>().ResolveAsync(services, cancellationToken);

    private async Task<Guid> ResolveAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (_workspace is { } known)
        {
            return known;
        }

        var workspace = slug is null
            && await services.GetRequiredService<BoardQueries>().WorkspaceAsync(null, cancellationToken) is { } oldest
                ? oldest
                : await services.GetRequiredService<WorkWriter>().EnsureWorkspaceAsync(slug, cancellationToken);

        _workspace = workspace;
        return workspace;
    }

    private async Task BindAsync(IServiceProvider services, string presented, CancellationToken cancellationToken)
    {
        var index = Array.FindIndex(Roles, r => r.Token == presented);
        if (index < 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_bound.Add(presented))
            {
                return;
            }

            var (token, role) = Roles[index];
            var workspace = await ResolveAsync(services, cancellationToken);

            MintResult minted;
            using (TenantScope.Enter(workspace))
            {
                minted = await services.GetRequiredService<SessionStore>().MintAsync(
                    workspace,
                    new MintRequest($"test-{role.ToString().ToLowerInvariant()}", role, SessionStore.MaxTtlHours, Replace: true, MintedBy: null),
                    cancellationToken);
            }

            if (minted.Minted is not { } session)
            {
                throw new InvalidOperationException($"binding {role}'s test token: {minted.Conflict}");
            }

            await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(
                """
                UPDATE agent_session SET token_hash = NULL WHERE token_hash = @hash;
                UPDATE agent_session SET token_hash = @hash WHERE id = @id;
                """,
                new { hash = SessionToken.Hash(token), id = session.SessionId });
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Filter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                var tokens = app.ApplicationServices.GetRequiredService<RoleTokens>();
                app.Use(async (HttpContext context, RequestDelegate inner) =>
                {
                    var header = context.Request.Headers[HeaderNames.Authorization].ToString();
                    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        await tokens.BindAsync(context.RequestServices, header["Bearer ".Length..].Trim(), context.RequestAborted);
                    }

                    await inner(context);
                });
                next(app);
            };
    }
}
