using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Workman.Core.Writing;

namespace Workman.Mcp.Tools;

/// <summary>
/// doc 11 — self-naming. Listed only where it can work: an OAuth token on a request whose session
/// helper sent <c>X-Workman-Client</c> (<see cref="WorkmanPolicies.SelfNamer"/>).
/// </summary>
[McpServerToolType]
public sealed class WorkSessionTools(
    SessionStore sessions,
    RequestTenant tenant,
    CallerIdentity caller)
{
    [Authorize(Policy = WorkmanPolicies.SelfNamer)]
    [McpServerTool(Name = "work_session", Title = "Sign: name this session")]
    [Description("""
        Signs every later call from this session with the name. Call once when work_brief says unnamed.
        """)]
    public async Task<CallToolResult> SessionAsync(
        [Description("Your name in ListAgents.")] string name,
        [Description("Optional Claude session id.")] string? claude_session_id = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Text("work_session needs a name — the one ListAgents shows for this session.", refused: true);
        }

        if (caller.ClientKey is not { } key)
        {
            return Text(SessionStore.NoKey(), refused: true);
        }

        var bound = await sessions.BindAsync(
            tenant.Id,
            caller.UserId,
            caller.Actor.Role,
            name,
            new SessionFacts(ClaudeSessionId: claude_session_id, ClientKey: key),
            cancellationToken);

        return bound.Session is { } session
            ? Text($"session {session.Name} — every call from this session is now signed {session.Name}", refused: false)
            : Text(bound.Refused!, refused: true);
    }

    private static CallToolResult Text(string body, bool refused)
        => new() { Content = [new TextContentBlock { Text = body }], IsError = refused };
}
