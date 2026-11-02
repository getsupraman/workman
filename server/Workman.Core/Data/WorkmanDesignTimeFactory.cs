using Microsoft.EntityFrameworkCore.Design;

namespace Workman.Core.Data;

/// <summary>
/// Exists so `dotnet ef migrations add` can build the model. The connection string is a placeholder
/// and is never opened — migrations are generated here and applied by the host or by the tests.
/// </summary>
public sealed class WorkmanDesignTimeFactory : IDesignTimeDbContextFactory<WorkmanDbContext>
{
    public WorkmanDbContext CreateDbContext(string[] args)
        => new(WorkmanDbContextOptions.Build(
            "Host=localhost;Port=5432;Database=workstate_design_time;Username=workstate;Password=workstate"));
}
