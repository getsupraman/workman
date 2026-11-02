using Microsoft.AspNetCore.Http;

namespace Workman.Mcp;

/// <summary>
/// The scheme and host a <c>web</c> line names: the configured public URL whose host this request names, else
/// the first configured one (<see cref="PublicUrls"/>), else the scheme and host of this request. Behind a proxy
/// listed in <c>WORKMAN_TRUSTED_PROXIES</c> those are the forwarded ones, because <c>ApiHost</c> runs
/// <c>UseForwardedHeaders</c> first; from any other address <c>X-Forwarded-*</c> is ignored, and with none
/// listed it is never read at all.
/// </summary>
public sealed class WebBase(IHttpContextAccessor accessor, PublicUrls publicUrls)
{
    public const string Key = PublicUrls.Key;

    public const string EnvironmentKey = PublicUrls.EnvironmentKey;

    public string? Current
    {
        get
        {
            var request = accessor.HttpContext?.Request;
            if (publicUrls.For(request) is { } configured)
            {
                return configured;
            }

            return request is { Host.HasValue: true }
                ? $"{request.Scheme}://{request.Host}{request.PathBase}"
                : null;
        }
    }
}
