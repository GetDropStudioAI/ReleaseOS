using System.Text.RegularExpressions;

namespace ReleaseMgmt.Domain.Services;

/// <summary>Guard names of the release model, environments, service catalog and deployments (REOS-107, 109, 110). Kept apart from <see cref="Guards"/> so parallel streams do not collide.</summary>
public static class ReleaseModelGuards
{
    public const string ReleaseInvalid = "ReleaseInvalid";
    public const string KeyPrefixInvalid = "KeyPrefixInvalid";
    public const string ReleaseTypeInvalid = "ReleaseTypeInvalid";
    public const string ReleaseTypeNameTaken = "ReleaseTypeNameTaken";
    /// <summary>RT1 (D46): a release is created with a type, and keeps one.</summary>
    public const string ReleaseTypeRequired = "ReleaseTypeRequired";
    /// <summary>RT1 (D48): a Beta release carries its number, 1 to 3; no other release does.</summary>
    public const string BetaNumberInvalid = "BetaNumberInvalid";
    public const string EnvironmentInvalid = "EnvironmentInvalid";
    public const string EnvironmentNameTaken = "EnvironmentNameTaken";
    public const string ServiceInvalid = "ServiceInvalid";
    public const string ServiceKeyTaken = "ServiceKeyTaken";
    public const string IngestKeyInvalid = "IngestKeyInvalid";
    public const string DeploymentInvalid = "DeploymentInvalid";
    public const string DeploymentIdentityMismatch = "DeploymentIdentityMismatch";
    public const string IllegalDeploymentTransition = "IllegalDeploymentTransition";
    public const string UnknownService = "UnknownService";
    public const string UnknownEnvironment = "UnknownEnvironment";
    public const string UnknownRelease = "UnknownRelease";
    /// <summary>The ingest API's own: 401 for a key that is missing, wrong or revoked; 429 beyond the rate limit.</summary>
    public const string InvalidIngestKey = "InvalidIngestKey";
    public const string RateLimited = "RateLimited";
}

/// <summary>Release keys (REOS-107): <c>PREFIX-number</c>, the prefix a per-workspace setting.</summary>
public static partial class ReleaseKeys
{
    public const int MinPrefix = 2, MaxPrefix = 10, DefaultNumberFloor = 1;
    public const string DefaultPrefix = "RLS";

    [GeneratedRegex("^[A-Z0-9]{2,10}$")]
    private static partial Regex PrefixRegex();

    public static bool IsValidPrefix(string? prefix) => prefix is not null && PrefixRegex().IsMatch(prefix);

    public static string Format(string prefix, long number) => $"{prefix}-{number}";
}

/// <summary>Field limits the schema's CHECKs also hold, shared by the services and the API's body limits.</summary>
public static class ReleaseModelLimits
{
    public const int MaxBranch = 200, MaxEnvironmentName = 100, MaxRegion = 60, MaxReleaseTypeName = 100, MinBetaNumber = 1, MaxBetaNumber = 3, MaxServiceKey = 60, MaxServiceName = 200,
        MaxRepository = 300, MaxProjectCode = 100, MaxKeyName = 100, MaxExternalId = 200, MaxReleaseVersion = 100, MaxEventMessage = 2000, MaxEventsPerCall = 100,
        MaxCanarySteps = 20;
}

/// <summary>A deployment's rollout state (D42) and the moves between states; trg_Deployment_Transition holds the same table as the backstop.</summary>
public static class DeploymentStates
{
    public const string Queued = "Queued", Running = "Running", Paused = "Paused", Successful = "Successful", Failed = "Failed", RolledBack = "RolledBack";
    public static readonly string[] All = [Queued, Running, Paused, Successful, Failed, RolledBack];
    public static readonly string[] Strategies = ["Rolling", "Canary", "Atomic"];
    public static readonly string[] Levels = ["Info", "Warning", "Error"];

    public static bool IsTerminal(string state) => state is Successful or Failed or RolledBack;

    /// <summary>Same state is not a move. Queued to Running or Failed; Running to Paused, Successful, Failed or RolledBack; Paused back to Running, or to Failed or RolledBack;
    /// a finished (Successful or Failed) deployment can still be RolledBack; RolledBack is final.</summary>
    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (Queued, Running or Failed) => true,
        (Running, Paused or Successful or Failed or RolledBack) => true,
        (Paused, Running or Failed or RolledBack) => true,
        (Successful or Failed, RolledBack) => true,
        _ => false,
    };
}
