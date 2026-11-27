using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Ai;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Tests.Ai;

/// <summary>A provider that answers every call with the same usage (so every cost is exact), optionally slowly (so calls overlap), and counts the calls it was really asked to make.</summary>
internal sealed class FixedUsageProvider(long inputTokens = 100, long outputTokens = 180, int delayMs = 0) : IAiProvider
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public Func<Exception?>? Throw { get; set; }
    /// <summary>Completes when the first call has reached the provider (so a test can act exactly while a call is in flight).</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Name => "fixed";
    public bool RequiresKey => false;
    public async Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Entered.TrySetResult();
        if (delayMs > 0) await Task.Delay(delayMs, ct);
        if (Throw?.Invoke() is { } ex) throw ex;
        return new AiProviderResponse("ok", inputTokens, outputTokens, ServedModel: request.Model);
    }
    public async Task<IReadOnlyList<AiProviderResponse?>> CompleteBatchAsync(IReadOnlyList<AiProviderRequest> requests, CancellationToken ct)
    {
        Interlocked.Add(ref _calls, requests.Count);
        if (delayMs > 0) await Task.Delay(delayMs, ct);
        return [.. requests.Select(r => (AiProviderResponse?)new AiProviderResponse("ok", inputTokens, outputTokens, ServedModel: r.Model))];
    }
}

internal sealed class CapturingNotifier : INotifier
{
    private readonly List<NotificationRequest> _sent = [];
    public IReadOnlyList<NotificationRequest> Sent { get { lock (_sent) return [.. _sent]; } }
    public Task<string> NotifyAsync(NotificationRequest request, CancellationToken ct = default) { lock (_sent) _sent.Add(request); return Task.FromResult("n-" + _sent.Count); }
}

/// <summary>
/// REOS-185 (D57, D58): the AI allowance on a migrated database with the frozen test clock, the real allowance service, the real action log and the real governed client over a provider with fixed usage.
/// Numbers: the model is priced 2 USD in / 10 USD out per million tokens, so a call of 100 in and 180 out costs exactly 2,000 micro-dollars; a request with a 190-token output budget reserves 1,902
/// (the worst case: 1 input token at half a character each, plus the whole budget); the included allowance is 2 credits = 20,000 micro-dollars, the soft cap 80% (16,000), chat's share 50% (10,000).
/// Users rte and rm are the administrators who are told; dev and gov1 ask for AI help. The database rules behind it are in tests/reference/test_schema.py.
/// </summary>
public sealed class AiAllowanceTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private const string Ws = "default";
    private const long Total = 20_000, Cost = 2_000, Estimate = 1_902;

    private sealed class Rig
    {
        public required OpsKit K { get; init; }
        public required FixedUsageProvider Provider { get; init; }
        public required AiActionService Log { get; init; }
        public required AiAllowanceService Allowance { get; init; }
        public required GovernedAiClient Client { get; init; }
        public required TestMonitor<AiOptions> Options { get; init; }
        public required CapturingNotifier Notes { get; init; }
        public required CapturingLogger<AiAllowanceService> AllowanceLog { get; init; }
    }

    private Rig Make(Action<AiOptions>? configure = null, FixedUsageProvider? provider = null, IAiActionLog? logOverride = null)
    {
        var k = new OpsKit(fx);
        var o = new AiOptions { Provider = "fake", RetryBaseDelayMilliseconds = 0 };
        o.Models.Small = "small-model"; o.Models.Drafting = "drafting-model";
        o.Pricing.Models["small-model"] = new AiModelPriceOptions { InputPerMTok = 2, OutputPerMTok = 10 };
        o.Pricing.Models["drafting-model"] = new AiModelPriceOptions { InputPerMTok = 2, OutputPerMTok = 10 };
        o.Allowance.IncludedCredits = 2; o.Allowance.UserPerMinute = 0; o.Allowance.UserPerHour = 0;   // no rate limit unless a test asks for one
        configure?.Invoke(o);
        var options = new TestMonitor<AiOptions>(o);
        var notes = new CapturingNotifier();
        var allowLog = new CapturingLogger<AiAllowanceService>();
        var allowance = new AiAllowanceService(k.Db, k.Time, new ConfigAiAllowancePolicy(options), options, notes, allowLog);
        var log = new AiActionService(k.Db, k.Time);
        var p = provider ?? new FixedUsageProvider();
        var client = new GovernedAiClient(p, new MemoryKeys(), options, new DbAiWorkspaceSettings(k.Db), new StubGate(Ws), new NoopAiUsageSink(), logOverride ?? log, k.Time, NullLogger<GovernedAiClient>.Instance, allowance);
        return new Rig { K = k, Provider = p, Log = log, Allowance = allowance, Client = client, Options = options, Notes = notes, AllowanceLog = allowLog };
    }

    private static AiRequest Req(string feature = AiFeatures.RiskScore, string? user = "dev", int maxOutput = 190, string? instruction = null) =>
        new(Ws, feature, "S", instruction ?? "I", null, maxOutput, false, new AiCallMeta(user, "Train", "t1", "t1", "test/1"));

    private static async Task<List<AiResult>> Calls(Rig r, int n, string feature = AiFeatures.RiskScore, string? user = "dev")
    {
        var list = new List<AiResult>();
        for (var i = 0; i < n; i++) list.Add(await r.Client.CompleteAsync(Req(feature, user)));
        return list;
    }

    private static ReleaseDbContext Open(Rig r) => r.K.Db.CreateDbContext();

    private static async Task<List<AiAllowancePeriods>> Periods(Rig r)
    {
        using var _ = WorkspaceScope.Enter(Ws);
        await using var db = Open(r);
        return await db.Set<AiAllowancePeriods>().AsNoTracking().OrderBy(p => p.PeriodStart).ToListAsync();
    }

    private static async Task<List<AiUsageLedger>> Ledger(Rig r)
    {
        using var _ = WorkspaceScope.Enter(Ws);
        await using var db = Open(r);
        return await db.Set<AiUsageLedger>().AsNoTracking().OrderBy(l => l.ReservedAt).ThenBy(l => l.Id).ToListAsync();
    }

    private static async Task<List<string>> AuditActions(Rig r, string entityType)
    {
        using var _ = WorkspaceScope.Enter(Ws);
        await using var db = Open(r);
        return await db.Set<AuditEvents>().AsNoTracking().Where(a => a.EntityType == entityType).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
    }

    /// <summary>The books balance: each period's counters are exactly what its ledger says (nothing is lost, double-counted or left reserved).</summary>
    private static async Task AssertBooksBalance(Rig r)
    {
        var periods = await Periods(r);
        var ledger = await Ledger(r);
        foreach (var p in periods)
        {
            var mine = ledger.Where(l => l.PeriodId == p.Id).ToList();
            Assert.Equal(mine.Where(l => l.State == "Settled").Sum(l => l.CostMicroUsd), p.UsedMicroUsd);
            Assert.Equal(mine.Where(l => l.State == "Reserved").Sum(l => l.ReservedMicroUsd), p.ReservedMicroUsd);
            Assert.Equal(mine.Where(l => l.State == "Settled" && l.Class == "Chat").Sum(l => l.CostMicroUsd), p.ChatUsedMicroUsd);
            Assert.Equal(mine.Where(l => l.State == "Reserved" && l.Class == "Chat").Sum(l => l.ReservedMicroUsd), p.ChatReservedMicroUsd);
        }
    }

    // ---- the cost model ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cost_is_priced_by_model_family_with_cache_and_batch_and_always_rounded_up()
    {
        var m = new AiCostModel();
        Assert.Equal(600_000, m.Cost("claude-haiku-5-5", 1_000_000, 1_000_000));                 // 0.10 + 0.50 USD
        Assert.Equal(12_000_000, m.Cost("claude-sonnet-5-5", 1_000_000, 1_000_000));             // 2 + 10 USD
        Assert.Equal(24_000_000, m.Cost("claude-opus-5-5", 1_000_000, 1_000_000));               // 4 + 20 USD
        Assert.Equal(100_000, m.Cost("claude-sonnet-5-5", 0, 0, cacheReadTokens: 1_000_000));    // a cache read is 5% of fresh input
        Assert.Equal(2_500_000, m.Cost("claude-sonnet-5-5", 0, 0, cacheWriteTokens: 1_000_000)); // a cache write is 125%
        Assert.Equal(6_000_000, m.Cost("claude-sonnet-5-5", 1_000_000, 1_000_000, batch: true)); // the batch path is half
        Assert.Equal(1, m.Cost("claude-haiku-5-5", 1, 0));                                       // 0.1 micro-dollar is still a micro-dollar: no call is ever free
        Assert.Equal(0, m.Cost("claude-sonnet-5-5", 0, 0));
        Assert.Equal(m.Cost("claude-haiku-5-5", 1234, 567), m.Cost(new AiUsage("risk-score", "claude-haiku-5-5", 1234, 567, 0, 0, false)));
    }

    [Fact]
    public void An_unknown_model_is_priced_as_the_most_expensive_family_and_a_configured_price_wins()
    {
        var unknown = new AiCostModel();
        Assert.Equal(4_000_000, unknown.Cost("some-future-model", 1_000_000, 0));                // never cheaper than it is
        Assert.Equal(4_000_000, unknown.Cost(null, 1_000_000, 0));
        var custom = new AiCostModel(new Dictionary<string, AiModelPrice> { ["some-future-model"] = new(1m, 3m) });
        Assert.Equal(4_000_000, custom.Cost("SOME-FUTURE-MODEL", 1_000_000, 1_000_000));
        Assert.Equal(AiCostModel.Haiku, custom.PriceFor("claude-haiku-6-0"));                    // a new version of a known family needs no change
    }

    [Fact]
    public void A_reservation_is_never_below_what_the_call_can_cost()
    {
        var m = new AiCostModel(new Dictionary<string, AiModelPrice> { ["small-model"] = new(2m, 10m) });
        Assert.Equal(Estimate, m.Estimate("small-model", 2, 190, cacheSystem: false, batch: false));
        Assert.True(m.Estimate("small-model", 2, 190, true, false) >= m.Estimate("small-model", 2, 190, false, false));
        Assert.Equal(m.Estimate("small-model", 1000, 100, false, false) / 2, m.Estimate("small-model", 1000, 100, false, true));
        // any real answer within the budget, for text of at least two characters per token, costs no more than was held
        for (var chars = 2; chars <= 40_000; chars += 997)
            Assert.True(m.Cost("small-model", (chars + 1) / 2, 190) <= m.Estimate("small-model", chars, 190, false, false));
        Assert.Equal(1, new AiCostModel().Estimate("claude-haiku-5-5", 0, 0, false, false));
    }

    // ---- the allowance -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_call_of_a_month_opens_its_period_and_every_call_is_in_the_ledger_at_its_exact_cost()
    {
        var r = Make();
        Assert.Empty(await Periods(r));
        var before = await r.Allowance.GetAsync(Ws);
        Assert.False(before.Opened);
        Assert.Equal((Total, 0, "Ok"), (before.TotalMicroUsd, before.UsedMicroUsd, before.Level));

        var res = await r.Client.CompleteAsync(Req());
        Assert.True(res.IsCompleted);
        Assert.Equal(Cost, res.Metering!.CostMicroUsd);

        var p = Assert.Single(await Periods(r));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), Total, Cost, 0L, 1), (p.PeriodStart, p.PeriodEnd, p.IncludedMicroUsd, p.UsedMicroUsd, p.ReservedMicroUsd, p.SoftCapPercent / 80));
        var l = Assert.Single(await Ledger(r));
        Assert.Equal(("risk-score", "Standard", "small-model", "Settled", Cost, 100, 180), (l.Feature, l.Class, l.Model, l.State, l.CostMicroUsd, l.InputTokens, l.OutputTokens));
        Assert.Equal(res.ActionId, l.AiActionId);                                  // the ledger entry and the audit-trail record are the same call
        Assert.Equal("dev", l.RequestedByUserId);
        Assert.Equal(Estimate, l.ReservedMicroUsd);
        Assert.Equal(["PeriodOpened"], await AuditActions(r, "AiAllowance"));
        Assert.Equal(["Reserved", "Settled"], await AuditActions(r, "AiUsage"));   // rule 3: every write has its audit row
        await AssertBooksBalance(r);

        var view = await r.Allowance.GetAsync(Ws);
        Assert.Equal(("2026-10-01", "2026-11-01", Total - Cost, 10, "Ok"), (view.PeriodStart, view.PeriodEnd, view.RemainingMicroUsd, view.PercentUsed, view.Level));
        Assert.Equal("1 November 2026 (UTC)", view.ResetsOn);
        var f = view.Features.Single(x => x.Feature == "risk-score");
        Assert.Equal((1L, 0L, Cost, 100L, 180L), (f.Calls, f.Refused, f.CostMicroUsd, f.InputTokens, f.OutputTokens));
        Assert.Equal(("dev", 1L, Cost), (view.Users.Single().UserId, view.Users.Single().Calls, view.Users.Single().CostMicroUsd));
    }

    [Fact]
    public async Task The_hard_cap_blocks_with_a_message_that_says_what_happens_next_and_it_is_recorded_not_silent()
    {
        var r = Make();
        var ok = await Calls(r, 10);
        Assert.All(ok, x => Assert.True(x.IsCompleted, x.Detail));
        Assert.Equal(Total, (await r.Allowance.GetAsync(Ws)).UsedMicroUsd);
        Assert.Equal(10, r.Provider.Calls);

        var blocked = await r.Client.CompleteAsync(Req());
        Assert.Equal(AiOutcome.AllowanceReached, blocked.Outcome);
        Assert.False(blocked.ProviderCalled);
        Assert.Equal(10, r.Provider.Calls);                                          // no model was called
        Assert.Contains("used up for October 2026", blocked.Detail);
        Assert.Contains("renews on 1 November 2026 (UTC)", blocked.Detail);
        Assert.Contains("credit pack", blocked.Detail);
        Assert.Null(blocked.Text);

        var a = (await r.Log.GetAsync(blocked.ActionId!))!;                          // the refusal is in the audit trail
        Assert.Equal(("Blocked", "NotApplicable", blocked.Detail), (a.Outcome, a.Decision, a.Detail));
        Assert.Equal("HardCap", a.CapReason);                                         // the audit trail says which rule stopped it, though its outcome list is unchanged
        Assert.Equal("HardCap", (await r.Log.ListAsync(new AiActionFilter(Outcome: "Blocked"))).Single().CapReason);
        var refused = (await Ledger(r)).Single(l => l.State == "Refused");           // and in the ledger, with the exact reason
        Assert.Equal(("HardCap", 0L, 0L, blocked.ActionId), (refused.RefusalReason, refused.CostMicroUsd, refused.ReservedMicroUsd, refused.AiActionId));
        Assert.Contains("Refused", await AuditActions(r, "AiUsage"));
        Assert.Contains("HardCapReached", await AuditActions(r, "AiAllowance"));

        var view = await r.Allowance.GetAsync(Ws);
        Assert.Equal(("Hard", 100, 0L), (view.Level, view.PercentUsed, view.RemainingMicroUsd));
        Assert.Equal(blocked.Detail, view.Message);
        Assert.Equal(1L, view.Features.Single(x => x.Feature == "risk-score").Refused);

        var pre = await r.Client.PreflightAsync(Ws, AiFeatures.RiskScore);              // the preflight and the status panel say the same, without reserving
        Assert.False(pre.Allowed);
        Assert.Equal(blocked.Detail, pre.Detail);
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task Administrators_are_told_once_when_the_allowance_is_used_up_however_often_calls_are_refused()
    {
        var r = Make();
        await Calls(r, 10);
        for (var i = 0; i < 4; i++) Assert.Equal(AiOutcome.AllowanceReached, (await r.Client.CompleteAsync(Req())).Outcome);
        var hard = r.Notes.Sent.Where(n => n.Message.Contains("is used up for")).ToList();
        Assert.Equal(["rm", "rte"], hard.Select(n => n.UserId).Order());              // the RTEs and Release Managers of the workspace, once each
        Assert.All(hard, n => Assert.Equal(("AiAllowance", "AiAllowance"), (n.Kind, n.EntityType)));
        Assert.Contains(r.AllowanceLog.Lines, l => l.Contains("HardCap"));              // always in the log too
        Assert.Single(await AuditActions(r, "AiAllowance"), a => a == "HardCapReached");
    }

    [Fact]
    public async Task A_request_too_large_for_what_is_left_is_refused_with_the_amount_left_and_a_smaller_one_still_runs()
    {
        var r = Make();
        await Calls(r, 9);                                                             // 18,000 used, 2,000 (0.2 credits) left
        var big = await r.Client.CompleteAsync(Req(maxOutput: 400));                   // would hold 4,002
        Assert.Equal(AiOutcome.AllowanceReached, big.Outcome);
        Assert.Contains("not enough for this request", big.Detail);
        Assert.Contains("0.2 credits", big.Detail);
        Assert.True((await r.Client.CompleteAsync(Req(maxOutput: 190))).IsCompleted);   // 18,000 + 1,902 fits
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task Chat_stops_at_the_soft_cap_while_drafts_and_summaries_go_on_and_administrators_are_told_once()
    {
        var r = Make(o => o.Allowance.ChatSharePercent = 100);                           // so only the soft cap stops chat here
        await Calls(r, 8);                                                             // 16,000 = the 80% soft cap
        var chat = await r.Client.CompleteAsync(Req(AiFeatures.Chat));
        Assert.Equal(AiOutcome.AllowanceReached, chat.Outcome);
        Assert.False(chat.ProviderCalled);
        Assert.Contains("AI chat is paused", chat.Detail);
        Assert.Contains("soft cap", chat.Detail);
        Assert.Contains("Drafts and summaries still run", chat.Detail);
        Assert.Contains("1 November 2026 (UTC)", chat.Detail);
        Assert.Equal("ChatCap", (await Ledger(r)).Single(l => l.State == "Refused").RefusalReason);
        var atSoft = await r.Allowance.GetAsync(Ws);
        Assert.Equal(("Soft", false, 80), (atSoft.Level, atSoft.ChatAvailable, atSoft.PercentUsed));

        Assert.True((await r.Client.CompleteAsync(Req(AiFeatures.ReleaseNotes))).IsCompleted);   // a draft still runs
        Assert.True((await r.Client.CompleteAsync(Req(AiFeatures.GateSummary))).IsCompleted);    // so does a summary
        Assert.Equal(AiOutcome.AllowanceReached, (await r.Client.CompleteAsync(Req(AiFeatures.Chat))).Outcome);

        var soft = r.Notes.Sent.Where(n => n.Message.Contains("reached 80%")).ToList();
        Assert.Equal(["rm", "rte"], soft.Select(n => n.UserId).Order());                // told once, not on every refusal
        Assert.Equal(("Hard", false), ((await r.Allowance.GetAsync(Ws)).Level, (await r.Allowance.GetAsync(Ws)).ChatAvailable));   // and then the whole allowance is used
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task Chat_is_capped_at_its_share_of_the_allowance_before_anything_else()
    {
        var r = Make();                                                                // chat's share is 50% = 10,000
        var chats = await Calls(r, 5, AiFeatures.Chat);
        Assert.All(chats, x => Assert.True(x.IsCompleted, x.Detail));
        var sixth = await r.Client.CompleteAsync(Req(AiFeatures.Chat));
        Assert.Equal(AiOutcome.AllowanceReached, sixth.Outcome);
        Assert.Contains("chat has used its share", sixth.Detail);
        Assert.True((await r.Client.CompleteAsync(Req(AiFeatures.RiskScore))).IsCompleted);   // other features are not held to chat's share
        var p = Assert.Single(await Periods(r));
        Assert.Equal((5 * Cost, 6 * Cost), (p.ChatUsedMicroUsd, p.UsedMicroUsd));
        var ledger = await Ledger(r);
        Assert.Equal((6, 1), (ledger.Count(l => l.Class == "Chat"), ledger.Count(l => l.Class == "Standard")));   // five settled and the refused sixth, and the one summary
        var view = await r.Allowance.GetAsync(Ws);
        Assert.Equal((10_000L, false, "Soft"), (view.ChatLimitMicroUsd, view.ChatAvailable, view.Level));
        Assert.True(view.Features.Single(f => f.Feature == AiFeatures.Chat).CappedFirst);
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task A_chat_share_of_zero_means_no_chat_at_all()
    {
        var r = Make(o => o.Allowance.ChatSharePercent = 0);
        Assert.Equal(AiOutcome.AllowanceReached, (await r.Client.CompleteAsync(Req(AiFeatures.Chat))).Outcome);
        Assert.True((await r.Client.CompleteAsync(Req(AiFeatures.RiskScore))).IsCompleted);
        Assert.Equal(0, r.Provider.Calls - 1);
    }

    [Fact]
    public async Task The_allowance_resets_on_the_first_of_the_month_and_a_past_month_is_kept_as_it_was()
    {
        var r = Make();
        await Calls(r, 10);
        Assert.Equal(AiOutcome.AllowanceReached, (await r.Client.CompleteAsync(Req())).Outcome);
        r.K.Time.Advance(TimeSpan.FromDays(11).Add(TimeSpan.FromHours(10)).Add(TimeSpan.FromSeconds(-1)));   // 2026-10-31 23:59:59: still October
        Assert.Equal(AiOutcome.AllowanceReached, (await r.Client.CompleteAsync(Req())).Outcome);
        r.Options.CurrentValue.Allowance.IncludedCredits = 5;                          // the policy changes: the new month gets it, October keeps its own
        r.K.Time.Advance(TimeSpan.FromSeconds(1));                                     // 2026-11-01 00:00:00
        Assert.True((await r.Client.CompleteAsync(Req())).IsCompleted);

        var periods = await Periods(r);
        Assert.Equal(2, periods.Count);
        Assert.Equal((new DateOnly(2026, 10, 1), Total, Total), (periods[0].PeriodStart, periods[0].IncludedMicroUsd, periods[0].UsedMicroUsd));
        Assert.Equal((new DateOnly(2026, 11, 1), 50_000L, Cost), (periods[1].PeriodStart, periods[1].IncludedMicroUsd, periods[1].UsedMicroUsd));
        var view = await r.Allowance.GetAsync(Ws);
        Assert.Equal(("2026-11-01", "2026-12-01", "Ok", 50_000L - Cost), (view.PeriodStart, view.PeriodEnd, view.Level, view.RemainingMicroUsd));
        Assert.Equal(1, (await Ledger(r)).Count(l => l.PeriodId == periods[1].Id && l.State == "Settled"));
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task A_new_month_can_be_announced_again_when_the_last_one_ran_out()
    {
        var r = Make();
        await Calls(r, 10); await r.Client.CompleteAsync(Req());
        var hard1 = r.Notes.Sent.Count(n => n.Message.Contains("is used up for"));
        r.K.Time.Advance(TimeSpan.FromDays(15));
        await Calls(r, 10); await r.Client.CompleteAsync(Req());
        Assert.Equal(hard1 * 2, r.Notes.Sent.Count(n => n.Message.Contains("is used up for")));
    }

    // ---- rate limits -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_person_is_limited_per_minute_and_per_hour_and_nobody_else_is_affected()
    {
        var r = Make(o => { o.Allowance.IncludedCredits = 100; o.Allowance.UserPerMinute = 3; o.Allowance.UserPerHour = 5; });
        Assert.All(await Calls(r, 3), x => Assert.True(x.IsCompleted));
        var fourth = await r.Client.CompleteAsync(Req(user: "dev"));
        Assert.Equal(AiOutcome.RateLimited, fourth.Outcome);
        Assert.False(fourth.ProviderCalled);
        Assert.Contains("3 per minute and 5 per hour", fourth.Detail);
        Assert.Contains("Try again in a minute", fourth.Detail);
        Assert.Equal(3, r.Provider.Calls);
        Assert.True((await r.Client.CompleteAsync(Req(user: "gov1"))).IsCompleted);     // another person is not limited by dev
        Assert.True((await r.Client.CompleteAsync(Req(user: null))).IsCompleted);       // a scheduled run (the system) is capped, never rate-limited

        r.K.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await r.Client.CompleteAsync(Req(user: "dev"))).IsCompleted);      // the minute window moved on: dev's 4th call of the hour
        Assert.True((await r.Client.CompleteAsync(Req(user: "dev"))).IsCompleted);      // 5th
        var sixth = await r.Client.CompleteAsync(Req(user: "dev"));                      // the hour window is full
        Assert.Equal(AiOutcome.RateLimited, sixth.Outcome);
        Assert.Contains("Try again in a little while", sixth.Detail);
        r.K.Time.Advance(TimeSpan.FromHours(1));
        Assert.True((await r.Client.CompleteAsync(Req(user: "dev"))).IsCompleted);

        var refused = (await Ledger(r)).Where(l => l.State == "Refused").ToList();
        Assert.All(refused, l => Assert.Equal("RateLimit", l.RefusalReason));
        Assert.Equal(2, refused.Count);
        Assert.Equal((AiOutcome.Blocked.ToString(), "RateLimit"), ((await r.Log.GetAsync(fourth.ActionId!))!.Outcome, (await r.Log.GetAsync(fourth.ActionId!))!.CapReason));
        await AssertBooksBalance(r);
    }

    // ---- credit packs ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_credit_pack_lifts_the_hard_cap_at_once_and_does_not_roll_into_the_next_month()
    {
        var r = Make();
        await Calls(r, 10);
        Assert.Equal(AiOutcome.AllowanceReached, (await r.Client.CompleteAsync(Req())).Outcome);

        var granted = await r.Allowance.GrantPackAsync(Ws, AiCredits.FromCredits(3), "Manual", null, "Launch pack", new Actor("rte"));
        Assert.True(granted.IsOk, string.Join("; ", granted.Failures.Select(f => f.Message)));
        Assert.Equal((30_000L, 3.0, "Manual"), (granted.Value!.MicroUsd, granted.Value.Credits, granted.Value.Source));
        var view = await r.Allowance.GetAsync(Ws);
        Assert.Equal((Total + 30_000, 30_000L, "Ok"), (view.TotalMicroUsd, view.PackMicroUsd, view.Level));
        Assert.Equal("Launch pack", Assert.Single(view.Packs).Note);
        Assert.Equal("Rae T.", view.Packs[0].GrantedByName);
        Assert.Equal(["Granted"], await AuditActions(r, "AiCreditPack"));

        Assert.All(await Calls(r, 15), x => Assert.True(x.IsCompleted, x.Detail));      // 30,000 more: the pack is spent
        Assert.Equal(AiOutcome.AllowanceReached, (await r.Client.CompleteAsync(Req())).Outcome);

        r.K.Time.Advance(TimeSpan.FromDays(15));                                       // November: the pack was for October
        var nov = await r.Allowance.GetAsync(Ws);
        Assert.Equal((0L, Total), (nov.PackMicroUsd, nov.TotalMicroUsd));
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task A_pack_after_running_out_lets_administrators_be_told_again_if_it_runs_out_again()
    {
        var r = Make();
        await Calls(r, 10); await r.Client.CompleteAsync(Req());
        var first = r.Notes.Sent.Count(n => n.Message.Contains("is used up for"));
        Assert.True(first > 0);
        await r.Allowance.GrantPackAsync(Ws, 20_000, "Manual", null, null, new Actor("rm"));
        await Calls(r, 10); await r.Client.CompleteAsync(Req());
        Assert.Equal(first * 2, r.Notes.Sent.Count(n => n.Message.Contains("is used up for")));
    }

    [Fact]
    public async Task A_billing_grant_is_idempotent_by_its_reference_and_bad_grants_are_refused_with_a_guard()
    {
        var r = Make();
        var a = await r.Allowance.GrantPackAsync(Ws, 50_000, "Billing", "inv-1001", null, null);
        var b = await r.Allowance.GrantPackAsync(Ws, 50_000, "Billing", "inv-1001", null, null);
        Assert.True(a.IsOk && b.IsOk);
        Assert.Equal(a.Value!.Id, b.Value!.Id);                                         // the same purchase is the same grant
        Assert.Equal(50_000L, (await r.Allowance.GetAsync(Ws)).PackMicroUsd);          // added once
        Assert.NotEqual(a.Value.Id, (await r.Allowance.GrantPackAsync(Ws, 10_000, "Billing", "inv-1002", null, null)).Value!.Id);

        foreach (var (micro, source, reference, actor) in new (long, string, string?, Actor?)[] { (0, "Manual", null, new("rte")), (-5, "Manual", null, new("rte")), (100, "Gift", null, new("rte")),
                     (100, "Billing", null, null), (100, "Manual", null, null) })
        {
            var bad = await r.Allowance.GrantPackAsync(Ws, micro, source, reference, null, actor);
            Assert.Equal(ResultKind.GuardFailed, bad.Kind);
            Assert.Equal(AiGuards.AiCreditPack, bad.Failures[0].Guard);
        }
        Assert.Equal(60_000L, (await r.Allowance.GetAsync(Ws)).PackMicroUsd);
    }

    // ---- no overspend, no leak ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_calls_do_not_overspend_the_allowance()
    {
        var provider = new FixedUsageProvider(delayMs: 30);                             // slow enough that calls overlap and hold reservations together
        var r = Make(provider: provider);
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => r.Client.CompleteAsync(Req(user: null)))));
        Assert.Equal(10, results.Count(x => x.IsCompleted));                             // exactly what 20,000 buys at 2,000 a call
        Assert.Equal(30, results.Count(x => x.Outcome == AiOutcome.AllowanceReached));
        Assert.Equal(10, provider.Calls);                                                // refused calls never reached the model
        var p = Assert.Single(await Periods(r));
        Assert.True(p.UsedMicroUsd <= Total, $"spent {p.UsedMicroUsd} of {Total}");
        Assert.Equal((Total, 0L), (p.UsedMicroUsd, p.ReservedMicroUsd));
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task Concurrent_chat_never_passes_its_share_and_other_features_use_the_rest()
    {
        var r = Make(provider: new FixedUsageProvider(delayMs: 20));
        var calls = Enumerable.Range(0, 30).Select(i => Task.Run(() => r.Client.CompleteAsync(Req(i % 2 == 0 ? AiFeatures.Chat : AiFeatures.RiskScore, user: null)))).ToArray();
        var results = await Task.WhenAll(calls);
        var p = Assert.Single(await Periods(r));
        Assert.True(p.ChatUsedMicroUsd <= 10_000, $"chat spent {p.ChatUsedMicroUsd}");
        Assert.True(p.UsedMicroUsd <= Total);
        Assert.InRange(results.Count(x => x.IsCompleted), 1, 10);                       // 20,000 buys at most ten calls whatever the mix
        Assert.Equal(results.Count(x => x.IsCompleted) * Cost, p.UsedMicroUsd);
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task A_failed_call_gives_its_reservation_back_and_costs_nothing()
    {
        var provider = new FixedUsageProvider { Throw = () => new AiProviderException("The model is overloaded", transient: false) };
        var r = Make(provider: provider);
        var res = await r.Client.CompleteAsync(Req());
        Assert.Equal(AiOutcome.ProviderError, res.Outcome);
        var l = Assert.Single(await Ledger(r));
        Assert.Equal(("Released", 0L, res.ActionId), (l.State, l.CostMicroUsd, l.AiActionId));
        var p = Assert.Single(await Periods(r));
        Assert.Equal((0L, 0L), (p.UsedMicroUsd, p.ReservedMicroUsd));
        Assert.Equal(Estimate, l.ReservedMicroUsd);
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task A_call_the_caller_cancels_gives_its_reservation_back()
    {
        var provider = new FixedUsageProvider(delayMs: 30_000);
        var r = Make(provider: provider);
        using var cts = new CancellationTokenSource();
        var call = r.Client.CompleteAsync(Req(), cts.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));              // the reservation is held and the model is being called
        Assert.Equal(Estimate, (await Periods(r)).Single().ReservedMicroUsd);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        var l = Assert.Single(await Ledger(r));
        Assert.Equal("Released", l.State);
        Assert.Equal(0L, Assert.Single(await Periods(r)).ReservedMicroUsd);
        Assert.Contains("Released", await AuditActions(r, "AiUsage"));
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task When_the_action_cannot_be_recorded_the_output_is_withheld_and_the_reservation_goes_back()
    {
        var down = new RecordingActionLog { Fail = true };
        var r = Make(logOverride: down);
        var res = await r.Client.CompleteAsync(Req());
        Assert.Equal(AiOutcome.AuditFailed, res.Outcome);
        Assert.Null(res.Text);
        var l = Assert.Single(await Ledger(r));
        Assert.Equal("Released", l.State);
        Assert.Equal(0L, Assert.Single(await Periods(r)).ReservedMicroUsd);
        await AssertBooksBalance(r);
    }

    private sealed class BrokenAllowance : IAiAllowance
    {
        public Task<AiReservation> ReserveAsync(string workspaceId, string feature, string? userId, string? model, long estimateMicroUsd, bool batch, CancellationToken ct = default) => throw new InvalidOperationException("the books are down");
        public Task ReleaseAsync(string workspaceId, string entryId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AiCapCheck> CheckAsync(string workspaceId, string feature, CancellationToken ct = default) => Task.FromResult(new AiCapCheck(true, null, null));
        public Task<AiAllowanceView> GetAsync(string workspaceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiLedgerEntry>> LedgerAsync(string workspaceId, int limit = 100, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceResult<AiCreditPackView>> GrantPackAsync(string workspaceId, long microUsd, string source, string? reference, string? note, Actor? actor, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_call_whose_allowance_cannot_be_reserved_is_not_made_at_all()
    {
        var r = Make();
        var log = new CapturingLogger<GovernedAiClient>();
        var client = new GovernedAiClient(r.Provider, new MemoryKeys(), r.Options, new DbAiWorkspaceSettings(r.K.Db), new StubGate(Ws), new NoopAiUsageSink(), r.Log, r.K.Time, log, new BrokenAllowance());
        var res = await client.CompleteAsync(Req());
        Assert.Equal(AiOutcome.AuditFailed, res.Outcome);
        Assert.Equal(0, r.Provider.Calls);                                              // a call that is not on the books is not made
        Assert.Contains("allowance could not be checked", res.Detail);
        Assert.Contains(log.Lines, l => l.Contains("could not be reserved") && l.Contains("the books are down"));   // and it is logged, not swallowed
    }

    [Fact]
    public async Task A_reservation_left_by_a_call_that_died_is_released_and_a_late_answer_is_still_counted()
    {
        var r = Make();
        var leaked = await r.Allowance.ReserveAsync(Ws, AiFeatures.RiskScore, "dev", "small-model", Estimate, false);   // reserved, then the process died
        Assert.True(leaked.Granted);
        Assert.Equal(Estimate, (await Periods(r)).Single().ReservedMicroUsd);
        r.K.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AiOutcome.Completed, (await r.Client.CompleteAsync(Req())).Outcome);   // too soon to be stale: still held (and it fits)
        Assert.Equal(Estimate, (await Periods(r)).Single().ReservedMicroUsd);

        r.K.Time.Advance(TimeSpan.FromHours(2));                                        // longer than any call or batch can take
        Assert.Equal(AiOutcome.Completed, (await r.Client.CompleteAsync(Req())).Outcome);
        var stale = (await Ledger(r)).Single(l => l.Id == leaked.EntryId);
        Assert.Equal("Released", stale.State);
        Assert.Equal(0L, (await Periods(r)).Single().ReservedMicroUsd);
        Assert.Contains(r.AllowanceLog.Lines, l => l.Contains("stale") || l.Contains("Stale"));
        await AssertBooksBalance(r);

        // the call that was thought dead answers after all: its record still settles, as a new entry
        var late = new AiResult(AiOutcome.Completed, AiFeatures.RiskScore, Ws, r.K.Time.GetUtcNow().UtcDateTime, "late answer", Model: "small-model", Usage: new AiUsage(AiFeatures.RiskScore, "small-model", 100, 180, 0, 0, false),
            ProviderCalled: true, Inputs: [], InputHash: new string('d', 64), Provider: "fixed", Metering: new AiMetering(leaked.EntryId, leaked.PeriodId, Estimate, Cost, null));
        await r.Log.RecordAsync(Req(), late);
        var p = (await Periods(r)).Single();
        Assert.Equal(3 * Cost, p.UsedMicroUsd);
        Assert.Equal(1, (await Ledger(r)).Count(l => l.State == "Settled" && l.ReservedMicroUsd == 0));
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task A_batch_is_priced_at_half_and_each_request_in_it_is_capped_on_its_own()
    {
        var r = Make();
        var batch = await r.Client.CompleteBatchAsync([Req(), Req(), Req()]);
        Assert.All(batch, x => Assert.True(x.IsCompleted, x.Detail));
        Assert.All(batch, x => Assert.Equal(Cost / 2, x.Metering!.CostMicroUsd));
        Assert.All(await Ledger(r), l => Assert.True(l.Batch));
        Assert.Equal(3 * Cost / 2, (await Periods(r)).Single().UsedMicroUsd);

        var many = await r.Client.CompleteBatchAsync(Enumerable.Range(0, 30).Select(_ => Req()).ToList());
        var done = many.Count(x => x.IsCompleted);
        Assert.InRange(done, 1, 29);
        Assert.Equal(30 - done, many.Count(x => x.Outcome == AiOutcome.AllowanceReached));
        Assert.True((await Periods(r)).Single().UsedMicroUsd <= Total);
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task A_refusal_by_a_module_or_kill_switch_never_touches_the_allowance()
    {
        var r = Make(o => o.KillSwitch = true);
        Assert.Equal(AiOutcome.KillSwitch, (await r.Client.CompleteAsync(Req())).Outcome);
        Assert.Empty(await Ledger(r));
        Assert.Empty(await Periods(r));
    }

    // ---- the cost model against a provider's report ------------------------------------------------------------------------------

    private sealed record Fixture(List<Row> Rows);
    private sealed record Row(string Model, bool Batch, long Calls, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, string CostUsd);

    private static List<AiProviderUsageRow> Report()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ai", "Fixtures", "provider-usage-report.json"));
        var f = JsonSerializer.Deserialize<Fixture>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return [.. f.Rows.Select(x => new AiProviderUsageRow(x.Model, x.Batch, x.InputTokens, x.OutputTokens, x.CacheReadTokens, x.CacheWriteTokens, x.Calls, decimal.Parse(x.CostUsd, System.Globalization.CultureInfo.InvariantCulture)))];
    }

    private sealed class ReplayProvider(Queue<AiProviderResponse> answers) : IAiProvider
    {
        public string Name => "replay";
        public bool RequiresKey => false;
        public Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken ct) => Task.FromResult(answers.Dequeue() with { ServedModel = request.Model });
        public Task<IReadOnlyList<AiProviderResponse?>> CompleteBatchAsync(IReadOnlyList<AiProviderRequest> requests, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AiProviderResponse?>>([.. requests.Select(r => (AiProviderResponse?)(answers.Dequeue() with { ServedModel = r.Model }))]);
    }

    [Fact]
    public async Task The_ledger_reconciles_against_a_provider_usage_report_to_the_micro_dollar()
    {
        var report = Report();
        Assert.True(report.Count >= 4);
        // Replay each bucket as its calls through the real client, with the models named as the provider names them and real (default) prices.
        var answers = new Queue<AiProviderResponse>();
        var plan = new List<(AiProviderUsageRow Row, int Index)>();
        foreach (var row in report)
            for (var i = 0; i < row.Calls; i++)
            {
                answers.Enqueue(new AiProviderResponse("ok", row.InputTokens / row.Calls, row.OutputTokens / row.Calls, row.CacheReadTokens / row.Calls, row.CacheWriteTokens / row.Calls));
                plan.Add((row, i));
            }
        Assert.All(report, row => { Assert.Equal(0, row.InputTokens % row.Calls); Assert.Equal(0, row.CacheReadTokens % row.Calls); });   // the fixture divides evenly

        var r = Make(o => { o.Pricing.Models.Clear(); o.Allowance.IncludedCredits = 100_000; o.Models.Small = "claude-haiku-5-5"; o.Models.Drafting = "claude-sonnet-5-5"; });
        var client = new GovernedAiClient(new ReplayProvider(answers), new MemoryKeys(), r.Options, new DbAiWorkspaceSettings(r.K.Db), new StubGate(Ws), new NoopAiUsageSink(), r.Log, r.K.Time,
            NullLogger<GovernedAiClient>.Instance, r.Allowance);
        // the plan order is the answers' order: route each call to the model its bucket names
        foreach (var (row, _) in plan)
        {
            var feature = row.Model.Contains("haiku") ? AiFeatures.RiskScore : AiFeatures.ReleaseNotes;
            if (!row.Model.Contains("haiku")) r.Options.CurrentValue.Models.Drafting = row.Model;
            var res = row.Batch ? (await client.CompleteBatchAsync([Req(feature, user: null)]))[0] : await client.CompleteAsync(Req(feature, user: null));
            Assert.True(res.IsCompleted, res.Detail);
            Assert.Equal(row.Model, res.Model);
        }

        var rec = AiUsageReconciler.Reconcile(await r.Allowance.LedgerAsync(Ws, 500), report);
        Assert.All(rec.Lines, l => Assert.True(l.Ok, $"{l.Model} batch={l.Batch}: ledger {l.LedgerCostMicroUsd} vs report {l.ReportCostMicroUsd} (tokens match: {l.TokensMatch})"));
        Assert.True(rec.Ok);
        Assert.Equal(report.Count, rec.Lines.Count);
        Assert.Equal(34_000, rec.Lines.Single(l => l.Model == "claude-haiku-5-5" && !l.Batch).ReportCostMicroUsd);
        Assert.Equal(0, rec.Lines.Sum(l => l.CostDifferenceMicroUsd));                  // here every call prices to a whole micro-dollar, so the match is exact
        await AssertBooksBalance(r);
    }

    [Fact]
    public async Task The_reconciliation_notices_a_wrong_price_a_missing_call_and_a_token_mismatch()
    {
        var r = Make();
        await Calls(r, 2);
        var entries = await r.Allowance.LedgerAsync(Ws, 50);
        var exact = new AiProviderUsageRow("small-model", false, 200, 360, 0, 0, 2, 0.004000m);
        Assert.True(AiUsageReconciler.Reconcile(entries, [exact]).Ok);
        Assert.False(AiUsageReconciler.Reconcile(entries, [exact with { CostUsd = 0.003000m }]).Ok);        // the provider bills less: we over-count by far more than rounding
        Assert.False(AiUsageReconciler.Reconcile(entries, [exact with { CostUsd = 0.005000m }]).Ok);        // the provider bills more: we under-count, which must never happen
        Assert.False(AiUsageReconciler.Reconcile(entries, [exact with { Calls = 3 }]).Ok);                 // a call the ledger does not have
        Assert.False(AiUsageReconciler.Reconcile(entries, [exact with { OutputTokens = 359 }]).Ok);        // tokens differ
        Assert.False(AiUsageReconciler.Reconcile(entries, []).Ok);                                          // a model the provider never billed
    }
}
