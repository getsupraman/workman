using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Workman.Api.App.V1;
using Workman.Api.Auth;
using Workman.Core.Data;
using Workman.Core.Queries;
using Workman.Core.Rendering;
using Workman.Mcp;

namespace Workman.Api.App;

/// <summary>Whether the cookie's user has a feed in the cookie's workspace, and since when. Never the token.</summary>
public sealed record CalendarFeedView(bool Exists, DateTimeOffset? CreatedAt);

/// <summary>A feed just made: the only answer that carries its token and URL.</summary>
public sealed record CreatedFeedView(string Token, string Url, DateTimeOffset CreatedAt);

/// <summary>
/// The calendar feed (doc 10, outside v1). A calendar app polls a URL and sends no header, so the URL carries the
/// secret: <c>GET /api/calendar/{token}.ics</c>, anonymous to the pipeline, answers the due dates of the open items in
/// the projects the token's user can see when it is asked. The user makes, inspects and revokes their feed with the
/// dashboard cookie under <c>/api/session/calendar</c>.
/// <para>
/// <b>Never log the token.</b> Nothing here writes a log line, every refusal is a bare 404, and only the token's hash
/// reaches a query. The ASP.NET hosting request line, which names the path, is off at the shipped log levels.
/// </para>
/// </summary>
public static class CalendarEndpoints
{
    public const string FeedPrefix = "/api/calendar/";

    public static void MapCalendar(this RouteGroupBuilder api)
    {
        api.MapGet("/calendar/{token}.ics", FeedAsync).AllowAnonymous();

        api.MapGet("/session/calendar", async (HttpContext context, [FromServices] CalendarFeedStore feeds) =>
        {
            var (user, tenant) = Caller(context.User);
            var createdAt = await feeds.CreatedAtAsync(user, tenant, context.RequestAborted);
            return Results.Ok(new CalendarFeedView(createdAt is not null, createdAt));
        }).RequireAuthorization(DashboardAuth.Policy);

        var write = api.MapGroup("/session/calendar").DashboardWrite();

        write.MapPost("", async (HttpContext context, [FromServices] CalendarFeedStore feeds, [FromServices] WebBase web) =>
        {
            var (user, tenant) = Caller(context.User);
            if (await feeds.CreateAsync(user, tenant, context.RequestAborted) is not { } created)
            {
                return Results.Json(
                    new { error = "only a member of this workspace has a calendar feed in it; an admin of the workspace adds the membership" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var url = $"{(web.Current ?? string.Empty).TrimEnd('/')}{FeedPrefix}{created.Token}.ics";
            return Results.Json(new CreatedFeedView(created.Token, url, created.CreatedAt), statusCode: StatusCodes.Status201Created);
        });

        write.MapDelete("", async (HttpContext context, [FromServices] CalendarFeedStore feeds) =>
        {
            var (user, tenant) = Caller(context.User);
            return await feeds.RevokeAsync(user, tenant, context.RequestAborted)
                ? Results.NoContent()
                : Results.Json(new { error = "there is no calendar feed to revoke" }, statusCode: StatusCodes.Status404NotFound);
        });
    }

    private static async Task<IResult> FeedAsync(
        string token,
        HttpContext context,
        [FromServices] CalendarFeedStore feeds,
        [FromServices] ApiQueries api,
        [FromServices] BoardQueries board,
        [FromServices] WebBase web,
        [FromServices] TimeProvider clock)
    {
        var cancellationToken = context.RequestAborted;

        // Owner connection, Bypass.Authentication: the token names the user and the workspace, so there is no tenant yet.
        if (await feeds.OpenAsync(token, cancellationToken) is not { } owner)
        {
            return Results.NotFound();
        }

        // Tenant connections from here on, as that user: a project they cannot see has no rows (doc 02 §15b).
        using var scope = TenantScope.EnterAs(owner.WorkspaceId, owner.UserId);

        var rows = await api.CalendarAsync(owner.WorkspaceId, cancellationToken);
        var zone = await api.TimeZoneAsync(owner.WorkspaceId, cancellationToken);
        var book = new V1Book(api, board);
        await book.ItemsAsync(rows.Select(r => r.Id), cancellationToken);

        var origin = (web.Current ?? string.Empty).TrimEnd('/');
        var host = Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : "workman";

        var events = rows.Where(r => book.Has(r.Id)).Select(r =>
        {
            var item = book.Item(r.Id);
            return new CalendarEvent(
                $"{r.Id}@{host}",
                r.Day,
                r.DayAfter,
                r.At,
                $"{item.QualifiedRef} {item.Title}",
                WebLinks.Link(origin, item.FullRef),
                V1Text.Status(r.Status));
        });

        context.Response.Headers.CacheControl = "no-store";
        return Results.Text(
            ICalendar.Render($"Workman {owner.Slug}", zone, clock.GetUtcNow(), events),
            ICalendar.ContentType);
    }

    private static (Guid User, Guid Tenant) Caller(ClaimsPrincipal principal)
        => (Guid.Parse(principal.FindFirstValue(DashboardAuth.UserIdClaim)!), RequestTenant.Of(principal)!.Value);
}
