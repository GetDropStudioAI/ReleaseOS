using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ReleaseMgmt.Api.Tests;

public sealed class ApiFactory(string environment = "Development") : WebApplicationFactory<Program>
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reos-api-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        var web = Path.Combine(_dir, "wwwroot");
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "index.html"), "<html>spa</html>");
        File.WriteAllText(Path.Combine(web, "probe.js"), "// asset");
        builder.UseWebRoot(web);
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Db:Path"] = Path.Combine(_dir, "app.db"),
            ["Backup:Directory"] = Path.Combine(_dir, "bk"),
            ["Logging:File"] = Path.Combine(_dir, "log-.txt"),
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
