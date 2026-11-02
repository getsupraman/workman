using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Workman.Mcp;

/// <summary>
/// The one reader and writer of the <c>X-Workman-*</c> headers (C56). Since server 2.0 no other name is
/// read or sent.
/// </summary>
public static class WorkmanHeaders
{
    public const string Prefix = "X-Workman-";

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

    /// <summary>The header, or empty when the request does not carry it.</summary>
    public static StringValues Read(IHeaderDictionary headers, string name) => headers[name];

    /// <summary><see cref="Read"/>, trimmed, or null when it carries no value.</summary>
    public static string? Text(IHeaderDictionary headers, string name)
        => Read(headers, name).ToString().Trim() is { Length: > 0 } text ? text : null;

    /// <summary>Sets the header.</summary>
    public static void Send(IHeaderDictionary headers, string name, string value) => headers[name] = value;
}
