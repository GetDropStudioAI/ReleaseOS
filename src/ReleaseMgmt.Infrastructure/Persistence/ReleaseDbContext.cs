using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;

namespace ReleaseMgmt.Infrastructure.Persistence;

/// <summary>The application model: the tables mapped from db/schema.sql (see tools/gen_entities.py).
/// Relationships are intentionally not modelled as navigations; the database enforces them (schema.sql is the contract).
/// REOS-105 (D45): the context belongs to the workspace that was current when it was created (<see cref="WorkspaceScope"/>). Its query filters show
/// only that workspace's rows, root rows and every row reached through a train, template, team or export job; new root rows are written into it;
/// and a write that points at a row of another workspace is refused before it reaches the database (<see cref="WorkspaceBoundaryException"/>).
/// A context created outside any workspace sees everything, as before workspaces.</summary>
public class ReleaseDbContext(DbContextOptions options) : AppDbContext(options)
{
    /// <summary>The workspace this context is scoped to; null = host-wide (start-up, backups, user accounts, the outer loop of a background service).</summary>
    public string? WorkspaceId { get; } = WorkspaceScope.Current;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ModelMap.Apply(modelBuilder);
        ApplyWorkspaceFilters(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }

    // Filters reference this context's WorkspaceId, which EF turns into a parameter per query. Each child row is shown when its parent is visible:
    // the parent's own filter is written out in full, so the result does not depend on whether EF nests filters.
    private void ApplyWorkspaceFilters(ModelBuilder b)
    {
        b.Entity<WorkspaceMembers>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<Teams>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<Holidays>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<TrainTemplates>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<CommTemplateLibrary>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<ReleaseTrains>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<FreezeWindows>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<WebhookDestinations>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<Notifications>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<ImportJobs>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<ExportJobs>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<IcsTokens>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<ConnectorState>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<ReleaseTypes>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);   // REOS-107/109/110
        b.Entity<Environments>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<Clients>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);   // REOS-118 (RT3)
        b.Entity<CatalogServices>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<IngestKeys>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<Deployments>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<MetricSamples>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);   // REOS-111 to 114
        b.Entity<MetricRollups>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<ThresholdBreaches>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<DeploymentControlActions>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<ApprovalPolicies>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        b.Entity<Approvals>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId);
        // A chain is the workspace's id; the host-wide rows' chain ('*') is read from the default workspace only, like the host-wide audit rows themselves (Q-105c).
        b.Entity<AuditChainHeads>().HasQueryFilter(e => WorkspaceId == null || e.ChainId == WorkspaceId || (e.ChainId == "*" && WorkspaceId == WorkspaceIds.Default));
        // Host-wide alerts (backups) concern every workspace; host-wide audit rows (user accounts) are read from the default workspace only (Q-105c).
        b.Entity<SyncAlerts>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId || e.WorkspaceId == null);
        b.Entity<AuditEvents>().HasQueryFilter(e => WorkspaceId == null || e.WorkspaceId == WorkspaceId || (e.WorkspaceId == null && WorkspaceId == WorkspaceIds.Default));

        b.Entity<TeamMembers>().HasQueryFilter(e => WorkspaceId == null || Set<Teams>().Any(p => p.Id == e.TeamId && p.WorkspaceId == WorkspaceId));
        b.Entity<TemplateGates>().HasQueryFilter(e => WorkspaceId == null || Set<TrainTemplates>().Any(p => p.Id == e.TemplateId && p.WorkspaceId == WorkspaceId));
        b.Entity<TemplateSteps>().HasQueryFilter(e => WorkspaceId == null || Set<TrainTemplates>().Any(p => p.Id == e.TemplateId && p.WorkspaceId == WorkspaceId));
        b.Entity<TemplateCommSchedule>().HasQueryFilter(e => WorkspaceId == null || Set<TrainTemplates>().Any(p => p.Id == e.TemplateId && p.WorkspaceId == WorkspaceId));
        b.Entity<MetricSnapshots>().HasQueryFilter(e => WorkspaceId == null || Set<ExportJobs>().Any(p => p.Id == e.ExportJobId && p.WorkspaceId == WorkspaceId));

        b.Entity<ChangeRecords>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<AffectedCIs>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<DeploymentWindows>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<BundledProducts>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<StageGates>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<Attachments>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<RunbookSteps>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<RunbookRuns>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<Blockers>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<KnownIssues>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<GoNoGoDecisions>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<PostImplementationReviews>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<FreezeOverrides>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<Baselines>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<ExternalLinks>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<CommTemplates>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<CommSchedule>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<ParsePreviews>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<TrainMilestones>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<TrainEnvironments>().HasQueryFilter(e => WorkspaceId == null || Set<ReleaseTrains>().Any(t => t.Id == e.ReleaseTrainId && t.WorkspaceId == WorkspaceId));
        b.Entity<DeploymentEvents>().HasQueryFilter(e => WorkspaceId == null || Set<Deployments>().Any(d => d.Id == e.DeploymentId && d.WorkspaceId == WorkspaceId));
        b.Entity<ApprovalPolicyRequirements>().HasQueryFilter(e => WorkspaceId == null || Set<ApprovalPolicies>().Any(p => p.Id == e.PolicyId && p.WorkspaceId == WorkspaceId));
        b.Entity<ApprovalDecisions>().HasQueryFilter(e => WorkspaceId == null || Set<Approvals>().Any(a => a.Id == e.ApprovalId && a.WorkspaceId == WorkspaceId));

        b.Entity<GateTransitions>().HasQueryFilter(e => WorkspaceId == null || Set<StageGates>().Any(g => g.Id == e.StageGateId && Set<ReleaseTrains>().Any(t => t.Id == g.ReleaseTrainId && t.WorkspaceId == WorkspaceId)));
        b.Entity<GateWaivers>().HasQueryFilter(e => WorkspaceId == null || Set<StageGates>().Any(g => g.Id == e.StageGateId && Set<ReleaseTrains>().Any(t => t.Id == g.ReleaseTrainId && t.WorkspaceId == WorkspaceId)));
        b.Entity<ChecklistTasks>().HasQueryFilter(e => WorkspaceId == null || Set<StageGates>().Any(g => g.Id == e.StageGateId && Set<ReleaseTrains>().Any(t => t.Id == g.ReleaseTrainId && t.WorkspaceId == WorkspaceId)));
        b.Entity<StepDependencies>().HasQueryFilter(e => WorkspaceId == null || Set<RunbookSteps>().Any(s => s.Id == e.StepId && Set<ReleaseTrains>().Any(t => t.Id == s.ReleaseTrainId && t.WorkspaceId == WorkspaceId)));
        b.Entity<StepExecutions>().HasQueryFilter(e => WorkspaceId == null || Set<RunbookRuns>().Any(r => r.Id == e.RunId && Set<ReleaseTrains>().Any(t => t.Id == r.ReleaseTrainId && t.WorkspaceId == WorkspaceId)));
        b.Entity<GoNoGoConditions>().HasQueryFilter(e => WorkspaceId == null || Set<GoNoGoDecisions>().Any(d => d.Id == e.DecisionId && Set<ReleaseTrains>().Any(t => t.Id == d.ReleaseTrainId && t.WorkspaceId == WorkspaceId)));
        b.Entity<PirActions>().HasQueryFilter(e => WorkspaceId == null || Set<PostImplementationReviews>().Any(p => p.Id == e.PirId && Set<ReleaseTrains>().Any(t => t.Id == p.ReleaseTrainId && t.WorkspaceId == WorkspaceId)));
        b.Entity<CommDispatches>().HasQueryFilter(e => WorkspaceId == null || Set<CommTemplates>().Any(c => c.Id == e.CommTemplateId && Set<ReleaseTrains>().Any(t => t.Id == c.ReleaseTrainId && t.WorkspaceId == WorkspaceId)));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceWorkspaceAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await EnforceWorkspaceAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Columns that point at a row of a workspace-scoped table, and the table. A new or changed value must name a row this context can see.
    private static readonly Dictionary<string, Type> References = new()
    {
        ["ReleaseTrainId"] = typeof(ReleaseTrains), ["ClonedFromTrainId"] = typeof(ReleaseTrains),   // not a tab's ActiveTrainId: a person's own drafts, read back by them only
        ["StageGateId"] = typeof(StageGates), ["CapturedAtGateId"] = typeof(StageGates), ["BundledProductId"] = typeof(BundledProducts),
        ["OwnerTeamId"] = typeof(Teams), ["TeamId"] = typeof(Teams), ["TemplateId"] = typeof(TrainTemplates), ["LibraryTemplateId"] = typeof(CommTemplateLibrary),
        ["RunId"] = typeof(RunbookRuns), ["StepId"] = typeof(RunbookSteps), ["DependsOnStepId"] = typeof(RunbookSteps), ["DecisionId"] = typeof(GoNoGoDecisions),
        ["PirId"] = typeof(PostImplementationReviews), ["CommTemplateId"] = typeof(CommTemplates), ["DispatchId"] = typeof(CommDispatches),
        ["WebhookDestinationId"] = typeof(WebhookDestinations), ["FreezeWindowId"] = typeof(FreezeWindows), ["ExportJobId"] = typeof(ExportJobs),
        ["OnCallTeamId"] = typeof(Teams), ["ReleaseTypeId"] = typeof(ReleaseTypes), ["EnvironmentId"] = typeof(Environments), ["CertEnvironmentId"] = typeof(Environments), ["CatalogServiceId"] = typeof(CatalogServices),
        ["DefaultTemplateId"] = typeof(TrainTemplates), ["ControlWebhookDestinationId"] = typeof(WebhookDestinations), ["DeploymentId"] = typeof(Deployments), ["IngestKeyId"] = typeof(IngestKeys),
        ["PolicyId"] = typeof(ApprovalPolicies), ["ApprovalId"] = typeof(Approvals), ["BreachId"] = typeof(ThresholdBreaches),
    };

    /// <summary>
    /// Inside a workspace: a new root row takes the workspace (the entities default to 'default'; a nullable column set to null stays host-wide), a row
    /// written for another workspace is refused, and every reference written must name a row visible in this workspace. The triggers keep rows of one
    /// workspace consistent with each other; this keeps a request from reaching into another workspace by id (CLAUDE.md rule 8: refused, never silent).
    /// </summary>
    private async Task EnforceWorkspaceAsync(CancellationToken ct)
    {
        if (WorkspaceId is null) return;
        var entries = ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();
        var adding = entries.Where(e => e.State == EntityState.Added && e.Metadata.FindProperty("Id") is not null)
            .Select(e => (e.Metadata.ClrType, Id: e.Property("Id").CurrentValue as string)).ToHashSet();
        var wanted = new Dictionary<Type, HashSet<string>>();
        foreach (var e in entries)
        {
            if (e.Entity is AuditEvents or WorkspaceMembers or Workspaces) { StampAudit(e); continue; }
            if (e.State == EntityState.Added && e.Metadata.FindProperty("WorkspaceId") is not null)
            {
                var p = e.Property("WorkspaceId");
                var v = p.CurrentValue as string;
                if (v == WorkspaceIds.Default) p.CurrentValue = WorkspaceId;
                else if (v is not null && v != WorkspaceId) throw new WorkspaceBoundaryException($"{e.Metadata.ClrType.Name} written for another workspace");
            }
            foreach (var (column, table) in References)
            {
                if (e.Metadata.FindProperty(column) is null) continue;
                var p = e.Property(column);
                if (p.CurrentValue is not string id || (e.State == EntityState.Modified && !p.IsModified)) continue;
                if (adding.Contains((table, id))) continue;   // written in this same save, and checked as such
                (wanted.TryGetValue(table, out var set) ? set : wanted[table] = []).Add(id);
            }
        }
        foreach (var (table, ids) in wanted)
        {
            var visible = await VisibleIdsAsync(table, ids, ct);
            if (visible.Count != ids.Count) throw new WorkspaceBoundaryException($"A {table.Name} row referenced by this change is not in the current workspace");
        }
    }

    private void StampAudit(EntityEntry e)
    {
        if (e.Entity is AuditEvents a && e.State == EntityState.Added && a.WorkspaceId == WorkspaceIds.Default) a.WorkspaceId = WorkspaceId;
    }

    private async Task<HashSet<string>> VisibleIdsAsync(Type table, HashSet<string> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        IQueryable<string> q = table.Name switch
        {
            nameof(ReleaseTrains) => Set<ReleaseTrains>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(StageGates) => Set<StageGates>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(BundledProducts) => Set<BundledProducts>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(Teams) => Set<Teams>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(TrainTemplates) => Set<TrainTemplates>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(CommTemplateLibrary) => Set<CommTemplateLibrary>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(RunbookRuns) => Set<RunbookRuns>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(RunbookSteps) => Set<RunbookSteps>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(GoNoGoDecisions) => Set<GoNoGoDecisions>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(PostImplementationReviews) => Set<PostImplementationReviews>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(CommTemplates) => Set<CommTemplates>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(CommDispatches) => Set<CommDispatches>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(WebhookDestinations) => Set<WebhookDestinations>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(FreezeWindows) => Set<FreezeWindows>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(ExportJobs) => Set<ExportJobs>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(ReleaseTypes) => Set<ReleaseTypes>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(Environments) => Set<Environments>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(CatalogServices) => Set<CatalogServices>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(Deployments) => Set<Deployments>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(IngestKeys) => Set<IngestKeys>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(ApprovalPolicies) => Set<ApprovalPolicies>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(Approvals) => Set<Approvals>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            nameof(ThresholdBreaches) => Set<ThresholdBreaches>().Where(x => list.Contains(x.Id)).Select(x => x.Id),
            _ => throw new InvalidOperationException($"No workspace check for {table.Name}"),
        };
        return [.. await q.ToListAsync(ct)];
    }
}

/// <summary>A write named a row of another workspace (or wrote a row for one). The API answers 404, as for a row that does not exist (Q-105b).</summary>
public sealed class WorkspaceBoundaryException(string message) : InvalidOperationException(message);
