using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Workman.Mcp;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App;

/// <summary>
/// One server reached on two origins (<c>WorkState:PublicUrls</c>): each is its own issuer and resource, the
/// request's host picks which one answers, and an unknown host is answered as the first. The test socket is
/// <c>127.0.0.1:&lt;random&gt;</c>; the <c>Host</c> header is what names the origin, as a proxy's would.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class MultiHostTests(PostgresFixture fixture)
{
    private const string A = "http://127.0.0.1:5996";
    private const string B = "http://localhost:5997";

    [Fact]
    public void PublicUrl_comes_first_then_the_list_without_repeats()
    {
        var urls = PublicUrls.Parse("https://a.example/", "https://b.example, https://A.example ;http://127.0.0.1:5290");
        Assert.Equal(["https://a.example", "https://b.example", "http://127.0.0.1:5290"], urls.All);
        Assert.Equal(
            ["https://a.example", "https://a.example/", "https://b.example", "https://b.example/", "http://127.0.0.1:5290", "http://127.0.0.1:5290/"],
            urls.Issuers());

        Assert.False(PublicUrls.Parse(null, " ").Any);
        Assert.Equal(["https://b.example"], PublicUrls.Parse(null, "https://b.example").All);

        // The single key was always passed over by the issuer when it was not a URL; it still is, so that host starts.
        Assert.False(PublicUrls.Parse("workman.example.com", null).Any);
        var refused = Assert.Throws<InvalidOperationException>(() => PublicUrls.Parse(null, "https://b.example, b.example"));
        Assert.Contains("'b.example'", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("b.example", "https://b.example")]
    [InlineData("B.EXAMPLE", "https://b.example")]
    [InlineData("127.0.0.1:5290", "http://127.0.0.1:5290")]
    [InlineData("127.0.0.1:5291", "https://a.example")]
    [InlineData("unknown.example", "https://a.example")]
    public void The_request_host_picks_the_origin_and_an_unknown_one_gets_the_first(string host, string expected)
    {
        var urls = PublicUrls.Parse("https://a.example", "https://b.example,http://127.0.0.1:5290");
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        Assert.Equal(expected, urls.For(context.Request));
        Assert.Null(PublicUrls.None.For(context.Request));
    }

    [Fact]
    public async Task Each_host_is_its_own_issuer_and_resource_and_signs_in_on_its_own()
    {
        var slug = Slug();
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, slug, Ct, publicUrls: $"{A},{B}");

        foreach (var origin in new[] { A, B })
        {
            using var anonymous = Client(server, origin);
            var discovery = await JsonAsync(anonymous, "/.well-known/oauth-authorization-server");
            Assert.Equal($"{origin}/", discovery.GetProperty("issuer").GetString());
            Assert.Equal($"{origin}/connect/token", discovery.GetProperty("token_endpoint").GetString());

            var resource = await JsonAsync(anonymous, "/.well-known/oauth-protected-resource/mcp");
            Assert.Equal($"{origin}/mcp", resource.GetProperty("resource").GetString());
            Assert.Equal(origin, resource.GetProperty("authorization_servers")[0].GetString());
        }

        var atA = await SignInAsync(server, A);
        var atB = await SignInAsync(server, B);

        Assert.Contains("work_brief", await ToolsListAsync(server, A, atA.AccessToken));
        Assert.Contains("work_brief", await ToolsListAsync(server, B, atB.AccessToken));

        // A token is for the origin it was issued at: the other's audience refuses it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await InitializeAsync(server, B, atA.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await InitializeAsync(server, A, atB.AccessToken)).StatusCode);

        // A host that is neither is answered as the first.
        Assert.Contains("work_brief", await ToolsListAsync(server, null, atA.AccessToken));
        using var unknown = Client(server, null);
        Assert.Equal($"{A}/", (await JsonAsync(unknown, "/.well-known/oauth-authorization-server")).GetProperty("issuer").GetString());

        // Refresh works at the origin that issued the grant.
        var refreshed = await OAuthTests.RefreshAsync(Client(server, B), atB.ClientId, atB.RefreshToken!);
        Assert.Contains("work_brief", await ToolsListAsync(server, B, refreshed.AccessToken));
    }

    [Fact]
    public async Task A_token_issued_with_one_public_url_keeps_working_after_a_second_is_added()
    {
        var slug = Slug();
        OAuthTests.TokenSet before;

        await using (var single = await AppServer.StartAsync(fixture.ConnectionString, slug, Ct, publicUrl: A))
        {
            before = await SignInAsync(single, A);
            Assert.Contains("work_brief", await ToolsListAsync(single, A, before.AccessToken));
        }

        await using var both = await AppServer.StartAsync(fixture.ConnectionString, slug, Ct, publicUrl: A, publicUrls: B);

        Assert.Contains("work_brief", await ToolsListAsync(both, A, before.AccessToken));
        var refreshed = await OAuthTests.RefreshAsync(Client(both, A), before.ClientId, before.RefreshToken!);
        Assert.Contains("work_brief", await ToolsListAsync(both, A, refreshed.AccessToken));

        var atB = await SignInAsync(both, B);
        Assert.Contains("work_brief", await ToolsListAsync(both, B, atB.AccessToken));
    }

    /// <summary>Register, sign in to the dashboard, authorize with PKCE and exchange — all on <paramref name="origin"/>.</summary>
    private static async Task<OAuthTests.TokenSet> SignInAsync(AppServer server, string origin)
    {
        var anonymous = Client(server, origin);
        var clientId = await OAuthTests.RegisterAsync(anonymous);

        var browser = Client(server, origin);
        var signedIn = await AppServer.SignInAsync(browser, AppServer.Username, AppServer.Password, Ct);
        Assert.Equal(HttpStatusCode.NoContent, signedIn.StatusCode);

        var pkce = OAuthTests.Pkce.Create();
        var code = await OAuthTests.AuthorizeForCodeAsync(browser, clientId, pkce, $"{origin}/mcp");
        return await OAuthTests.ExchangeAsync(anonymous, clientId, code, pkce);
    }

    private static async Task<string> ToolsListAsync(AppServer server, string? origin, string token)
    {
        var response = await McpAsync(server, origin, token, new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"tools/list at {origin} → {(int)response.StatusCode}: {body}");
        return body;
    }

    private static Task<HttpResponseMessage> InitializeAsync(AppServer server, string origin, string token)
        => McpAsync(server, origin, token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "multi-host", version = "0" } },
        });

    private static async Task<HttpResponseMessage> McpAsync(AppServer server, string? origin, string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new("Bearer", token);
        return await Client(server, origin).SendAsync(request, Ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    /// <summary>A client of the test socket that names <paramref name="origin"/>'s host on every request; null names the socket's own.</summary>
    private static HttpClient Client(AppServer server, string? origin)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };
        return new HttpClient(origin is null ? handler : new HostHeader(new Uri(origin).Authority, handler))
        {
            BaseAddress = new Uri(server.BaseAddress),
        };
    }

    private sealed class HostHeader(string host, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Host = host;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static string Slug() => $"hosts-{Guid.CreateVersion7():n}";
}
