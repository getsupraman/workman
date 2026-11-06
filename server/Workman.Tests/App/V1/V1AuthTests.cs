using System.Net;
using System.Net.Http.Headers;
using Workman.Api.App.V1;
using Workman.Tests.Read;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App.V1;

/// <summary>
/// doc 10 rule 5: the read API takes the dashboard cookie or any agent token, and nothing else.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1AuthTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Every_agent_token_and_the_cookie_read_v1()
    {
        await using var server = await V1Http.StartAsync(fixture, $"agents-{Guid.CreateVersion7():n}");

        foreach (var token in (string[])[ReadServer.WorkerToken, ReadServer.OrchestratorToken, ReadServer.ControllerToken])
        {
            await V1Http.GetAsync(V1Http.Bearer(server, token), "/api/v1/overview");
        }

        await V1Http.GetAsync(await server.SignInAsync(Ct), "/api/v1/overview");
    }

    [Fact]
    public async Task No_credential_is_a_401_that_still_carries_the_api_header()
    {
        await using var server = await V1Http.StartAsync(fixture, $"anon-{Guid.CreateVersion7():n}");
        var anonymous = server.NewClient();

        foreach (var route in (string[])["/api/v1/overview", "/api/v1/inbox", $"/api/v1/items/{Guid.CreateVersion7()}"])
        {
            var response = await anonymous.GetAsync(route, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(ApiV1.Version, Assert.Single(response.Headers.GetValues(ApiV1.Header)));
        }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, Ct)).StatusCode);

        var wrong = V1Http.Bearer(server, "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/v1/overview", Ct)).StatusCode);
    }
}
