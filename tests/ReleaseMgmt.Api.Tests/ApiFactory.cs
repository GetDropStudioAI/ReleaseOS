using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ReleaseMgmt.Api.Tests;

public sealed class ApiFactory(string environment = "Development", bool demoData = false) : WebApplicationFactory<Program>
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reos-api-").FullName;
    public string DbPath => Path.Combine(_dir, "app.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        var web = Path.Combine(_dir, "wwwroot");
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "index.html"), "<html>spa</html>");
        File.WriteAllText(Path.Combine(web, "probe.js"), "// asset");
        builder.UseWebRoot(web);
        // UseSetting, not ConfigureAppConfiguration: Program reads these while building, before late config sources apply.
        builder.UseSetting("Db:Path", DbPath);
        builder.UseSetting("Seed:Demo", demoData ? "true" : "false");
        builder.UseSetting("Backup:Directory", Path.Combine(_dir, "bk"));
        builder.UseSetting("Logging:File", Path.Combine(_dir, "log-.txt"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
