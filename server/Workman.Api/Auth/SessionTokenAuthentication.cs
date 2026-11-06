using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Workman.Core.Writing;
using ActorRole = Workman.Core.Domain.ActorRole;
using Workman.Mcp;

namespace Workman.Api.Auth;

/// <summary>
/// doc 14 §2 — a minted <c>wss_</c> session token, looked up by its hash; unknown, revoked and expired fail
/// alike, and so does one whose user may no longer enter its workspace. The principal carries the granted
/// role, nested — a controller also satisfies orchestrator and worker — the session, which binds the name to
/// the credential instead of to a header, and the user who minted it, whose further mints are theirs too.
/// </summary>
public sealed class SessionTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "WorkmanSessionToken";
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var raw = header.ToString();
        if (!raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("Authorization header must be 'Bearer <token>'.");
        }

        var presented = raw[BearerPrefix.Length..].Trim();

        if (!presented.StartsWith(SessionToken.Prefix, StringComparison.Ordinal)
            || Context.RequestServices.GetService<SessionStore>() is not { } store
            || await store.AuthenticateAsync(presented, Context.RequestAborted) is not { } session)
        {
            return AuthenticateResult.Fail("Unrecognised bearer token.");
        }

        var identity = Identity(Nested(session.Role));
        identity.AddClaim(new Claim(RequestTenant.Claim, session.WorkspaceId.ToString()));
        identity.AddClaim(new Claim(CallerSession.SessionIdClaim, session.SessionId.ToString()));
        identity.AddClaim(new Claim(CallerSession.SessionNameClaim, session.Name));
        if (session.UserId is { } user)
        {
            identity.AddClaim(new Claim(CallerSession.UserIdClaim, user.ToString()));
        }

        foreach (var grant in session.Grants)
        {
            identity.AddClaim(new Claim(CallerSession.GrantClaim, grant));
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    public static string[] Nested(ActorRole role) => role switch
    {
        ActorRole.Controller => [WorkmanPolicies.Worker, WorkmanPolicies.Orchestrator, WorkmanPolicies.Controller],
        ActorRole.Orchestrator => [WorkmanPolicies.Worker, WorkmanPolicies.Orchestrator],
        ActorRole.Worker => [WorkmanPolicies.Worker],
        _ => [],
    };

    /// <summary>The shape every agent principal has; <c>OAuthTokenHandler</c> builds its own from this too.</summary>
    internal static ClaimsIdentity Identity(string[] roles, string authenticationType = SchemeName)
    {
        var identity = new ClaimsIdentity(authenticationType);
        identity.AddClaim(new Claim(ClaimTypes.Name, roles[^1]));
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        return identity;
    }
}
