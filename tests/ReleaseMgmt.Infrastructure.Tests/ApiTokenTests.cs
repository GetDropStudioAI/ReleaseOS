using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-162 and REOS-163 below the HTTP layer: the token service, scope parsing, the audit attribution, the rate limiter and the idempotency store, all on the frozen clock.</summary>
public sealed class ApiTokenTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private static (OpsKit Kit, ApiTokenService Tokens, Actor Rte) Setup(TriggerSuiteFixture fx)
    {
        var kit = new OpsKit(fx);
        // the fixture's database has the people the trigger suite uses: rte (RTE, Rae T.), rm (ReleaseManager, Rel Mgr), dev (Viewer), all members of the default workspace
        return (kit, new ApiTokenService(kit.Db, kit.Time), new Actor("rte"));
    }

    [Fact]
    public void Scopes_parse_to_the_canonical_order_and_unknown_or_empty_ones_are_refused()
    {
        Assert.Equal(["read", "write"], ApiTokenScopes.Parse(["write", "read", "write"]));
        Assert.Equal(["read", "write", "approvals", "deployments"], ApiTokenScopes.Parse(["deployments", "approvals", "write", "read"]));
        Assert.Null(ApiTokenScopes.Parse([]));
        Assert.Null(ApiTokenScopes.Parse(null));
        Assert.Null(ApiTokenScopes.Parse(["read", "admin"]));
        Assert.Null(ApiTokenScopes.Parse(["Read"]));
        Assert.Equal(["read", "write"], ApiTokenScopes.Split("read,write"));
        Assert.Equal("read,write", ApiTokenScopes.Join(["read", "write"]));
    }

    [Theory]
    [InlineData("rt_" + "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG", true)]
    [InlineData("rk_abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG", false)]   // an ingest key
    [InlineData("rt_short", false)]
    [InlineData("rt_has space in it xxxxxxxxxxxxxxxxxxxxxxxxxxxx", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_text_shaped_like_a_token_is_looked_up(string? text, bool shaped) => Assert.Equal(shaped, ApiTokenService.LooksLikeToken(text));

    [Fact]
    public async Task A_token_is_made_with_a_hash_a_prefix_and_an_audit_row_and_never_the_secret_in_either()
    {
        var (kit, tokens, rte) = Setup(fx);
        var r = await tokens.CreateAsync("  Dashboard  ", ["write", "read"], null, rte);
        Assert.True(r.IsOk);
        var secret = r.Value!.Secret;
        Assert.StartsWith("rt_", secret);
        Assert.Equal(46, secret.Length);   // rt_ + 43 base64url characters of 256 bits
        Assert.Equal("Dashboard", r.Value.Token.Name);
        Assert.Equal(["read", "write"], r.Value.Token.Scopes);
        Assert.Equal("Active", r.Value.Token.State);
        Assert.Equal("Rae T.", r.Value.Token.OwnerName);
        Assert.Equal(ApiTokenService.Hash(secret), kit.Text("SELECT TokenSha256 FROM ApiTokens"));
        Assert.Equal(secret[..8], kit.Text("SELECT TokenPrefix FROM ApiTokens"));
        Assert.Equal(1, kit.Audits("ApiToken", "Create"));
        Assert.Equal(0, kit.Count("SELECT count(*) FROM AuditEvents WHERE AfterJson LIKE ? OR AfterJson LIKE ?", $"%{secret}%", $"%{ApiTokenService.Hash(secret)}%"));
        Assert.Equal(2, new HashSet<string>([secret, (await tokens.CreateAsync("b", ["read"], null, rte)).Value!.Secret]).Count);
    }

    [Theory]
    [InlineData("", "read", false)]
    [InlineData("   ", "read", false)]
    [InlineData("x", "", false)]
    [InlineData("x", "read,bogus", false)]
    [InlineData("x", "read", true)]
    public async Task A_token_needs_a_name_and_known_scopes(string name, string scopes, bool ok)
    {
        var (kit, tokens, rte) = Setup(fx);
        var r = await tokens.CreateAsync(name, scopes.Split(',', StringSplitOptions.RemoveEmptyEntries), null, rte);
        Assert.Equal(ok, r.IsOk);
        if (!ok) Assert.Equal("ApiTokenInvalid", r.Failures[0].Guard);
        Assert.Equal(ok ? 1 : 0, kit.Count("SELECT count(*) FROM ApiTokens"));
    }

    [Fact]
    public async Task The_name_is_at_most_100_characters_and_the_expiry_must_be_in_the_future()
    {
        var (_, tokens, rte) = Setup(fx);
        Assert.False((await tokens.CreateAsync(new string('n', 101), ["read"], null, rte)).IsOk);
        Assert.True((await tokens.CreateAsync(new string('n', 100), ["read"], null, rte)).IsOk);
        Assert.False((await tokens.CreateAsync("x", ["read"], OpsKit.T0, rte)).IsOk);   // now is not the future
        Assert.False((await tokens.CreateAsync("x", ["read"], OpsKit.T0.AddSeconds(-1), rte)).IsOk);
        Assert.True((await tokens.CreateAsync("x", ["read"], OpsKit.T0.AddSeconds(1), rte)).IsOk);
    }

    [Fact]
    public async Task Someone_outside_the_workspace_cannot_make_a_token()
    {
        var (kit, tokens, _) = Setup(fx);
        kit.Sql("INSERT INTO Users(Id,Email,DisplayName,Role) VALUES('u-out','out@x.com','Out','RTE')");
        var r = await tokens.CreateAsync("x", ["read"], null, new Actor("u-out"));
        Assert.False(r.IsOk);
        Assert.Equal("ApiTokenOwnerNotMember", r.Failures[0].Guard);
    }

    [Fact]
    public async Task Authentication_reads_the_role_the_person_holds_now_and_stamps_last_used_once_a_minute()
    {
        var (kit, tokens, rte) = Setup(fx);
        var secret = (await tokens.CreateAsync("CI", ["read", "deployments"], null, rte)).Value!.Secret;
        var a = await tokens.AuthenticateAsync(secret);
        Assert.True(a.Ok);
        Assert.Equal("RTE", a.Principal!.Role);
        Assert.Equal("rte", a.Principal.OwnerUserId);
        Assert.Equal("default", a.Principal.WorkspaceId);
        Assert.Equal(new[] { "deployments", "read" }, a.Principal.Scopes.Order().ToArray());
        Assert.Equal(OpsKit.Iso(OpsKit.T0), kit.Text("SELECT LastUsedAt FROM ApiTokens"));
        kit.Time.Advance(TimeSpan.FromSeconds(59));
        await tokens.AuthenticateAsync(secret);
        Assert.Equal(OpsKit.Iso(OpsKit.T0), kit.Text("SELECT LastUsedAt FROM ApiTokens"));
        kit.Time.Advance(TimeSpan.FromSeconds(1));
        await tokens.AuthenticateAsync(secret);
        Assert.Equal(OpsKit.Iso(OpsKit.T0.AddSeconds(60)), kit.Text("SELECT LastUsedAt FROM ApiTokens"));
        kit.Sql("UPDATE WorkspaceMembers SET Role='Viewer' WHERE UserId='rte'");
        Assert.Equal("Viewer", (await tokens.AuthenticateAsync(secret)).Principal!.Role);   // the role is read on every request
    }

    [Fact]
    public async Task Each_way_a_token_stops_working_is_named()
    {
        var (kit, tokens, rte) = Setup(fx);
        var secret = (await tokens.CreateAsync("CI", ["read"], OpsKit.T0.AddHours(1), rte)).Value!;
        Assert.Equal(ApiTokenDenial.Malformed, (await tokens.AuthenticateAsync("nope")).Denial);
        Assert.Equal(ApiTokenDenial.Unknown, (await tokens.AuthenticateAsync("rt_" + new string('z', 43))).Denial);
        kit.Sql("UPDATE Users SET IsActive=0 WHERE Id='rte'");
        var inactive = await tokens.AuthenticateAsync(secret.Secret);
        Assert.Equal(ApiTokenDenial.OwnerInactive, inactive.Denial);
        Assert.Equal(secret.Token.Id, inactive.TokenId);   // known token: the denial can be audited in its workspace
        kit.Sql("UPDATE Users SET IsActive=1 WHERE Id='rte'; DELETE FROM WorkspaceMembers WHERE UserId='rte'");
        Assert.Equal(ApiTokenDenial.OwnerNotMember, (await tokens.AuthenticateAsync(secret.Secret)).Denial);
        kit.Sql("INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('default','rte','RTE','2026-10-01T00:00:00Z')");
        Assert.True((await tokens.AuthenticateAsync(secret.Secret)).Ok);
        kit.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(ApiTokenDenial.Expired, (await tokens.AuthenticateAsync(secret.Secret)).Denial);
        var revoked = (await tokens.CreateAsync("CI2", ["read"], null, rte)).Value!;
        Assert.True((await tokens.RevokeAsync(revoked.Token.Id, rte, null)).IsOk);
        Assert.Equal(ApiTokenDenial.Revoked, (await tokens.AuthenticateAsync(revoked.Secret)).Denial);
        var live = (await tokens.CreateAsync("CI3", ["read"], null, rte)).Value!;
        Assert.True((await tokens.AuthenticateAsync(live.Secret)).Ok);
        kit.Sql("UPDATE Workspaces SET ArchivedAt='2026-10-20T00:00:00Z' WHERE Id='default'");
        Assert.Equal(ApiTokenDenial.WorkspaceArchived, (await tokens.AuthenticateAsync(live.Secret)).Denial);
    }

    [Fact]
    public async Task Rotate_replaces_the_hash_hands_over_the_token_and_audits_without_the_secret()
    {
        var (kit, tokens, rte) = Setup(fx);
        var made = (await tokens.CreateAsync("CI", ["read"], null, rte)).Value!;
        var stale = await tokens.RotateAsync(made.Token.Id, null, new Actor("rm"), 5);
        Assert.Equal(ResultKind.Conflict, stale.Kind);
        Assert.DoesNotContain("TokenSha256", System.Text.Json.JsonSerializer.Serialize(stale.Current));
        var r = await tokens.RotateAsync(made.Token.Id, null, new Actor("rm"), 1);
        Assert.True(r.IsOk);
        Assert.NotEqual(made.Secret, r.Value!.Secret);
        Assert.Equal(2, r.Value.Token.Version);
        Assert.Equal("Rel Mgr", r.Value.Token.OwnerName);
        Assert.False((await tokens.AuthenticateAsync(made.Secret)).Ok);
        var now = await tokens.AuthenticateAsync(r.Value.Secret);
        Assert.Equal("ReleaseManager", now.Principal!.Role);
        Assert.Equal(1, kit.Audits("ApiToken", "Rotate"));
        Assert.Equal(0, kit.Count("SELECT count(*) FROM AuditEvents WHERE AfterJson LIKE ? OR BeforeJson LIKE ?", $"%{r.Value.Secret}%", $"%{made.Secret}%"));
        Assert.Equal(ResultKind.NotFound, (await tokens.RotateAsync("nope", null, rte, null)).Kind);
    }

    [Fact]
    public async Task Revoke_is_audited_once_and_keeps_who_revoked_it()
    {
        var (kit, tokens, rte) = Setup(fx);
        var made = (await tokens.CreateAsync("CI", ["read"], null, rte)).Value!;
        Assert.Equal(ResultKind.Conflict, (await tokens.RevokeAsync(made.Token.Id, rte, 9)).Kind);
        var first = await tokens.RevokeAsync(made.Token.Id, rte, 1);
        Assert.Equal("Revoked", first.Value!.State);
        var again = await tokens.RevokeAsync(made.Token.Id, new Actor("rm"), null);
        Assert.True(again.IsOk);
        Assert.Equal("Rae T.", again.Value!.RevokedBy);
        Assert.Equal(1, kit.Audits("ApiToken", "Revoke"));
        Assert.Equal(2, again.Value.Version);
        Assert.Equal("Revoked", (await tokens.ListAsync())[0].State);
    }

    [Fact]
    public async Task A_denied_use_is_one_audit_row_for_the_person_in_the_workspace_and_a_missing_token_is_host_wide()
    {
        var (kit, tokens, rte) = Setup(fx);
        var made = (await tokens.CreateAsync("CI", ["read"], null, rte)).Value!;
        var who = new ApiTokenAuthentication(null, ApiTokenDenial.None, made.Token.Id, "CI", "default", "rte");
        await tokens.RecordDeniedAsync(who, "missing scope write", "POST", "/api/public/v1/gates/g1:start", "write");
        Assert.Equal("rte", kit.Text("SELECT ActorUserId FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Denied'"));
        Assert.Equal("default", kit.Text("SELECT WorkspaceId FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Denied'"));
        Assert.Contains("\"requiredScope\":\"write\"", kit.Text("SELECT AfterJson FROM AuditEvents WHERE Action='Denied'"));
        await tokens.RecordDeniedAsync(new ApiTokenAuthentication(null, ApiTokenDenial.Unknown), "unknown token", "GET", "/x", null);   // no token id: nothing to file it under
        Assert.Equal(1, kit.Audits("ApiToken", "Denied"));
        await tokens.RecordRejectedAsync("10.0.0.1", "unknown token");
        Assert.Equal("", kit.Text("SELECT COALESCE(WorkspaceId,'') FROM AuditEvents WHERE Action='Rejected'"));
        Assert.DoesNotContain("rt_", kit.Text("SELECT AfterJson FROM AuditEvents WHERE Action='Rejected'"));
    }

    [Fact]
    public async Task A_service_write_under_a_token_names_it_and_carries_the_token_in_the_audit_row_and_an_explicit_actor_wins()
    {
        var (kit, tokens, rte) = Setup(fx);
        var made = (await tokens.CreateAsync("Release bot", ["read"], null, rte)).Value!;
        Assert.Equal("{\"a\":1}", ApiCallContext.WithVia("{\"a\":1}"));   // outside a token call: unchanged
        Assert.Null(ApiCallContext.WithVia(null));
        using (ApiCallContext.Enter(made.Token.Id, "Release bot", "rte"))
        {
            Assert.Equal($"{{\"a\":1,\"_via\":{{\"apiTokenId\":\"{made.Token.Id}\",\"apiToken\":\"Release bot\"}}}}", ApiCallContext.WithVia("{\"a\":1}"));
            Assert.Equal($"{{\"_via\":{{\"apiTokenId\":\"{made.Token.Id}\",\"apiToken\":\"Release bot\"}}}}", ApiCallContext.WithVia(null));
            Assert.Contains("\"value\":[1,2]", ApiCallContext.WithVia("[1,2]"));
            var rev = await tokens.RevokeAsync(made.Token.Id, new Actor("rm"), null);
            Assert.True(rev.IsOk);
        }
        Assert.Null(ApiCallContext.Current);
        Assert.Contains("_via", kit.Text("SELECT AfterJson FROM AuditEvents WHERE Action='Revoke'"));
        Assert.Equal("rm", kit.Text("SELECT ActorUserId FROM AuditEvents WHERE Action='Revoke'"));   // an explicit actor wins
    }

    // ---- REOS-163 ------------------------------------------------------------------------------------------------------------------

    private static (OpsKit Kit, IdempotencyService Idem, string TokenId) IdemSetup(TriggerSuiteFixture fx)
    {
        var (kit, tokens, rte) = Setup(fx);
        var id = tokens.CreateAsync("CI", ["read"], null, rte).GetAwaiter().GetResult().Value!.Token.Id;
        return (kit, new IdempotencyService(kit.Db, kit.Time, new PublicApiOptions()), id);
    }

    private static readonly string H1 = new('a', 64), H2 = new('b', 64);

    [Fact]
    public async Task The_first_request_starts_a_repeat_waits_or_replays_and_another_request_is_refused()
    {
        var (kit, idem, tid) = IdemSetup(fx);
        var first = await idem.BeginAsync("default", tid, "k1", H1, default);
        Assert.Equal(IdempotencyOutcome.Started, first.Outcome);
        Assert.Equal(IdempotencyOutcome.InProgress, (await idem.BeginAsync("default", tid, "k1", H1, default)).Outcome);
        Assert.Equal(IdempotencyOutcome.Mismatch, (await idem.BeginAsync("default", tid, "k1", H2, default)).Outcome);
        await idem.CompleteAsync("default", first.Id!, 201, "application/json", "{\"id\":1}", default);
        var replay = await idem.BeginAsync("default", tid, "k1", H1, default);
        Assert.Equal(IdempotencyOutcome.Replay, replay.Outcome);
        Assert.Equal((201, "application/json", "{\"id\":1}"), (replay.StatusCode, replay.ContentType, replay.Body));
        Assert.Equal(IdempotencyOutcome.Mismatch, (await idem.BeginAsync("default", tid, "k1", H2, default)).Outcome);
        Assert.Equal(IdempotencyOutcome.Started, (await idem.BeginAsync("default", tid, "k2", H1, default)).Outcome);   // another key
        Assert.Equal(2, kit.Count("SELECT count(*) FROM IdempotencyKeys"));
    }

    [Fact]
    public async Task An_abandoned_key_runs_again_an_expired_one_is_forgotten_and_a_token_takes_its_keys_with_it()
    {
        var (kit, idem, tid) = IdemSetup(fx);
        var a = await idem.BeginAsync("default", tid, "k1", H1, default);
        await idem.AbandonAsync("default", a.Id!, default);
        Assert.Equal(IdempotencyOutcome.Started, (await idem.BeginAsync("default", tid, "k1", H1, default)).Outcome);
        var b = await idem.BeginAsync("default", tid, "k3", H1, default);
        await idem.CompleteAsync("default", b.Id!, 200, null, "", default);
        kit.Time.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(IdempotencyOutcome.Started, (await idem.BeginAsync("default", tid, "k3", H2, default)).Outcome);   // forgotten: another request may take the key
        Assert.Equal(1, kit.Count("SELECT count(*) FROM IdempotencyKeys WHERE KeyValue='k3'"));
        kit.Sql("DELETE FROM ApiTokens WHERE Id=?", tid);
        Assert.Equal(0, kit.Count("SELECT count(*) FROM IdempotencyKeys"));
    }

    [Fact]
    public async Task Concurrent_first_requests_with_one_key_start_exactly_one()
    {
        var (kit, idem, tid) = IdemSetup(fx);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => idem.BeginAsync("default", tid, "race", H1, default))));
        Assert.Equal(1, outcomes.Count(o => o.Outcome == IdempotencyOutcome.Started));
        Assert.Equal(7, outcomes.Count(o => o.Outcome == IdempotencyOutcome.InProgress));
        Assert.Equal(1, kit.Count("SELECT count(*) FROM IdempotencyKeys"));
    }
}
