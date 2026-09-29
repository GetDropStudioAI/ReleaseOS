using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ReleaseMgmt.Infrastructure.Persistence;

public static class DbRegistration
{
    public static IServiceCollection AddReleaseMgmtDb(this IServiceCollection services, string connectionString) =>
        services
            .AddSingleton<SqliteConnectionInterceptor>()
            .AddDbContextFactory<AppDbContext>((sp, o) => o
                .UseSqlite(connectionString)
                .AddInterceptors(sp.GetRequiredService<SqliteConnectionInterceptor>()));
}
