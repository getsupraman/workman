using Microsoft.AspNetCore.Authentication;
using Microsoft.Net.Http.Headers;
using Workman.Api.Auth.OAuth;
using Workman.Mcp;

namespace Workman.Api.Auth;

/// <summary>
/// One Information line for every request that ended 401: which client and agent sent it, its User-Agent,
/// whether it carried an Authorization header and why the bearer schemes refused it. Never the header's value:
/// the reasons are the handlers' own fixed texts.
/// </summary>
public static class ChallengeLog
{
    public const string Category = "Workman.Auth.Challenge";

    private static readonly string[] BearerSchemes = [SessionTokenHandler.SchemeName, OAuthTokenHandler.SchemeName];

    /// <summary>Ahead of authentication, so the line sees the status the whole pipeline ended on.</summary>
    public static IApplicationBuilder UseChallengeLog(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(Category);

        return app.Use(async (context, next) =>
        {
            await next(context);

            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized && logger.IsEnabled(LogLevel.Information))
            {
                var headers = context.Request.Headers;
                logger.LogInformation(
                    "Unauthenticated {Method} {Path}: client={Client} agent={Agent} user-agent={UserAgent} authorization={Authorization} reason={Reason}",
                    context.Request.Method,
                    context.Request.Path.Value,
                    Clean(headers[WorkmanHeaders.Client]),
                    Clean(headers[WorkmanHeaders.Agent]),
                    Clean(headers[HeaderNames.UserAgent]),
                    headers.ContainsKey(HeaderNames.Authorization) ? "yes" : "no",
                    await ReasonAsync(context));
            }
        });
    }

    /// <summary>Each handler caches its result per request, so this re-reads what the challenge was decided on.</summary>
    private static async Task<string> ReasonAsync(HttpContext context)
    {
        var reasons = new List<string>();
        foreach (var scheme in BearerSchemes)
        {
            if ((await context.AuthenticateAsync(scheme)).Failure is { } failure)
            {
                reasons.Add($"{scheme}: {failure.Message}");
            }
        }

        return reasons.Count == 0 ? "none" : string.Join("; ", reasons);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }
}
