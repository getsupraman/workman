using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Workman.Tests.App.V1;

/// <summary>
/// Counts the SQL commands one HTTP read issues. The test opens a trace of its own; the client sends it as
/// <c>traceparent</c>, the host continues it, and every Npgsql command the request runs is an activity in that
/// trace. Sampling only that trace keeps the other tests' commands and the server's background work out.
/// </summary>
internal static class SqlCommands
{
    private static readonly ActivitySource Source = new("Workman.Tests.SqlCommands");

    public sealed record Counted(JsonElement Body, int Commands, IReadOnlyList<string> Texts, TimeSpan Elapsed);

    public static async Task<Counted> GetAsync(HttpClient client, string path)
    {
        var trace = ActivityTraceId.CreateRandom();
        var commands = new ConcurrentQueue<string>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Npgsql" or "Microsoft.AspNetCore" || s == Source,
            Sample = (ref ActivityCreationOptions<ActivityContext> options)
                => options.Parent.TraceId == trace ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == trace && activity.Source.Name == "Npgsql" && activity.Kind == ActivityKind.Client
                    && (activity.GetTagItem("db.query.text") ?? activity.GetTagItem("db.statement")) is { } text)
                {
                    commands.Enqueue($"{activity.Duration.TotalMilliseconds,7:0} ms  {text}");
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        JsonElement body;
        var clock = Stopwatch.StartNew();
        using (Source.StartActivity("count", ActivityKind.Internal, new ActivityContext(trace, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)))
        {
            body = await V1Http.GetAsync(client, path);
        }

        return new Counted(body, commands.Count, [.. commands], clock.Elapsed);
    }

    /// <summary>The slowest commands, each with its duration.</summary>
    public static string Slowest(Counted counted, int take = 5)
        => string.Join(
            "\n",
            counted.Texts
                .OrderByDescending(t => double.Parse(t[..7], System.Globalization.CultureInfo.InvariantCulture))
                .Take(take)
                .Select(t => string.Join(' ', t.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(3))));

    /// <summary>The commands grouped by their text's first line, most frequent first: where a loop shows up.</summary>
    public static string Summary(Counted counted)
        => string.Join(
            "\n",
            counted.Texts
                .GroupBy(t => string.Join(' ', t[11..].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(2)))
                .OrderByDescending(g => g.Count())
                .Take(12)
                .Select(g => $"{g.Count(),5}  {g.Key[..Math.Min(140, g.Key.Length)]}"));

    public static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
}
