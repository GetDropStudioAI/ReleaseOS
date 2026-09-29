using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// Reference data (always, idempotent): the default train template (open item 10) and US federal holidays 2026-2027 (open item 13).
/// Demo data (opt-in, <c>Seed:Demo</c>): three trains with four gates each in mixed states, built through the real services
/// so the audit trail and triggers behave exactly as in use.
/// </summary>
public sealed class SeedService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time,
    GateService gates, TaskService tasks, TrainLifecycleService trains)
{
    /// <summary>Open item 10 default gates: (name, class, T-minus business days, required before).</summary>
    public static readonly (string Name, string Class, int Offset, string Before)[] DefaultGates =
    [
        ("Code Freeze", "Standard", 5, "Gated"),
        ("QA Sign-off", "Standard", 3, "Gated"),
        ("Compliance Sign-off", "Compliance", 2, "Executing"),
        ("CAB Approval", "Compliance", 1, "Executing"),
    ];

    public const string DefaultTemplateName = "Standard release";

    public async Task SeedReferenceDataAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var have = (await db.Set<Holidays>().Select(h => h.Day).ToListAsync(ct)).ToHashSet();
        foreach (var (day, name) in new[] { 2026, 2027 }.SelectMany(UsHolidays.ForYear).Where(h => !have.Contains(h.Day)))
            db.Set<Holidays>().Add(new Holidays { Day = day, Name = name });

        if (!await db.Set<TrainTemplates>().AnyAsync(t => t.Name == DefaultTemplateName, ct))
        {
            // Draft: approving needs a person (Approved requires ApprovedByUserId), so the seed never fabricates an approval.
            var t = new TrainTemplates { Id = Ids.New(), Name = DefaultTemplateName, Status = "Draft", DefaultRiskTier = "Moderate" };
            db.Set<TrainTemplates>().Add(t);
            var order = 0;
            foreach (var g in DefaultGates)
                db.Set<TemplateGates>().Add(new TemplateGates { Id = Ids.New(), TemplateId = t.Id, GateName = g.Name, GateClass = g.Class, SequenceOrder = ++order, OffsetDays = g.Offset, RequiredBeforeStatus = g.Before });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SeedDemoDataAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        if (await db.Set<ReleaseTrains>().AnyAsync(ct)) return; // never touch a database that already has trains

        var now = time.GetUtcNow().UtcDateTime;
        var stamp = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var holidays = (await db.Set<Holidays>().Select(h => h.Day).ToListAsync(ct)).ToHashSet();

        Users User(string key, string name, string role) => new() { Id = Ids.New(), Email = $"demo-{key}@example.com", DisplayName = name, Role = role };
        var rte = User("rte", "Rae Tran (RTE)", Roles.RTE);
        var rm = User("rm", "Rel Marsh (Release Manager)", Roles.ReleaseManager);
        var gov1 = User("gov1", "Gina Ortiz (Governance)", Roles.GovernanceOfficer);
        var gov2 = User("gov2", "Gus Patel (Governance)", Roles.GovernanceOfficer);
        var dev = User("dev", "Dee Vega (Viewer)", Roles.Viewer);
        db.Set<Users>().AddRange(rte, rm, gov1, gov2, dev);

        var nextFriday = DateOnly.FromDateTime(stamp);
        while (nextFriday.DayOfWeek != DayOfWeek.Friday) nextFriday = nextFriday.AddDays(1);
        var trainDefs = new[] { ("R26.11 Payments platform", nextFriday.AddDays(21), "High"), ("R26.12 Card portal", nextFriday.AddDays(14), "Moderate"), ("R26.13 Reporting", nextFriday.AddDays(35), "Low") };
        var ids = new List<string>();
        foreach (var (title, target, risk) in trainDefs)
        {
            var t = new ReleaseTrains { Id = Ids.New(), Title = title, TargetReleaseDate = target, RiskTier = risk, CreatedAt = stamp, UpdatedAt = stamp, LastChangedByUserId = rte.Id, LastChangedAt = stamp };
            db.Set<ReleaseTrains>().Add(t);
            ids.Add(t.Id);
            db.Set<DeploymentWindows>().Add(new DeploymentWindows { Id = Ids.New(), ReleaseTrainId = t.Id, StartsAt = new DateTime(target.Year, target.Month, target.Day, 6, 0, 0, DateTimeKind.Utc), EndsAt = new DateTime(target.Year, target.Month, target.Day, 10, 0, 0, DateTimeKind.Utc) });
            var products = new[] { ("Payments API", "4.5.0", "PAY"), ("Card Portal", "2.1.0", "CRD"), ("Ledger Svc", "3.8.2", "LED") }
                .Select(p => new BundledProducts { Id = Ids.New(), ReleaseTrainId = t.Id, ProductName = p.Item1, VersionTag = p.Item2, ProjectCode = p.Item3 }).ToList();
            db.Set<BundledProducts>().AddRange(products);
            if (title.StartsWith("R26.12"))   // one open High and one Medium blocker so the products table shows "At risk"
            {
                db.Set<Blockers>().Add(new Blockers { Id = Ids.New(), ReleaseTrainId = t.Id, BundledProductId = products[1].Id, Title = "Pen-test finding open", Severity = "High", OwnerUserId = rte.Id, RaisedAt = stamp });
                db.Set<Blockers>().Add(new Blockers { Id = Ids.New(), ReleaseTrainId = t.Id, BundledProductId = products[2].Id, Title = "Migration 0042 needs a backout review", Severity = "Medium", OwnerUserId = rte.Id, RaisedAt = stamp });
            }
            var order = 0;
            foreach (var g in DefaultGates)
            {
                var gate = new StageGates
                {
                    Id = Ids.New(), ReleaseTrainId = t.Id, GateName = g.Name, GateClass = g.Class, SequenceOrder = ++order, OffsetDays = g.Offset,
                    DueOn = BusinessDays.SubtractBusinessDays(target, g.Offset, holidays), RequiredBeforeStatus = g.Before,
                    OwnerUserId = g.Class == "Compliance" ? gov1.Id : rte.Id, LastChangedByUserId = rte.Id,
                };
                db.Set<StageGates>().Add(gate);
                db.Set<ChecklistTasks>().Add(new ChecklistTasks { Id = Ids.New(), StageGateId = gate.Id, TaskDescription = g.Class == "Compliance" ? "Evidence attached and reviewed" : $"{g.Name}: all items complete", OwnerUserId = gate.OwnerUserId, SequenceOrder = 1, BundledProductId = products[(order - 1) % products.Count].Id });
            }
        }
        await db.SaveChangesAsync(ct);

        // Mixed states through the services. Train 1: Code Freeze certified, QA in progress. Train 2: both Gated gates certified, train Gated, Compliance in progress.
        var rteA = new Actor(rte.Id);
        async Task<List<StageGates>> GatesOf(string trainId)
        {
            await using var d = await dbf.CreateDbContextAsync(ct);
            return await d.Set<StageGates>().Where(g => g.ReleaseTrainId == trainId).OrderBy(g => g.SequenceOrder).ToListAsync(ct);
        }
        async Task CertifyStandard(StageGates g)
        {
            await using var d = await dbf.CreateDbContextAsync(ct);
            var task = await d.Set<ChecklistTasks>().SingleAsync(x => x.StageGateId == g.Id, ct);
            Require(await tasks.CompleteAsync(task.Id, rteA, ct: ct));
            Require(await gates.StartAsync(g.Id, rteA, ct: ct));
            Require(await gates.CertifyAsync(g.Id, rteA, ct: ct));
        }
        var t1 = await GatesOf(ids[0]);
        await CertifyStandard(t1[0]);
        Require(await gates.StartAsync(t1[1].Id, rteA, ct: ct));

        var t2 = await GatesOf(ids[1]);
        await CertifyStandard(t2[0]);
        await CertifyStandard(t2[1]);
        Require(await trains.AdvanceAsync(ids[1], "Gated", rteA, ct: ct));
        Require(await gates.StartAsync(t2[2].Id, new Actor(gov1.Id), ct: ct));
        // train 3 stays fresh: four Pending gates
    }

    private static void Require<T>(ServiceResult<T> r)
    {
        if (!r.IsOk) throw new InvalidOperationException($"Demo seed step failed: {r.Kind} {string.Join("; ", r.Failures.Select(f => $"{f.Guard}: {f.Message}"))}");
    }
}
