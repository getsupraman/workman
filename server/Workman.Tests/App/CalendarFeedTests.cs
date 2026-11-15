using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Workman.Api.App;
using Workman.Api.Auth;
using Workman.Core.Queries;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App;

/// <summary>
/// deadlines/7, the calendar feed: a user makes their feed URL with the dashboard cookie and sees its token once; only
/// its hash is stored; <c>GET /api/calendar/{token}.ics</c> lists the open dated items of the projects that user can
/// see when it is asked; a replaced, revoked or unknown token, a disabled user and a removed member are a bare 404; and
/// no log line ever carries the token.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class CalendarFeedTests(PostgresFixture fixture)
{
    private const string Feed = "/api/session/calendar";

    [Fact]
    public async Task Calendar_a_user_creates_sees_once_replaces_and_revokes_their_feed_and_only_its_hash_is_stored()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var cookie = await world.CookieAsync(world.User1);

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.NewClient().GetAsync(Feed, Ct)).StatusCode);
        Assert.False((await StatusAsync(cookie)).GetProperty("exists").GetBoolean());

        // A dashboard write: without the header nothing is made.
        Assert.Equal(HttpStatusCode.Forbidden, (await cookie.PostAsync(Feed, null, Ct)).StatusCode);
        Assert.False((await StatusAsync(cookie)).GetProperty("exists").GetBoolean());

        var (token, url) = await CreateAsync(cookie);
        Assert.Equal($"{server.BaseAddress}/api/calendar/{token}.ics", url);

        var status = await cookie.GetStringAsync(Feed, Ct);
        Assert.Contains("\"exists\":true", status, StringComparison.Ordinal);
        Assert.DoesNotContain(token, status, StringComparison.Ordinal);

        await using (var owner = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            var stored = await owner.QuerySingleAsync<string>(
                "SELECT token_hash FROM calendar_feed WHERE user_id = @user AND workspace_id = @ws;",
                new { user = world.User1Id, ws = world.WorkspaceId });
            Assert.Equal(LinkToken.Hash(token), stored);
            Assert.Equal(0, await owner.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM calendar_feed f WHERE row_to_json(f)::text LIKE '%' || @token || '%';", new { token }));
        }

        var anonymous = server.NewClient();
        var hit = await anonymous.GetAsync(new Uri(url), Ct);
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        Assert.Equal("text/calendar", hit.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", hit.Content.Headers.ContentType?.CharSet);

        // A new one replaces the old: the old URL stops working.
        var (second, secondUrl) = await CreateAsync(cookie);
        Assert.NotEqual(token, second);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(new Uri(url), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(new Uri(secondUrl), Ct)).StatusCode);

        var revoked = await Send(cookie, HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        var miss = await anonymous.GetAsync(new Uri(secondUrl), Ct);
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
        Assert.Empty(await miss.Content.ReadAsStringAsync(Ct));
        Assert.False((await StatusAsync(cookie)).GetProperty("exists").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(cookie, HttpMethod.Delete)).StatusCode);

        // Unknown, and not even shaped like a token: the same bare 404.
        foreach (var path in new[] { $"/api/calendar/{LinkToken.New()}.ics", "/api/calendar/nope.ics" })
        {
            var unknown = await anonymous.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Empty(await unknown.Content.ReadAsStringAsync(Ct));
        }

        // One user's feed is theirs: user 2's revoke does not touch user 1's.
        var (third, thirdUrl) = await CreateAsync(cookie);
        var other = await world.CookieAsync(world.User2);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(other, HttpMethod.Delete)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(new Uri(thirdUrl), Ct)).StatusCode);
        Assert.NotEmpty(third);
    }

    [Fact]
    public async Task Calendar_the_feed_lists_the_open_dated_items_of_the_projects_its_user_can_see_now()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        const string title = "quokka, task; of P1 \\ with a newline\nand a very long tail so that the summary line has to fold: é€😀 done";

        await using (var owner = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await owner.ExecuteAsync(
                """
                UPDATE workspace SET settings = coalesce(settings, '{}'::jsonb) || '{"timezone":"Europe/Rome"}' WHERE id = @ws;
                UPDATE item SET due_on = DATE '2026-12-01', title = @title WHERE id = @p1Task;
                UPDATE item SET due_at = TIMESTAMPTZ '2026-12-03T16:30:00Z' WHERE id = @p2Only;
                UPDATE item SET due_on = DATE '2026-12-02', status = 'done', resolution = 'accepted', closed_at = now() WHERE id = @p2Task;
                """,
                new { ws = world.WorkspaceId, title, p1Task = world.One.TaskId, p2Only = world.OnlyInP2Id, p2Task = world.P2TaskId });
        }

        var board = new BoardQueries(fixture.DataSource);
        var p1Link = $"{server.BaseAddress}/r/{string.Join('/', (await board.FullRefAsync(world.One.TaskId, Ct))!.Split('/').Select(Uri.EscapeDataString))}";

        // User 1 is a member of P1 only.
        var one = await world.CookieAsync(world.User1);
        var (_, url1) = await CreateAsync(one);
        var feed1 = await IcsAsync(server, url1);
        AssertWellFormed(feed1.Raw);

        var p1 = Assert.Single(feed1.Events);
        Assert.Contains($"UID:{world.One.TaskId}@127.0.0.1", p1);
        Assert.Contains("DTSTART;VALUE=DATE:20261201", p1);
        Assert.Contains("DTEND;VALUE=DATE:20261202", p1);
        Assert.Contains(@"SUMMARY:1.1 quokka\, task\; of P1 \\ with a newline\nand a very long tail so that the summary line has to fold: é€😀 done", p1);
        Assert.Contains($"URL:{p1Link}", p1);
        Assert.Contains("DESCRIPTION:open", p1);
        Assert.Contains("X-WR-TIMEZONE:Europe/Rome", feed1.Unfolded);
        Assert.DoesNotContain(world.OnlyInP2Id.ToString(), feed1.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ProjectWorld.TitleOnlyInP2, feed1.Raw, StringComparison.Ordinal);

        // User 2 is a member of both: the timed event of P2 too, and never the closed item.
        var two = await world.CookieAsync(world.User2);
        var (_, url2) = await CreateAsync(two);
        var feed2 = await IcsAsync(server, url2);
        AssertWellFormed(feed2.Raw);
        Assert.Equal(2, feed2.Events.Count);
        var p2 = Assert.Single(feed2.Events, e => e.Contains($"UID:{world.OnlyInP2Id}@", StringComparison.Ordinal));
        Assert.Contains("DTSTART:20261203T163000Z", p2);
        Assert.DoesNotContain("DTEND", p2, StringComparison.Ordinal);
        Assert.Contains($"{ProjectWorld.TitleOnlyInP2}", p2);
        Assert.DoesNotContain(world.P2TaskId.ToString(), feed2.Raw, StringComparison.Ordinal);

        // An archived project drops out of the calendar as out of every list; restoring it brings its item back.
        Assert.Equal(HttpStatusCode.OK, (await Archive(two, world.P2Id, "archive")).StatusCode);
        var archived = await IcsAsync(server, url2);
        Assert.Single(archived.Events);
        Assert.DoesNotContain(world.OnlyInP2Id.ToString(), archived.Raw, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await Archive(two, world.P2Id, "unarchive")).StatusCode);
        var restored = await IcsAsync(server, url2);
        Assert.Equal(2, restored.Events.Count);
        Assert.Contains(restored.Events, e => e.Contains($"UID:{world.OnlyInP2Id}@", StringComparison.Ordinal));

        // Access is read when asked: out of P2, its event is gone from the same URL.
        await using (var owner = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            // P2 keeps another member (the operator), else a project with no member is the whole tenant's (doc 02 §15b).
            await owner.ExecuteAsync(
                """
                INSERT INTO project_member (project_item_id, user_id, workspace_id)
                SELECT @p2, u.id, @ws FROM app_user u WHERE lower(u.name) = lower(@name);
                DELETE FROM project_member WHERE project_item_id = @p2 AND user_id = @user;
                """,
                new { p2 = world.P2Id, ws = world.WorkspaceId, name = AppServer.Username, user = world.User2Id });
        }

        var narrowed = await IcsAsync(server, url2);
        Assert.Single(narrowed.Events);
        Assert.DoesNotContain(world.OnlyInP2Id.ToString(), narrowed.Raw, StringComparison.Ordinal);

        // Disabled: 404. Removed from the workspace: 404, and the feed went with the membership.
        await using (var owner = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await owner.ExecuteAsync("UPDATE app_user SET disabled_at = now() WHERE id = @user;", new { user = world.User1Id });
            Assert.Equal(HttpStatusCode.NotFound, (await server.NewClient().GetAsync(new Uri(url1), Ct)).StatusCode);

            await owner.ExecuteAsync(
                "DELETE FROM tenant_membership WHERE user_id = @user AND workspace_id = @ws;", new { user = world.User2Id, ws = world.WorkspaceId });
            Assert.Equal(HttpStatusCode.NotFound, (await server.NewClient().GetAsync(new Uri(url2), Ct)).StatusCode);
            Assert.Equal(0, await owner.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM calendar_feed WHERE user_id = @user;", new { user = world.User2Id }));
        }
    }

    /// <summary>
    /// At the shipped log levels (appsettings.json: Information, Microsoft.AspNetCore at Warning), a hit, a miss and a
    /// revoked token write the token nowhere: no message, no exception, and no scope around a line written during the
    /// request (the hosting scope names the path). Debug on the auth handlers, or Information on
    /// Microsoft.AspNetCore.Hosting, would write it; doc 10 says so.
    /// </summary>
    [Fact]
    public async Task Calendar_no_log_line_or_scope_carries_the_token_on_a_hit_a_miss_or_a_revoked_token()
    {
        var lines = new LogLines();
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString,
            world.Slug,
            Ct,
            configure: builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = "Information",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
                });
                builder.Logging.AddProvider(lines);
            });

        await using (var owner = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await owner.ExecuteAsync("UPDATE item SET due_on = DATE '2026-12-01' WHERE id = @id;", new { id = world.TaskId });
        }

        var cookie = await server.SignInAsync(Ct);
        var (token, url) = await CreateAsync(cookie);
        var anonymous = server.NewClient();
        var missing = LinkToken.New();

        var hit = await anonymous.GetAsync(new Uri(url), Ct);
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        Assert.Contains("BEGIN:VEVENT", await hit.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/calendar/{missing}.ics", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(cookie, HttpMethod.Delete)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(new Uri(url), Ct)).StatusCode);

        Assert.NotEmpty(lines.All);
        foreach (var line in lines.All)
        {
            Assert.False(line.Contains(token, StringComparison.Ordinal), line);
            Assert.DoesNotContain(missing, line, StringComparison.Ordinal);
        }

        // The one line that names a path, the 401 challenge, names the route and never a feed's token.
        Assert.Equal("/api/calendar/{token}.ics", ChallengeLog.Redacted($"/api/calendar/{token}.ics"));
        Assert.Equal("/api/v1/overview", ChallengeLog.Redacted("/api/v1/overview"));
    }

    private static async Task<JsonElement> StatusAsync(HttpClient cookie)
        => JsonDocument.Parse(await cookie.GetStringAsync(Feed, Ct)).RootElement.Clone();

    private static async Task<(string Token, string Url)> CreateAsync(HttpClient cookie)
    {
        var response = await Send(cookie, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        return (body.GetProperty("token").GetString()!, body.GetProperty("url").GetString()!);
    }

    private static Task<HttpResponseMessage> Send(HttpClient cookie, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, Feed);
        request.Headers.Add("X-Workman-Write", "1");
        return cookie.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Archive(HttpClient cookie, Guid project, string action)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/{action}");
        request.Headers.Add("X-Workman-Write", "1");
        return cookie.SendAsync(request, Ct);
    }

    private sealed record Ics(string Raw, string Unfolded, IReadOnlyList<string> Events);

    private static async Task<Ics> IcsAsync(AppServer server, string url)
    {
        var response = await server.NewClient().GetAsync(new Uri(url), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var unfolded = raw.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        var events = unfolded.Split("BEGIN:VEVENT\r\n").Skip(1).Select(e => e[..e.IndexOf("END:VEVENT", StringComparison.Ordinal)]).ToList();
        return new Ics(raw, unfolded, events);
    }

    /// <summary>CRLF only, no physical line over 75 octets, and the calendar's frame.</summary>
    private static void AssertWellFormed(string raw)
    {
        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", raw, StringComparison.Ordinal);
        Assert.EndsWith("END:VCALENDAR\r\n", raw, StringComparison.Ordinal);
        Assert.Equal(raw.Split('\n').Length, raw.Split("\r\n").Length);
        foreach (var line in raw.Split("\r\n"))
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"{Encoding.UTF8.GetByteCount(line)} octets: {line}");
        }

        Assert.Contains("DTSTAMP:", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every line written, with the scopes open around it, as a provider that prints scopes would write it: the hosting
    /// scope names the request path, so a line written during the feed's request would carry the token there.
    /// </summary>
    private sealed class LogLines : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly AsyncLocal<ImmutableStack> _scopes = new();

        public IEnumerable<string> All => _lines.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed record ImmutableStack(string Text, ImmutableStack? Outer);

        private sealed class Logger(string category, LogLines owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                var outer = owner._scopes.Value;
                owner._scopes.Value = new ImmutableStack(Describe(state), outer);
                return new Pop(owner, outer);
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scopes = new List<string>();
                for (var scope = owner._scopes.Value; scope is not null; scope = scope.Outer)
                {
                    scopes.Add(scope.Text);
                }

                owner._lines.Enqueue($"{category} {logLevel} {formatter(state, exception)} {exception} => {string.Join(" => ", scopes)}");
            }

            private static string Describe(object state)
                => state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"))
                    : state.ToString() ?? string.Empty;
        }

        private sealed class Pop(LogLines owner, ImmutableStack? outer) : IDisposable
        {
            public void Dispose() => owner._scopes.Value = outer!;
        }
    }
}
