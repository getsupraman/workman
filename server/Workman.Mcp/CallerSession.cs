using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Workman.Core.Domain;
using Workman.Core.Writing;

namespace Workman.Mcp;

/// <summary>
/// doc 11 — resolves which session a call comes from, once per call and before the tool runs, and
/// leaves it on the request for <see cref="CallerIdentity"/>. Resolving touches the row and renews
/// the session's leases, which is why it runs for every tool call and resource read, not only
/// writes. The source is the credential when it is a minted token (doc 14 §2), else the headers:
/// the transport is stateless, so neither a connection id nor a declaration made on one call
/// survives to the next. Order: a minted token's session, ignoring both headers; else
/// <see cref="AgentHeader"/>, binding <see cref="ClientHeader"/> to it when both are sent; else the
/// session <c>work_session</c> bound the client key to; else none.
/// </summary>
public static class CallerSession
{
    internal const string ItemKey = "workman.session";

    // Each read through WorkmanHeaders.
    public const string AgentHeader = WorkmanHeaders.Agent;
    public const string SessionHeader = WorkmanHeaders.Session;
    public const string LaunchedByHeader = WorkmanHeaders.LaunchedBy;
    public const string OriginHeader = WorkmanHeaders.Origin;
    public const string CwdHeader = WorkmanHeaders.Cwd;
    public const string ForemanRoleHeader = WorkmanHeaders.ForemanRole;

    /// <summary>The session helper's key for the Claude Code process: host/pid/start time.</summary>
    public const string ClientHeader = WorkmanHeaders.Client;

    /// <summary>
    /// <see cref="ClientHeader"/>, on the principal of a static agent token only — a minted token's
    /// session is its credential's, so its key is never read.
    /// </summary>
    public const string ClientKeyClaim = "workman:client-key";

    /// <summary>doc 14 §2 — on the principal of a minted token, and only there.</summary>
    public const string SessionIdClaim = "workman:session-id";

    public const string SessionNameClaim = "workman:session-name";

    /// <summary>One per grant the minted token carries; a static token never has one.</summary>
    public const string GrantClaim = "workman:grant";

    /// <summary>
    /// The <c>app_user</c> id behind an OAuth token, or behind a minted token a user minted. A static token has
    /// none, and its sessions share the workspace's names; a user's are theirs alone.
    /// </summary>
    public const string UserIdClaim = "workman:user-id";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (context, cancellationToken) =>
        {
            var refused = await ResolveAsync(context, cancellationToken);

            return refused is null
                ? await next(context, cancellationToken)
                : new CallToolResult { Content = [new TextContentBlock { Text = refused }], IsError = true };
        };

    public static McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> ResourceFilter(
        McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> next)
        => async (context, cancellationToken) =>
        {
            await ResolveAsync(context, cancellationToken);
            return await next(context, cancellationToken);
        };

    private static async Task<string?> ResolveAsync(MessageContext context, CancellationToken cancellationToken)
    {
        var services = context.Services;
        var http = services?.GetService<IHttpContextAccessor>()?.HttpContext;
        var store = services?.GetService<SessionStore>();
        var caller = services?.GetService<CallerIdentity>();

        if (http is null || store is null || caller is null)
        {
            return null;
        }

        var headers = http.Request.Headers;
        string? name = WorkmanHeaders.Read(headers, AgentHeader);
        var role = caller.Actor.Role;

        if (role == ActorRole.System)
        {
            return null;
        }

        // A minted token names its own session; the Agent header and the launcher rule do not apply.
        if (Guid.TryParse(http.User.FindFirst(SessionIdClaim)?.Value, out var bound))
        {
            var facts = new SessionFacts(
                ClaudeSessionId: WorkmanHeaders.Read(headers, SessionHeader),
                Origin: WorkmanHeaders.Read(headers, OriginHeader),
                Cwd: WorkmanHeaders.Read(headers, CwdHeader),
                ExternalRole: WorkmanHeaders.Read(headers, ForemanRoleHeader),
                ClientName: context.Server.ClientInfo?.Name,
                ClientVersion: context.Server.ClientInfo?.Version);

            if (await store.ResolveBoundAsync(bound, role, facts, cancellationToken) is { } own)
            {
                http.Items[ItemKey] = own;
            }

            return null;
        }

        var key = http.User.FindFirst(ClientKeyClaim)?.Value;
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (RequestTenant.Of(http.User) is not { } workspaceId)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var keyed = new SessionFacts(
                ClaudeSessionId: WorkmanHeaders.Read(headers, SessionHeader),
                Origin: WorkmanHeaders.Read(headers, OriginHeader),
                Cwd: WorkmanHeaders.Read(headers, CwdHeader),
                ExternalRole: WorkmanHeaders.Read(headers, ForemanRoleHeader),
                ClientName: context.Server.ClientInfo?.Name,
                ClientVersion: context.Server.ClientInfo?.Version,
                ClientKey: key);

            if (await store.ResolveByKeyAsync(workspaceId, caller.UserId, role, keyed, cancellationToken) is { } byKey)
            {
                http.Items[ItemKey] = byKey;
            }

            return null;
        }

        var named = new SessionFacts(
            Name: name,
            ClaudeSessionId: WorkmanHeaders.Read(headers, SessionHeader),
            LaunchedBy: WorkmanHeaders.Read(headers, LaunchedByHeader),
            Origin: WorkmanHeaders.Read(headers, OriginHeader),
            Cwd: WorkmanHeaders.Read(headers, CwdHeader),
            ExternalRole: WorkmanHeaders.Read(headers, ForemanRoleHeader),
            ClientName: context.Server.ClientInfo?.Name,
            ClientVersion: context.Server.ClientInfo?.Version,
            ClientKey: key);

        var resolved = await store.ResolveAsync(workspaceId, caller.UserId, role, named, cancellationToken);
        if (resolved.Session is { } session)
        {
            http.Items[ItemKey] = session;
        }

        return resolved.Refused;
    }
}
