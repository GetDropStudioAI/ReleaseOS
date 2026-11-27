using ReleaseMgmt.Domain.Ai;

namespace ReleaseMgmt.Infrastructure.Ai;

/// <summary>One bucket of a provider's usage report (tokens and what the provider billed, in US dollars) for a model, split by whether it was the batch path.</summary>
public sealed record AiProviderUsageRow(string Model, bool Batch, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, long Calls, decimal CostUsd);

/// <summary>One model of the reconciliation: what the ledger says against what the provider says.</summary>
public sealed record AiReconciliationLine(string Model, bool Batch, long LedgerCalls, long ReportCalls, long LedgerCostMicroUsd, long ReportCostMicroUsd, long CostDifferenceMicroUsd, bool TokensMatch, bool Ok);

public sealed record AiReconciliation(IReadOnlyList<AiReconciliationLine> Lines, bool Ok);

/// <summary>
/// REOS-185 (D57): checks the cost model against a provider's usage report. Tokens must match exactly. The ledger prices each call and rounds it UP to a whole micro-dollar, the provider prices a bucket as a whole, so
/// the ledger may exceed the report by less than one micro-dollar per call and may never be below it: the allowance can over-count by a hair but never under-count. Anything else is a pricing error.
/// </summary>
public static class AiUsageReconciler
{
    public static AiReconciliation Reconcile(IReadOnlyList<AiLedgerEntry> ledger, IReadOnlyList<AiProviderUsageRow> report)
    {
        var settled = ledger.Where(e => e.State == "Settled" && e.Model is not null).ToList();
        var keys = settled.Select(e => (e.Model!, e.Batch)).Concat(report.Select(r => (r.Model, r.Batch))).Distinct().OrderBy(k => k.Item1).ThenBy(k => k.Item2);
        var lines = new List<AiReconciliationLine>();
        foreach (var (model, batch) in keys)
        {
            var mine = settled.Where(e => e.Model == model && e.Batch == batch).ToList();
            var theirs = report.Where(r => r.Model == model && r.Batch == batch).ToList();
            var reportMicro = (long)Math.Round(theirs.Sum(r => r.CostUsd) * 1_000_000m, MidpointRounding.AwayFromZero);
            var ledgerMicro = mine.Sum(e => e.CostMicroUsd);
            var tokens = mine.Sum(e => e.InputTokens) == theirs.Sum(r => r.InputTokens) && mine.Sum(e => e.OutputTokens) == theirs.Sum(r => r.OutputTokens)
                      && mine.Sum(e => e.CacheReadTokens) == theirs.Sum(r => r.CacheReadTokens) && mine.Sum(e => e.CacheWriteTokens) == theirs.Sum(r => r.CacheWriteTokens);
            var diff = ledgerMicro - reportMicro;
            lines.Add(new AiReconciliationLine(model, batch, mine.Count, theirs.Sum(r => r.Calls), ledgerMicro, reportMicro, diff, tokens, tokens && diff >= 0 && diff < Math.Max(1, mine.Count) && mine.Count == theirs.Sum(r => r.Calls)));
        }
        return new AiReconciliation(lines, lines.All(l => l.Ok));
    }
}
