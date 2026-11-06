using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App;

/// <summary>
/// Server 3.0 reads no static role or reader token: a value some machine still holds, even with its old
/// configuration key still set, is a credential nobody accepts, while the dashboard and OAuth keep working.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class StaticTokensRetiredTests(PostgresFixture fixture)
{
    private static readonly string[] Former =
        ["test-worker-token-4b1f7a", "test-orchestrator-token-8d2c6e", "test-controller-token-c93a15", "test-reader-token-7e0d42"];

    [Fact]
    public async Task A_former_static_token_reaches_nothing_even_configured_and_the_cookie_still_works()
    {
        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString,
            $"retired-{Guid.CreateVersion7():n}",
            Ct,
            configure: builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workman:Auth:WorkerToken"] = Former[0],
                ["Workman:Auth:OrchestratorToken"] = Former[1],
                ["Workman:Auth:ControllerToken"] = Former[2],
                ["Workman:Auth:ReaderToken"] = Former[3],
            }));

        foreach (var token in Former)
        {
            var client = server.NewClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(AppServer.McpInitialize(), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.PostAsync("/api/agent-sessions", JsonContent.Create(new { name = "retired", role = "worker" }), Ct)).StatusCode);
        }

        // /mcp still points an unauthenticated client at the OAuth server, which is how every client signs in now.
        var anonymous = await server.NewClient().SendAsync(AppServer.McpInitialize(), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("resource_metadata", anonymous.Headers.WwwAuthenticate.ToString());

        var signedIn = await server.SignInAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/overview", Ct)).StatusCode);
    }
}
