using Xunit;

namespace Workman.Tests.Store;

/// <summary>
/// C62 at the source: <c>WorkspaceOptions</c> — <c>Workman:Workspace</c> — was the static tokens' own binding and
/// went with them. A production file that names it again is a request path reaching for a configured default
/// instead of its principal's tenant.
/// </summary>
public sealed class TenantSourceGuardTests
{
    private static readonly string[] Allowed = [];

    [Fact]
    public void No_production_file_names_WorkspaceOptions_a_configured_tenant()
    {
        var mcp = Root();
        var sources = Directory.EnumerateDirectories(mcp, "Workman.*")
            .Where(project => !Path.GetFileName(project).Equals("Workman.Tests", StringComparison.Ordinal))
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.True(sources.Count > 50, $"found only {sources.Count} sources under {mcp}");

        var offenders = sources
            .Where(file => File.ReadAllText(file).Contains("WorkspaceOptions", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(mcp, file))
            .Where(file => !Allowed.Contains(file))
            .ToList();

        Assert.True(offenders.Count == 0, $"WorkspaceOptions in production code: {string.Join(", ", offenders)}");
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Workman.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"no Workman.slnx above {AppContext.BaseDirectory}");
    }
}
