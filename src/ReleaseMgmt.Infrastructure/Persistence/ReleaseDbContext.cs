using Microsoft.EntityFrameworkCore;

namespace ReleaseMgmt.Infrastructure.Persistence;

/// <summary>The application model: 47 tables mapped from db/schema.sql (see tools/gen_entities.py).
/// Relationships are intentionally not modelled as navigations; the database enforces them (schema.sql is the contract).</summary>
public class ReleaseDbContext(DbContextOptions options) : AppDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ModelMap.Apply(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}
