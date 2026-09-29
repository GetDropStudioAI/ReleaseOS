using Microsoft.EntityFrameworkCore;

namespace ReleaseMgmt.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcTextConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcTextConverter>();
        configurationBuilder.Properties<DateOnly>().HaveConversion<DateTextConverter>();
        configurationBuilder.Properties<DateOnly?>().HaveConversion<DateTextConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // EF's default SQLite save path uses RETURNING, unsupported on tables with AFTER triggers (CLAUDE.md rule 1).
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => e.GetTableName() is not null))
        {
            modelBuilder.Entity(entity.ClrType).ToTable(entity.GetTableName()!, t => t.UseSqlReturningClause(false));
        }
    }
}
