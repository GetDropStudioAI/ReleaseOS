using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ReleaseMgmt.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef` only.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ReleaseDbContext>
{
    public ReleaseDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ReleaseDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
