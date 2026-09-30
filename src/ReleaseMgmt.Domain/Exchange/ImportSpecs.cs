namespace ReleaseMgmt.Domain.Exchange;

public enum ColumnType { Text, Int, Date, Timestamp, Enum, Email, Handle, Owner, EmailList, CodeList }

/// <summary>
/// One import column (PROJECT_SCOPE 9). <see cref="Required"/> means the column must be in the header <i>and</i> hold a value, except where
/// <see cref="AllowEmpty"/> is set (a team with no members). Optional columns: absent = leave the field alone on update, present but empty = clear it (or default it).
/// </summary>
public sealed record ColumnSpec(string Name, bool Required, ColumnType Type, string[]? Allowed = null, int Min = 0, int Max = 0, string? Pattern = null,
                                bool Upper = false, bool AllowEmpty = false, string? Hint = null, bool Multiline = false);

/// <summary>Key names the upsert key columns; Plan is true for plan imports (refused once the train is Executing/Complete); Admin for admin data.</summary>
public sealed record ImportKindSpec(string Kind, string Label, string Grid, IReadOnlyList<ColumnSpec> Columns, IReadOnlyList<string> Key, bool Plan, bool Admin)
{
    public ColumnSpec? Find(string name) => Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed record ImportError(int Row, string Column, string Message);

public static class ImportLimits
{
    public const int MaxRows = 10_000;
    public const int MaxBytes = 5 * 1024 * 1024;
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);
}

public static class ExchangeGuards
{
    public const string ImportTooLarge = "ImportTooLarge";
    public const string ImportRejected = "ImportRejected";
    public const string UnknownImportKind = "UnknownImportKind";
    public const string InvalidImportMode = "InvalidImportMode";
    public const string ImportHasErrors = "ImportHasErrors";
    public const string ImportExpired = "ImportExpired";
    public const string ImportCommitted = "ImportCommitted";
    public const string ImportTampered = "ImportTampered";
    public const string DecertifyNotAcknowledged = "DecertifyNotAcknowledged";
    public const string UnknownGrid = "UnknownGrid";
}

/// <summary>The nine import kinds (PROJECT_SCOPE 9, and the ImportJobs.Kind CHECK). Column order is the order the section lists them: required first, then optional.</summary>
public static class ImportKinds
{
    public const string Trains = "Trains", Products = "Products", Gates = "Gates", Tasks = "Tasks", RunbookSteps = "RunbookSteps",
                        ExternalLinks = "ExternalLinks", Holidays = "Holidays", Users = "Users", Teams = "Teams";

    public static readonly string[] RiskTiers = ["Low", "Moderate", "High", "VeryHigh"];
    public static readonly string[] Sections = ["PreCheck", "Deploy", "Verify", "Rollback", "Hypercare"];
    public static readonly string[] GateClasses = ["Standard", "Compliance"];
    public static readonly string[] RequiredBefore = ["Gated", "Executing", "Complete"];
    public static readonly string[] EntityTypes = ["Train", "Product", "Gate", "RunbookStep", "Blocker", "KnownIssue"];
    public static readonly string[] Systems = ["Jira", "ServiceNow"];
    public static readonly string[] UserRoles = ["Viewer", "ReleaseManager", "RTE", "GovernanceOfficer"];

    /// <summary>Columns that would set a status directly. Refused with their own message: status changes only through the workflow (CLAUDE.md rule 2).</summary>
    public static readonly string[] StatusColumns = ["Status", "CurrentStatus", "IsCompleted", "Completed", "CertifiedBy", "CertifiedAt", "CloseCode", "Outcome", "SyncState"];

    private static ColumnSpec Req(string n, ColumnType t = ColumnType.Text, string[]? allowed = null, int min = 0, int max = 0, string? pattern = null, bool upper = false, bool allowEmpty = false, string? hint = null) =>
        new(n, true, t, allowed, min, max, pattern, upper, allowEmpty, hint);
    private static ColumnSpec Opt(string n, ColumnType t = ColumnType.Text, string[]? allowed = null, int min = 0, int max = 0, string? pattern = null, bool upper = false, string? hint = null, bool multiline = false) =>
        new(n, false, t, allowed, min, max, pattern, upper, true, hint, multiline);

    public static readonly IReadOnlyList<ImportKindSpec> All =
    [
        new(Trains, "Trains", "trains", [Req("Title", max: 200), Req("TargetReleaseDate", ColumnType.Date), Req("RiskTier", ColumnType.Enum, RiskTiers),
            Opt("Template", max: 200), Opt("ChangeTicketNumber", max: 60), Opt("WindowStart", ColumnType.Timestamp), Opt("WindowEnd", ColumnType.Timestamp)], ["Title"], Plan: false, Admin: false),
        new(Products, "Products", "products", [Req("Train", max: 200), Req("ProductName", max: 200), Req("VersionTag", max: 100), Req("ProjectCode", max: 100)], ["Train", "ProductName"], Plan: true, Admin: false),
        new(Gates, "Gates", "gates", [Req("Train", max: 200), Req("GateName", max: 200), Req("SequenceOrder", ColumnType.Int, min: 1, max: 1000), Req("OffsetDays", ColumnType.Int, min: 0, max: 365),
            Req("RequiredBefore", ColumnType.Enum, RequiredBefore), Req("Owner", ColumnType.Owner), Opt("GateClass", ColumnType.Enum, GateClasses)], ["Train", "SequenceOrder"], Plan: true, Admin: false),
        new(Tasks, "Tasks", "tasks", [Req("Train", max: 200), Req("Gate", max: 200), Req("Description", max: 2000), Req("Owner", ColumnType.Owner),
            Opt("Product", max: 200), Opt("Order", ColumnType.Int, min: 1, max: 100000)], ["Train", "Gate", "Description"], Plan: true, Admin: false),
        new(RunbookSteps, "Runbook steps", "runbook-steps", [Req("Train", max: 200), Req("StepCode", pattern: "^[A-Za-z0-9][A-Za-z0-9-]{0,19}$", upper: true, hint: "letters, digits or dashes, up to 20 characters, for example R-014"),
            Req("Title", max: 300), Req("Section", ColumnType.Enum, Sections), Req("PlannedStart", ColumnType.Timestamp), Req("DurationMin", ColumnType.Int, min: 1, max: 100000), Req("Owner", ColumnType.Owner),
            Opt("Product", max: 200), Opt("DependsOn", ColumnType.CodeList), Opt("Instructions", max: 20000, multiline: true)], ["Train", "StepCode"], Plan: true, Admin: false),
        new(ExternalLinks, "External links", "external-links", [Req("Train", max: 200), Req("EntityType", ColumnType.Enum, EntityTypes), Req("EntityRef", max: 300),
            Req("System", ColumnType.Enum, Systems), Req("Key", max: 100)], ["System", "EntityType", "EntityRef", "Key"], Plan: false, Admin: false),
        new(Holidays, "Holidays", "holidays", [Req("Day", ColumnType.Date), Req("Name", max: 200)], ["Day"], Plan: false, Admin: true),
        new(Users, "Users", "users", [Req("Email", ColumnType.Email, max: 254), Req("DisplayName", max: 200), Req("Role", ColumnType.Enum, UserRoles), Opt("Handle", ColumnType.Handle)], ["Email"], Plan: false, Admin: true),
        new(Teams, "Teams", "teams", [Req("Handle", ColumnType.Handle), Req("Name", max: 200), Req("Members", ColumnType.EmailList, allowEmpty: true)], ["Handle"], Plan: false, Admin: true),
    ];

    public static ImportKindSpec? Get(string? kind) => All.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase));
}
