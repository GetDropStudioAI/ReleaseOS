using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Infrastructure.Services;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Backup;
using ReleaseMgmt.Infrastructure.Persistence;
using Serilog;

// CLI verb: `ReleaseMgmt.Api restore <backupFile> <targetDb>` (D3). Runs before the host is built.
if (args is ["restore", var backupFile, var targetDb])
{
    BackupRunner.Restore(backupFile, targetDb);
    Console.WriteLine($"Restored {backupFile} -> {targetDb}; integrity_check: {BackupRunner.IntegrityCheck(targetDb)}");
    return 0;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.File(ctx.Configuration["Logging:File"] ?? "logs/releasemgmt-.log", rollingInterval: RollingInterval.Day));

var config = builder.Configuration;
var dbPath = config["Db:Path"] ?? "data/releasemgmt.db";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
var connectionString = $"Data Source={dbPath}";

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddReleaseMgmtDb(connectionString);
builder.Services.AddSingleton<IAlertSink, LoggingAlertSink>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, ReleaseMgmt.Api.Realtime.UidUserIdProvider>();
builder.Services.AddSingleton<IRealtimePublisher, ReleaseMgmt.Api.Realtime.SignalRPublisher>();   // services take it as an optional ctor argument
builder.Services.AddHostedService<ReleaseMgmt.Api.Realtime.ServerTimeBroadcaster>();
builder.Services.AddSingleton<INotifier, Notifier>();
builder.Services.AddSingleton<UserProvisioner>();
builder.Services.AddSingleton<TrainLifecycleService>();
builder.Services.AddSingleton<GateService>();
builder.Services.AddSingleton<WaiverService>();
builder.Services.AddSingleton<TaskService>();
builder.Services.AddSingleton<BaselineService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<IReadinessService, ReadinessService>();
builder.Services.AddSingleton<SeedService>();
builder.Services.AddExceptionHandler<DbRuleExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(new BackupOptions(connectionString, config["Backup:Directory"] ?? "data/backups", BackupOptions.DefaultInterval));
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());

var roleMap = config.GetSection("Auth:RoleMap").Get<Dictionary<string, string>>() ?? new();
var authority = config["Auth:Oidc:Authority"];
var auth = builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    if (authority is not null) o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
}).AddCookie(o =>
{
    o.Cookie.Name = "releasemgmt.auth";
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
if (authority is not null)
{
    auth.AddOpenIdConnect(o =>
    {
        o.Authority = authority;
        o.ClientId = config["Auth:Oidc:ClientId"];
        o.ClientSecret = config["Auth:Oidc:ClientSecret"]; // supplied via env / secret store, never committed (rule 11)
        o.ResponseType = "code";
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = true;
        o.Scope.Add("profile"); o.Scope.Add("email");
        o.Events.OnTokenValidated = async ctx =>
        {
            if (ctx.Principal?.Identity is not ClaimsIdentity id) return;
            RoleMapper.AddRoles(id, roleMap);
            var email = id.FindFirst(ClaimTypes.Email)?.Value ?? id.FindFirst("email")?.Value ?? id.FindFirst("preferred_username")?.Value;
            if (email is null) { ctx.Fail("The identity provider returned no email claim"); return; }
            var role = id.FindAll(ClaimTypes.Role).Select(c => c.Value).OrderBy(r => Array.IndexOf(Roles.All, r)).First();
            var uid = await ctx.HttpContext.RequestServices.GetRequiredService<UserProvisioner>()
                .UpsertAsync(email, id.FindFirst("name")?.Value ?? email, role);
            id.AddClaim(new Claim("uid", uid));
        };
    });
}
builder.Services.AddAuthorization(Policies.Configure);

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ReleaseDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();
    var seed = scope.ServiceProvider.GetRequiredService<SeedService>();
    await seed.SeedReferenceDataAsync();
    if (config.GetValue("Seed:Demo", app.Environment.IsDevelopment())) await seed.SeedDemoDataAsync(); // demo trains only when asked (default: Development)
}
// Static files first: the fallback policy (authenticated user) applies to any request with no endpoint, so the SPA
// assets must be served before the authorization middleware or the sign-in page itself would 401.
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", [AllowAnonymous] async (IDbContextFactory<ReleaseDbContext> dbf, BackupService backup, CancellationToken ct) =>
{
    string db;
    try
    {
        await using var ctx = await dbf.CreateDbContextAsync(ct);
        await ctx.Database.ExecuteSqlRawAsync("SELECT 1", ct);
        db = "ok";
    }
    catch (Exception ex) { app.Logger.LogError(ex, "healthz DB check failed"); db = "failed"; }
    var body = new { status = db == "ok" ? "Healthy" : "Unhealthy", db, backup = backup.LastSuccessUtc, poller = "notConfigured", watchdog = "notConfigured" };
    return db == "ok" ? Results.Ok(body) : Results.Json(body, statusCode: 503);
});

app.MapHub<ReleaseMgmt.Api.Realtime.TrainsHub>(ReleaseMgmt.Api.Realtime.TrainsHub.Path);
var api = app.MapGroup("/api/v1");
api.MapGet("/me", (ClaimsPrincipal u) => new
{
    email = u.FindFirstValue(ClaimTypes.Email),
    name = u.Identity?.Name,
    roles = u.FindAll(ClaimTypes.Role).Select(c => c.Value)
}).RequireAuthorization(Policies.Read);
api.MapLifecycle();
api.MapTrainQueries();
api.MapAdmin();

// Dev-only fake login (config Auth:Oidc:*): POST /auth/dev-login {email, name, role}. Never mapped outside Development.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/auth/dev-login", [AllowAnonymous] async (DevLogin req, HttpContext http) =>
    {
        if (!Roles.All.Contains(req.Role)) return Results.BadRequest(new { message = $"Role must be one of {string.Join(", ", Roles.All)}" });
        var uid = await http.RequestServices.GetRequiredService<UserProvisioner>().UpsertAsync(req.Email, req.Name, req.Role);
        var id = new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, req.Email), new Claim(ClaimTypes.Name, req.Name), new Claim(ClaimTypes.Role, req.Role), new Claim("uid", uid)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id));
        return Results.Ok();
    });
}
// Password reset lives at the identity provider (Q-006 option a, D12): the app only links to it. MFA comes with the IdP's reset flow.
// Auth:PasswordResetUrl must be absolute https; anything else is ignored and logged so a bad value is visible, never silent.
var resetUrl = config["Auth:PasswordResetUrl"];
if (!string.IsNullOrWhiteSpace(resetUrl) && !(Uri.TryCreate(resetUrl, UriKind.Absolute, out var ru) && ru.Scheme == Uri.UriSchemeHttps))
{
    Log.Warning("Auth:PasswordResetUrl must be an absolute https URL; ignoring it");
    resetUrl = null;
}
app.MapGet("/auth/config", [AllowAnonymous] () => Results.Ok(new { organisationSignIn = authority is not null, passwordResetUrl = string.IsNullOrWhiteSpace(resetUrl) ? null : resetUrl }));
app.MapPost("/auth/logout", [AllowAnonymous] async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});
if (authority is not null)
    app.MapGet("/auth/login", [AllowAnonymous] () => Results.Challenge(new() { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));

app.MapFallbackToFile("index.html").AllowAnonymous();
app.Run();
return 0;

public record DevLogin(string Email, string Name, string Role);
public partial class Program;
