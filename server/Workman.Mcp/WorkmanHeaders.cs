using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Workman.Mcp;

/// <summary>
/// The one reader and writer of the <c>X-Workman-*</c> headers (C56). Each is still accepted under its
/// old <c>X-WorkState-*</c> name until server 2.0 (CHANGELOG, Unreleased); when a request carries both,
/// the new name wins. A header the server sends goes out under both names for the same window.
/// </summary>
public static class WorkmanHeaders
{
    public const string Prefix = "X-Workman-";

    /// <summary>The name every header had before Workman; read and written until server 2.0.</summary>
    public const string LegacyPrefix = "X-WorkState-";

    public const string Agent = Prefix + "Agent";
    public const string Session = Prefix + "Session";
    public const string LaunchedBy = Prefix + "Launched-By";
    public const string Origin = Prefix + "Origin";
    public const string Cwd = Prefix + "Cwd";
    public const string ForemanRole = Prefix + "Foreman-Role";

    /// <summary>The session helper's key for the Claude Code process: host/pid/start time.</summary>
    public const string Client = Prefix + "Client";

    /// <summary>What every dashboard write carries (doc 10).</summary>
    public const string Write = Prefix + "Write";

    /// <summary>The read API's version, on every <c>/api/v1/*</c> and <c>/api/stream</c> response.</summary>
    public const string Api = Prefix + "Api";

    public static readonly IReadOnlyList<string> All = [Agent, Session, LaunchedBy, Origin, Cwd, ForemanRole, Client, Write, Api];

    /// <summary><c>X-Workman-Agent</c> → <c>X-WorkState-Agent</c>.</summary>
    public static string Legacy(string name)
        => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? LegacyPrefix + name[Prefix.Length..]
            : throw new ArgumentException($"{name} is not an {Prefix}* header.", nameof(name));

    /// <summary>The header under its new name when the request carries it, else under its old one.</summary>
    public static StringValues Read(IHeaderDictionary headers, string name)
        => headers.TryGetValue(name, out var value) && !StringValues.IsNullOrEmpty(value)
            ? value
            : headers[Legacy(name)];

    /// <summary><see cref="Read"/>, trimmed, or null when neither name carries a value.</summary>
    public static string? Text(IHeaderDictionary headers, string name)
        => Read(headers, name).ToString().Trim() is { Length: > 0 } text ? text : null;

    /// <summary>Sets the header under both names.</summary>
    public static void Send(IHeaderDictionary headers, string name, string value)
    {
        headers[name] = value;
        headers[Legacy(name)] = value;
    }
}
