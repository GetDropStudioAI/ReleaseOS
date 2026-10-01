using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Api.Exports;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Infrastructure.Services;
using ReleaseMgmt.Infrastructure.Analytics;
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
// REOS-70: preserveStaticLogger gives each host its own logger (disposed with the host) and leaves the static Log.Logger alone, so hosts in one process
// (the in-process test servers) never write to each other's file. One production host behaves as before: same configuration, enrichers and file sink.
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.With<IcsTokenRedactor>()   // SEC-B10: feed tokens never reach a sink, whatever the levels or the format
    .Enrich.With<LogSanitizer>()   // security review SEC-D5: one event is one line, whatever a logged value holds
    .WriteTo.File(ctx.Configuration["Logging:File"] ?? "logs/releasemgmt-.log", rollingInterval: RollingInterval.Day),
    preserveStaticLogger: true);

var config = builder.Configuration;
AllowedHostsGuard.Enforce(builder.Environment, config);   // REOS-68: outside Development, AllowedHosts must name the host (fail fast)
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
builder.Services.AddSingleton<ReleaseMgmt.Api.Realtime.HubConnections>();   // REOS-63: live connections by user and session
builder.Services.AddHostedService<ReleaseMgmt.Api.Realtime.HubSessionMonitor>();   // REOS-63: Realtime:SessionRecheckSeconds (default 30)
builder.Services.AddSingleton<INotifier, Notifier>();
builder.Services.AddSingleton<UserProvisioner>();
builder.Services.AddSingleton<IdpIdentityBinder>();   // SEC-B8: organisation sign-in matches the IdP issuer + subject (Q-SEC-B8)
builder.Services.AddSingleton<SessionValidator>();   // REOS-53
builder.Services.AddSingleton<SessionLifetime>();    // SEC-B4/B5: idle and absolute limits, server-side sign-out
builder.Services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>, KeyRingPermissions>();   // SEC-B12
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<CookieAuthenticationOptions>, CookieProtectionPurpose>();   // SEC-B11
builder.Services.AddSingleton<TrainLifecycleService>();
builder.Services.AddSingleton<GateService>();
builder.Services.AddSingleton<WaiverService>();
builder.Services.AddSingleton<TaskService>();
builder.Services.AddSingleton<BaselineService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<WindowService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<RunbookService>();
builder.Services.AddSingleton<PlanStructureService>();   // REOS-81
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<ForecastService>();
builder.Services.AddSingleton<TaskParserService>();
builder.Services.AddSingleton<ChangeRecordService>();
builder.Services.AddSingleton<GoNoGoService>();
builder.Services.AddSingleton<FreezeService>();
builder.Services.AddSingleton<CloseoutService>();
builder.Services.AddHostedService<ReleaseMgmt.Api.Realtime.SessionStateJanitor>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<TemplateService>();   // REOS-38
builder.Services.AddSingleton<TrainCreationService>();   // REOS-80 new train: blank, from template, clone
builder.Services.AddSingleton<IReadinessService, ReadinessService>();
builder.Services.AddSingleton<SeedService>();
// REOS-35 evidence attachments: stored outside wwwroot (Attachments:Directory), capped at Attachments:MaxBytes (never above the 50 MB schema CHECK)
builder.Services.AddSingleton(new AttachmentOptions(config["Attachments:Directory"] ?? AttachmentOptions.DefaultDirectory, config.GetValue("Attachments:MaxBytes", AttachmentOptions.HardMaxBytes)));
builder.Services.AddSingleton<AttachmentService>();
builder.Services.AddSingleton<AuditQueryService>();   // REOS-38 audit viewer (read-only)
builder.Services.AddSingleton<AnalyticsService>();   // REOS-46 analytics M1-M15 (read-only, own connection)
builder.Services.AddSingleton<IAnalyticsConnectionFactory>(new SqliteAnalyticsConnectionFactory(connectionString));
builder.Services.AddSingleton<ReleaseMgmt.Api.Sync.SyncHealthService>();   // REOS-42 sync health, connector-wide banner state, webhook allowlist
builder.Services.AddCommunications();   // REOS-43/44 comm library, T-minus schedule, token hydration
builder.Services.AddSyncEngine(config, builder.Environment.IsDevelopment());   // REOS-39/40/41: ITSM connectors, poller, watchdog, credentials (Data Protection)
// REOS-74 (Q-052e): the key ring is encrypted at rest (certificate, or DPAPI on Windows); outside Development an unprotected ring refuses to start unless opted in.
var keyRing = KeyRingEncryption.Configure(builder.Services.AddDataProtection(), config, builder.Environment);
builder.Services.AddSingleton<CalendarService>(); builder.Services.AddSingleton<IcsFeedService>(); builder.Services.AddSingleton<IcsTokenService>();   // REOS-51 calendar + ICS feeds
builder.Services.AddPdfExports(config, builder.Environment.IsDevelopment());   // REOS-50: PDF export jobs + worker (QuestPDF)
builder.Services.AddProxyHeaders(config);   // SEC-E5: X-Forwarded-For/Proto from trusted proxies only
builder.Services.AddExceptionHandler<DbRuleExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(new BackupOptions(connectionString, config["Backup:Directory"] ?? "data/backups", BackupOptions.DefaultInterval));
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
builder.Services.AddNotificationScheduling(config);   // REOS-37: reminders, escalation, inbox, My work, team webhooks
builder.Services.AddCommDispatch(config);   // REOS-45: comm dispatch service, webhook sender, fail-closed default renderer
builder.Services.AddSingleton<ReleaseMgmt.Infrastructure.Comms.ICommDispatchRenderer, ReleaseMgmt.Api.Comms.CommHydrationAdapter>();   // dispatch renders through the REOS-44 hydrator (wins over the fail-closed default)
builder.Services.AddExchange(config, dbPath);   // REOS-48/49: CSV import (preview/commit), CSV/XLSX grid exports

var roleMap = config.GetSection("Auth:RoleMap").Get<Dictionary<string, string>>() ?? new();
var defaultRole = OidcSignIn.DefaultRole(config);   // SEC-B9: unset = an identity in no mapped group is refused
var authority = config["Auth:Oidc:Authority"];
// SEC-B3: outside Development the cookie is Secure and __Host- prefixed whatever scheme reached Kestrel (the runbook puts a TLS proxy in front of plain http).
var httpsCookie = config.GetValue("Auth:Cookie:RequireHttps", !builder.Environment.IsDevelopment());
// SEC-B13: the cookie scheme also answers challenges (401, which the SPA turns into its sign-in page). An OIDC default challenge redirected every background
// API and hub call to the identity provider and planted nonce/correlation cookies each time; organisation sign-in starts only at GET /auth/login.
var auth = builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
}).AddCookie(o =>
{
    o.Cookie.Name = httpsCookie ? "__Host-releasemgmt.auth" : "releasemgmt.auth";
    o.Cookie.SecurePolicy = httpsCookie ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;   // REOS-53: stated, not left to the framework default (Q-053b)
    o.ExpireTimeSpan = SessionLifetime.Idle(config);   // SEC-B4: idle timeout (sliding); the absolute limit is checked below
    o.SlidingExpiration = true;
    o.Events.OnSigningIn = async ctx =>
    {
        await ctx.HttpContext.RequestServices.GetRequiredService<SessionLifetime>().OnSigningIn(ctx);
        SecurityEvents.SignedIn(ctx);   // SEC-E4
    };
    o.Events.OnValidatePrincipal = async ctx =>
    {
        if (await ctx.HttpContext.RequestServices.GetRequiredService<SessionLifetime>().ValidateAsync(ctx))   // SEC-B4/B5
            await ctx.HttpContext.RequestServices.GetRequiredService<SessionValidator>().ValidateAsync(ctx);   // Q-053c
    };
    o.Events.OnRedirectToLogin = SecurityEvents.Unauthenticated;        // 401, never a redirect (SEC-E4 logs it at Debug)
    o.Events.OnRedirectToAccessDenied = SecurityEvents.AccessDenied;    // 403, never a redirect (SEC-E4 logs who and what)
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
        o.Events.OnTokenValidated = ctx => OidcSignIn.OnTokenValidated(ctx, roleMap, defaultRole);   // SEC-B7/B8/B9
        o.Events.OnRemoteFailure = OidcSignIn.OnRemoteFailure;   // REOS-65: a refused or failed sign-in goes back to the sign-in page with a reason code, never a 500
    });
}
builder.Services.AddAuthorization(Policies.Configure);

var app = builder.Build();
if (keyRing.Warning is { } keyRingWarning) app.Logger.LogWarning("{Warning}", keyRingWarning);   // every start, until the ring is protected (REOS-74)
else app.Logger.LogInformation("Data Protection key ring protection: {Protector}", keyRing.Protector);
await using (var scope = app.Services.CreateAsyncScope())
{
    await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ReleaseDbContext>>().CreateDbContextAsync();
    await UpgradeGuard.EnsureNoManualUpgradePendingAsync(db);   // Q-SEC-B8m: an older database needs its db/upgrades script first
    await db.Database.MigrateAsync();
    // REOS-73 (Q-053e): webhook addresses stored before the change are encrypted now; idempotent, and before anything reads WebhookDestinations.
    await ReleaseMgmt.Infrastructure.Comms.WebhookUrlProtectionUpgrade.RunAsync(connectionString, scope.ServiceProvider.GetRequiredService<ReleaseMgmt.Infrastructure.Comms.IWebhookUrlVault>(),
        app.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, app.Logger);
    var seed = scope.ServiceProvider.GetRequiredService<SeedService>();
    await seed.SeedReferenceDataAsync();
    if (config.GetValue("Seed:Demo", app.Environment.IsDevelopment())) await seed.SeedDemoDataAsync(); // demo trains only when asked (default: Development)
}
// Static files first: the fallback policy (authenticated user) applies to any request with no endpoint, so the SPA
// assets must be served before the authorization middleware or the sign-in page itself would 401.
app.UseForwardedHeaders();                    // SEC-E5: first, so every later check sees the real client and scheme
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();   // SEC-E5: browsers keep to https once they have reached the app over it
app.UseMiddleware<SecurityHeaders>();          // REOS-53
app.UseMiddleware<RequestBodyLimit>();         // security review SEC-D7: Limits:MaxRequestBodyBytes (Q-SEC-D1)
app.UseMiddleware<CrossSiteRequestGuard>();    // REOS-53: before authentication, so a cross-site write never reaches a handler
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", [AllowAnonymous] (IDbContextFactory<ReleaseDbContext> dbf, BackupService backup,
    ReleaseMgmt.Infrastructure.Sync.SyncPollerService poller, ReleaseMgmt.Infrastructure.Sync.SyncWatchdogService watchdog, CancellationToken ct) =>
    Health.CheckAsync(dbf, backup, poller.Heartbeat, watchdog.Heartbeat, app.Logger, ct));   // REOS-69: real poller and watchdog state; 503 when one has stalled

var devTools = DevSignIn.Enabled(app.Environment, config, app.Logger);   // SEC-B1: Development, and not beside organisation sign-in unless opted in
app.MapHub<ReleaseMgmt.Api.Realtime.TrainsHub>(ReleaseMgmt.Api.Realtime.TrainsHub.Path);
var api = app.MapGroup("/api/v1").AddEndpointFilterFactory(FieldLimits.Filter);   // REOS-66: per-field length limits on every JSON body (Q-SEC-D1)
api.MapGet("/me", (ClaimsPrincipal u) => new
{
    id = u.FindFirstValue("uid"),
    email = u.FindFirstValue(ClaimTypes.Email),
    name = u.Identity?.Name,
    roles = u.FindAll(ClaimTypes.Role).Select(c => c.Value)
}).RequireAuthorization(Policies.Read);
api.MapLifecycle();
api.MapTrainQueries();
api.MapGovernance();
api.MapFreezes();
api.MapCloseout();
api.MapGet("/config", (IConfiguration c) => Results.Ok(new { displayTimeZone = c["Display:TimeZone"] ?? "America/Chicago" })).RequireAuthorization(Policies.Read);
if (devTools) api.MapDev();
api.MapSession();
api.MapRunbook();
api.MapPlanStructure();   // REOS-81
api.MapRuns();
api.MapParser();
api.MapAdmin();
api.MapConnectors();   // REOS-39
api.MapAttachments();
api.MapNotifications();
api.MapTemplates();   // REOS-38
api.MapTrainCreation();   // REOS-80
api.MapAudit();
api.MapAnalytics();   // REOS-46
api.MapAnalyticsExport();   // REOS-47
api.MapSync();   // REOS-42
if (devTools) api.MapSyncDev();   // REOS-42 dev-only seeding
api.MapComms();   // REOS-43/44
api.MapCommDispatch();   // REOS-45
api.MapCalendar();   // REOS-51
api.MapImports(); api.MapGridExports();   // REOS-48/49
api.MapPdfExports();   // REOS-50

// Dev-only fake login: POST /auth/dev-login {email, name, role}. Never mapped outside Development; see DevSignIn for when it exists and who may call it.
if (devTools)
{
    app.MapPost("/auth/dev-login", [AllowAnonymous] async (DevLogin req, HttpContext http) =>
    {
        if (!DevSignIn.IsLocal(http)) return DevSignIn.Refuse(http, app.Logger);   // SEC-B2: this machine only (also defeats DNS rebinding)
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
    app.Logger.LogWarning("Auth:PasswordResetUrl must be an absolute https URL; ignoring it");   // the host's logger: the static Log.Logger is not this host's (REOS-70)
    resetUrl = null;
}
app.MapGet("/auth/config", [AllowAnonymous] () => Results.Ok(new { organisationSignIn = authority is not null, passwordResetUrl = string.IsNullOrWhiteSpace(resetUrl) ? null : resetUrl }));
app.MapPost("/auth/logout", [AllowAnonymous] async (HttpContext http, SessionLifetime sessions) =>
{
    var session = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await sessions.SignedOutAsync(session.Properties, session.Principal, http.RequestAborted);   // SEC-B5: copies of this cookie die too, also after a restart (REOS-62); REOS-63: so do its live connections
    if (session.Succeeded) SecurityEvents.SignedOut(http, session.Principal);   // SEC-E4
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
