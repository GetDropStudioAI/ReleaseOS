using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Ai;

public sealed record AiInventoryFeature(string Key, string Name, string Tier, string? Model, bool Enabled, string? BlockedBecause, long Calls, long Completed, long Accepted, long Edited, long Rejected, long Pending,
    DateTime? LastUsedAt, IReadOnlyList<string> PromptTemplateVersions, IReadOnlyList<string> InputLabels);
public sealed record AiInventoryModel(string Provider, string Model, IReadOnlyList<string> ServedVersions, long Calls, long InputTokens, long OutputTokens, DateTime? LastUsedAt, IReadOnlyList<string> Features);
public sealed record AiInventoryDataRules(IReadOnlyList<string> Sent, IReadOnlyList<string> NeverSent, IReadOnlyList<string> Redacted, int MaxInputChars, bool HashesOnly);
public sealed record AiInventoryDecisions(long Total, long Pending, long Accepted, long Edited, long Rejected, long Refused);
public sealed record AiInventory(AiStatus Controls, IReadOnlyList<AiInventoryFeature> Features, IReadOnlyList<AiInventoryModel> Models, AiInventoryDataRules Data, AiInventoryDecisions Decisions, int ActionsConsidered);

/// <summary>
/// REOS-184 (D58): the AI inventory for administrators and Governance Officers: which features exist and whether each can run now, which models were used, what data goes to a model, and the state of
/// every kill switch. Read-only. The usage columns come from <c>AiActions</c> of the current workspace (the newest <see cref="Window"/> rows), so the screen tells what actually happened, not only what is configured.
/// </summary>
public sealed class AiInventoryService(IDbContextFactory<ReleaseDbContext> dbf, AiStatusService status, IAiClient client, IOptionsMonitor<AiOptions> options)
{
    public const int Window = 2000;
    private static readonly JsonSerializerOptions Camel = new(JsonSerializerDefaults.Web);

    public async Task<AiInventory> GetAsync(string workspaceId, CancellationToken ct = default)
    {
        var controls = await status.GetAsync(workspaceId, ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var rows = await db.Set<AiActions>().AsNoTracking().OrderByDescending(a => a.RequestedAt).ThenByDescending(a => a.Id).Take(Window).ToListAsync(ct);

        var features = new List<AiInventoryFeature>();
        foreach (var f in AiFeatures.All)
        {
            var pre = await client.PreflightAsync(workspaceId, f.Key, ct);
            var mine = rows.Where(r => r.Feature == f.Key).ToList();
            var labels = mine.SelectMany(r => Labels(r.InputRefsJson)).Distinct().Order().Take(30).ToList();
            features.Add(new AiInventoryFeature(f.Key, f.Name, f.Tier.ToString(), pre.Model, pre.Allowed, pre.Allowed ? null : pre.Detail, mine.Count, mine.Count(r => r.Outcome == nameof(AiOutcome.Completed)),
                mine.Count(r => r.Decision == AiDecisions.Accepted), mine.Count(r => r.Decision == AiDecisions.Edited), mine.Count(r => r.Decision == AiDecisions.Rejected), mine.Count(r => r.Decision == AiDecisions.Pending),
                mine.Count == 0 ? null : mine.Max(r => r.RequestedAt), [.. mine.Select(r => r.PromptTemplateVersion).Distinct().Order()], labels));
        }

        var models = rows.Where(r => r.Model is not null && r.Outcome is nameof(AiOutcome.Completed) or nameof(AiOutcome.TimedOut) or nameof(AiOutcome.ProviderError)).GroupBy(r => (r.Provider, Model: r.Model!))
            .Select(g => new AiInventoryModel(g.Key.Provider, g.Key.Model, [.. g.Select(r => r.ServedModel).Where(m => m is not null && m != g.Key.Model).Select(m => m!).Distinct().Order()], g.Count(),
                g.Sum(r => (long)r.InputTokens), g.Sum(r => (long)r.OutputTokens), g.Max(r => r.RequestedAt), [.. g.Select(r => r.Feature).Distinct().Order()])).OrderByDescending(m => m.Calls).ToList();

        var o = options.CurrentValue;
        var data = new AiInventoryDataRules(
            [$"{AiDataClass.ReleaseData}: release, gate and run facts the person can already see", $"{AiDataClass.FreeText}: text a person wrote in the tool"],
            [$"{AiDataClass.TicketSummary}: ticket summaries from Jira or ServiceNow (D12)", $"{AiDataClass.CustomerData}: customer data"],
            ["API keys and bearer tokens", "GitHub, Slack and AWS tokens", "JSON web tokens", "passwords and secrets in assignments", "e-mail addresses"], o.MaxInputChars, HashesOnly: true);
        var decisions = new AiInventoryDecisions(rows.Count, rows.Count(r => r.Decision == AiDecisions.Pending), rows.Count(r => r.Decision == AiDecisions.Accepted), rows.Count(r => r.Decision == AiDecisions.Edited),
            rows.Count(r => r.Decision == AiDecisions.Rejected), rows.Count(r => r.Decision == AiDecisions.NotApplicable));
        return new AiInventory(controls, features, models, data, decisions, rows.Count);
    }

    private static IEnumerable<string> Labels(string json)
    {
        try { return (JsonSerializer.Deserialize<List<AiInputRef>>(json, Camel) ?? []).Select(i => i.Label); }
        catch (JsonException) { return []; }
    }
}
