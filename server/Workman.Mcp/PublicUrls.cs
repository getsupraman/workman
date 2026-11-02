using Microsoft.AspNetCore.Http;

namespace Workman.Mcp;

/// <summary>
/// The origins this server is reached on, when the operator names them: <c>WorkState:PublicUrl</c> (one, as
/// before) and <c>WorkState:PublicUrls</c> (several, comma-separated), in that order and without repeats. Each
/// is its own OAuth issuer and resource; a request is answered as the one whose host it names, and a request
/// naming none of them as the first. None configured, the request's own scheme and host are used.
/// </summary>
public sealed class PublicUrls
{
    public const string Key = "WorkState:PublicUrl";

    public const string EnvironmentKey = "WORKSTATE_PUBLIC_URL";

    public const string ListKey = "WorkState:PublicUrls";

    public const string ListEnvironmentKey = "WORKMAN_PUBLIC_URLS";

    public static readonly PublicUrls None = new([]);

    private PublicUrls(IReadOnlyList<string> all) => All = all;

    /// <summary>Each origin without a trailing slash, the first being the fallback.</summary>
    public IReadOnlyList<string> All { get; }

    public bool Any => All.Count > 0;

    /// <summary>
    /// Every <paramref name="list"/> entry an absolute http(s) URL, or the host does not start. A
    /// <paramref name="single"/> that is not one is passed over, as the issuer always passed it over, so an
    /// install that starts today still starts.
    /// </summary>
    public static PublicUrls Parse(string? single, string? list)
    {
        var all = new List<string>();
        if (Origin(single) is { } first)
        {
            all.Add(first);
        }

        foreach (var raw in list?.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            var origin = Origin(raw) ?? throw new InvalidOperationException(
                $"'{raw.Trim()}' in {ListKey} is not an absolute http(s) URL; name each origin like https://workman.example.com.");

            if (!all.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                all.Add(origin);
            }
        }

        return all.Count == 0 ? None : new PublicUrls(all);
    }

    private static string? Origin(string? raw)
    {
        var trimmed = raw?.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? trimmed
            : null;
    }

    /// <summary>The configured origin whose host (and port) the request names, else the first; null when none is configured.</summary>
    public string? For(HttpRequest? request)
    {
        if (All.Count == 0)
        {
            return null;
        }

        if (request is { Host.HasValue: true })
        {
            var host = request.Host.Value;
            foreach (var url in All)
            {
                if (string.Equals(new Uri(url).Authority, host, StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
        }

        return All[0];
    }

    /// <summary>Every issuer a token from this server may name: each origin with and without its trailing slash.</summary>
    public IEnumerable<string> Issuers()
        => All.SelectMany(url => new[] { url, url + "/" });
}
