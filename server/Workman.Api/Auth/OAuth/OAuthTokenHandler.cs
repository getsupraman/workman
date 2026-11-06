using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Workman.Core.Writing;
using Workman.Mcp;
using ActorRole = Workman.Core.Domain.ActorRole;

namespace Workman.Api.Auth.OAuth;

/// <summary>
/// <c>/mcp</c>'s second credential, beside <see cref="SessionTokenHandler"/>'s, and the one that answers
/// an anonymous request with RFC 9728's challenge. The read API and the agent-session group take it too,
/// with <see cref="OAuthScopeRequirement"/> on top.
/// <para>
/// It touches only a JWT-shaped bearer — dotted, and not a <c>wss_</c> session token — so a
/// minted token is judged by the handler that always judged it and nothing else (C31). An access token
/// OpenIddict accepts becomes a controller principal of the shape a minted token's has, so
/// <see cref="CallerIdentity"/>, <see cref="CallerSession"/> and the tool policies see no difference.
/// </para>
/// </summary>
public sealed class OAuthTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAuthenticationSchemeProvider schemes)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "WorkmanOAuth";

    /// <summary>One claim per granted scope, read by <see cref="OAuthScopeRequirement"/>.</summary>
    public const string ScopeClaim = "workman_scope";

    /// <summary>The OpenIddict authorization the token was issued under; a mint records it (doc 14 §2).</summary>
    public const string AuthorizationClaim = "workman_authorization";

    /// <summary>The token's user's name, as a mint records it in <c>minted_by_subject</c>.</summary>
    public const string UserNameClaim = "workman_user_name";

    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Presented() is not { } token || !IsJwt(token) || !await ServesOAuthAsync())
        {
            return AuthenticateResult.NoResult();
        }

        var validated = await Context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (validated is not { Succeeded: true, Principal: { } principal })
        {
            return AuthenticateResult.Fail(validated.Failure ?? new InvalidOperationException("Unrecognised bearer token."));
        }

        // Every token here is issued with this role; the user and the tenant behind it are re-read below.
        if (!principal.HasClaim(OpenIddictConstants.Claims.Role, WorkmanPolicies.Controller))
        {
            return AuthenticateResult.Fail("The token carries no role this server issues.");
        }

        var identity = SessionTokenHandler.Identity(SessionTokenHandler.Nested(ActorRole.Controller), SchemeName);
        await UserAsync(identity, principal.GetClaim(OpenIddictConstants.Claims.Subject) ?? string.Empty, principal);
        foreach (var scope in principal.GetScopes())
        {
            identity.AddClaim(new Claim(ScopeClaim, scope));
        }

        if (principal.GetAuthorizationId() is { Length: > 0 } authorization)
        {
            identity.AddClaim(new Claim(AuthorizationClaim, authorization));
        }

        if (WorkmanHeaders.Text(Request.Headers, CallerSession.ClientHeader) is { } key)
        {
            identity.AddClaim(new Claim(CallerSession.ClientKeyClaim, key));
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    /// <summary>
    /// The token's user and its <see cref="RequestTenant.Claim"/>, re-checked on every call: the user exists,
    /// is not disabled and may still enter the tenant. A token issued before tokens named a tenant names its
    /// user by name and takes their only membership (<see cref="UserStore.OAuthUserAsync"/>). A token that
    /// yields neither still authenticates, carrying the reason instead, and the tenant requirement turns that
    /// into a 403 naming what to do (<see cref="TenantRequirement"/>).
    /// </summary>
    private async Task UserAsync(ClaimsIdentity identity, string subject, ClaimsPrincipal token)
    {
        var claimed = Guid.TryParse(token.GetClaim(RequestTenant.Claim), out var id) ? id : (Guid?)null;
        var (grant, refusal) = Context.RequestServices.GetService<UserStore>() is { } users
            ? await users.OAuthUserAsync(subject, claimed, Context.RequestAborted)
            : (null, "This server has no user store to find the token's workspace in.");

        if (grant is not { User: var user, Tenant: var tenant })
        {
            identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, subject));
            identity.AddClaim(new Claim(TenantRequirement.RefusalClaim, refusal!));
            return;
        }

        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, user.Id.ToString()));
        identity.AddClaim(new Claim(CallerSession.UserIdClaim, user.Id.ToString()));
        identity.AddClaim(new Claim(UserNameClaim, user.Name));
        identity.AddClaim(new Claim(RequestTenant.Claim, tenant.ToString()));
    }

    /// <summary>
    /// The resource-metadata challenge for a request with no credential or with a JWT. A request that
    /// presented any other token gets the bare 401 it always got. Under <c>/api</c> the
    /// metadata is <c>/api</c>'s; the MCP handler would name one per request path.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if ((Presented() is not { } token || IsJwt(token)) && await ServesOAuthAsync())
        {
            if (!Request.Path.StartsWithSegments(OAuthServer.ApiPath))
            {
                await Context.ChallengeAsync(McpAuthenticationDefaults.AuthenticationScheme, properties);
                return;
            }

            var metadata = OAuthServer.Origin(Context) + OAuthServer.ResourceMetadataPath + OAuthServer.ApiPath;
            Response.Headers.Append(HeaderNames.WWWAuthenticate, $"Bearer resource_metadata=\"{metadata}\"");
        }

        await base.HandleChallengeAsync(properties);
    }

    private string? Presented()
    {
        var raw = Request.Headers[HeaderNames.Authorization].ToString();
        return raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? raw[BearerPrefix.Length..].Trim()
            : null;
    }

    private static bool IsJwt(string token)
        => token.Contains('.') && !token.StartsWith(SessionToken.Prefix, StringComparison.Ordinal);

    /// <summary>A host without a store has no OAuth server, and so nothing to validate or advertise.</summary>
    private async Task<bool> ServesOAuthAsync()
        => await schemes.GetSchemeAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) is not null;
}
