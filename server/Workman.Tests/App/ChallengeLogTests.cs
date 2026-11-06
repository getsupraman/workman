using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Workman.Api.Auth;
using Workman.Mcp;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App;

/// <summary>
/// Every request that ends 401 says who sent it — the client key, the agent name, the User-Agent — whether it
/// carried a credential and why that was refused, and never the credential.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ChallengeLogTests(PostgresFixture fixture)
{
    private const string Presented = "wss_challenge-log-secret-5c2e91";

    [Fact]
    public async Task A_challenged_request_logs_its_client_agent_and_user_agent_and_never_the_token()
    {
        var lines = new LogLines();
        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString,
            $"challenge-{Guid.CreateVersion7():n}",
            Ct,
            configure: builder => builder.Logging
                .AddProvider(lines)
                .AddFilter<LogLines>(ChallengeLog.Category, LogLevel.Information));

        var client = server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Presented);
        client.DefaultRequestHeaders.Add(WorkmanHeaders.Client, "host-a/4242/1700000000");
        client.DefaultRequestHeaders.Add(WorkmanHeaders.Agent, "aw-probe");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("probe-agent/1.0");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(AppServer.McpInitialize(), Ct)).StatusCode);

        var refused = Assert.Single(lines.Of(ChallengeLog.Category));
        Assert.Equal(LogLevel.Information, refused.Level);
        Assert.Contains("POST /mcp", refused.Text, StringComparison.Ordinal);
        Assert.Contains("client=host-a/4242/1700000000", refused.Text, StringComparison.Ordinal);
        Assert.Contains("agent=aw-probe", refused.Text, StringComparison.Ordinal);
        Assert.Contains("user-agent=probe-agent/1.0", refused.Text, StringComparison.Ordinal);
        Assert.Contains("authorization=yes", refused.Text, StringComparison.Ordinal);
        Assert.Contains("Unrecognised bearer token.", refused.Text, StringComparison.Ordinal);

        // Not the token, nor any piece of it long enough to matter, on any line any category wrote.
        foreach (var line in lines.All)
        {
            Assert.DoesNotContain(Presented["wss_".Length..], line.Text, StringComparison.Ordinal);
        }

        lines.Clear();
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.NewClient().GetAsync("/api/v1/overview", Ct)).StatusCode);

        var anonymous = Assert.Single(lines.Of(ChallengeLog.Category));
        Assert.Contains("GET /api/v1/overview", anonymous.Text, StringComparison.Ordinal);
        Assert.Contains("client=- agent=- user-agent=-", anonymous.Text, StringComparison.Ordinal);
        Assert.Contains("authorization=no reason=none", anonymous.Text, StringComparison.Ordinal);

        // An admitted request writes no such line.
        lines.Clear();
        var admitted = server.NewClient();
        admitted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Read.ReadServer.WorkerToken);
        Assert.Equal(HttpStatusCode.OK, (await admitted.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Empty(lines.Of(ChallengeLog.Category));
    }

    private sealed record Line(string Category, LogLevel Level, string Text);

    private sealed class LogLines : ILoggerProvider
    {
        private readonly ConcurrentQueue<Line> _lines = new();

        public IEnumerable<Line> All => _lines.ToArray();

        public IEnumerable<Line> Of(string category) => All.Where(line => line.Category == category);

        public void Clear() => _lines.Clear();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<Line> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue(new Line(category, logLevel, formatter(state, exception) + (exception is null ? string.Empty : $" {exception}")));
        }
    }
}
