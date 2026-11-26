using System.Reflection;
using System.Text.RegularExpressions;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Infrastructure.Ai;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-183 (D58): two rules enforced by tests, so they cannot erode. (1) Nothing outside the abstraction may reach a model provider. (2) AI is advisory only: no AI code path may call a
/// status-changing domain service. Both read the source tree, because that is where a violation is first written, and the second also reads the compiled types.
/// </summary>
public class AiArchitectureTests
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ReleaseMgmt.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    private static string Rel(string f) => Path.GetRelativePath(Root(), f).Replace('\\', '/');
    private const string ProviderFolder = "src/ReleaseMgmt.Infrastructure/Ai/Providers/";

    /// <summary>Everything that is AI code: the Domain contracts, the platform, and the endpoints and services named Ai*.</summary>
    private static bool IsAiCode(string rel) =>
        rel.StartsWith("src/ReleaseMgmt.Domain/Ai/") || rel.StartsWith("src/ReleaseMgmt.Infrastructure/Ai/") || Regex.IsMatch(rel, @"^src/ReleaseMgmt\.(Api|Infrastructure)/.*/Ai[A-Z][^/]*\.cs$") || Regex.IsMatch(rel, @"^src/ReleaseMgmt\.Api/Ai[^/]*/");

    private static readonly string[] ProviderMarkers =
    [
        "api.anthropic.com", "anthropic-version", "AnthropicAiClient", "using Anthropic", "Anthropic.SDK", "api.openai.com", "generativelanguage.googleapis.com", "Azure.AI.OpenAI",
    ];

    [Fact]
    public void Nothing_outside_the_providers_folder_references_a_provider_sdk_address_or_adapter()
    {
        var offenders = new List<string>();
        foreach (var f in SourceFiles())
        {
            var rel = Rel(f);
            if (rel.StartsWith(ProviderFolder)) continue;
            var text = File.ReadAllText(f);
            foreach (var m in ProviderMarkers.Where(m => text.Contains(m, StringComparison.OrdinalIgnoreCase))) offenders.Add($"{rel}: {m}");
        }
        Assert.True(offenders.Count == 0, "Only Infrastructure/Ai/Providers may reference a model provider. Offenders:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void AI_code_outside_the_providers_folder_never_uses_an_http_client()
    {
        var offenders = SourceFiles().Select(f => (Rel: Rel(f), Text: File.ReadAllText(f)))
            .Where(x => IsAiCode(x.Rel) && !x.Rel.StartsWith(ProviderFolder))
            .Where(x => Regex.IsMatch(x.Text, @"\b(HttpClient|IHttpClientFactory|HttpMessageHandler|WebClient|WebRequest|Socket)\b") && true)
            .Select(x => x.Rel).ToList();
        Assert.True(offenders.Count == 0, "AI code must reach a provider only through the adapter. Offenders:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Only_the_infrastructure_project_may_reference_a_provider_package_and_no_loaded_assembly_is_one()
    {
        var bad = Directory.EnumerateFiles(Root(), "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}") && !f.Replace('\\', '/').Contains("/src/ReleaseMgmt.Infrastructure/"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"PackageReference\s+Include=""(Anthropic|OpenAI|Azure\.AI|Google\.Cloud\.AIPlatform|Mscc\.GenerativeAI)", RegexOptions.IgnoreCase))
            .Select(Rel).ToList();
        Assert.True(bad.Count == 0, "Provider packages belong in ReleaseMgmt.Infrastructure only: " + string.Join(", ", bad));
        var domain = typeof(IAiClient).Assembly;
        Assert.DoesNotContain(domain.GetReferencedAssemblies(), a => Regex.IsMatch(a.Name ?? "", "Anthropic|OpenAI", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void The_adapters_are_registered_only_behind_the_governed_client()
    {
        // Callers get IAiClient; the governed client is its only implementation, so the guards cannot be skipped by injecting an adapter as an IAiClient.
        var implementers = new[] { typeof(IAiClient).Assembly, typeof(GovernedAiClient).Assembly }.SelectMany(a => a.GetTypes())
            .Where(t => !t.IsInterface && !t.IsAbstract && typeof(IAiClient).IsAssignableFrom(t)).ToList();
        Assert.Equal([typeof(GovernedAiClient)], implementers);
        // No code outside the Ai folders takes an IAiProvider (the adapter interface).
        var users = SourceFiles().Where(f => !IsAiCode(Rel(f))).Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bIAiProvider\b")).Select(Rel).ToList();
        Assert.True(users.Count == 0, "Only the AI platform may use IAiProvider: " + string.Join(", ", users));
    }

    // ---- D58: advisory only ----

    private static readonly string[] StatusChangingServices = ["TrainLifecycleService", "GateService", "RunService"];

    [Fact]
    public void No_AI_source_file_names_a_status_changing_domain_service()
    {
        var offenders = SourceFiles().Select(f => (Rel: Rel(f), Text: File.ReadAllText(f))).Where(x => IsAiCode(x.Rel))
            .SelectMany(x => StatusChangingServices.Where(s => Regex.IsMatch(x.Text, $@"\b{s}\b")).Select(s => $"{x.Rel}: {s}")).ToList();
        Assert.True(offenders.Count == 0, "AI is advisory only (D58); it must not call status-changing services:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void No_AI_type_depends_on_a_status_changing_service_or_any_concrete_database_service()
    {
        var asms = new[] { typeof(IAiClient).Assembly, typeof(GovernedAiClient).Assembly, typeof(ReleaseMgmt.Api.Endpoints.AiEndpoints).Assembly }.Distinct();
        var aiTypes = asms.SelectMany(a => a.GetTypes()).Where(t => (t.Namespace ?? "").Split('.').Contains("Ai") || t.Name.StartsWith("Ai", StringComparison.Ordinal) && t.Namespace?.Contains("Endpoints") == true).ToList();
        Assert.NotEmpty(aiTypes);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        bool Forbidden(Type t) => StatusChangingServices.Contains(t.Name) || typeof(ServiceBase).IsAssignableFrom(t);
        var bad = new List<string>();
        foreach (var t in aiTypes)
        {
            var used = t.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
                .Concat(t.GetFields(all).Select(f => f.FieldType)).Concat(t.GetProperties(all).Select(p => p.PropertyType))
                .Concat(t.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));
            foreach (var u in used.SelectMany(Flatten).Where(Forbidden)) bad.Add($"{t.FullName} uses {u.Name}");
        }
        Assert.True(bad.Count == 0, "AI types may use only interfaces (IModuleGate, read-only readiness, ...), never a concrete service that writes:\n" + string.Join('\n', bad.Distinct()));

        static IEnumerable<Type> Flatten(Type t)
        {
            yield return t;
            if (t.HasElementType) foreach (var e in Flatten(t.GetElementType()!)) yield return e;
            if (t.IsGenericType) foreach (var a in t.GetGenericArguments().SelectMany(Flatten)) yield return a;
        }
    }

    [Fact]
    public void The_checks_themselves_can_fail_a_planted_violation_is_found()
    {
        // Guard against a vacuous test: the patterns must match what they are meant to catch.
        Assert.Matches(@"\bGateService\b", "var s = sp.GetRequiredService<GateService>();");
        Assert.Matches(@"\b(HttpClient|IHttpClientFactory)\b", "new HttpClient()");
        Assert.True(IsAiCode("src/ReleaseMgmt.Api/Endpoints/AiEndpoints.cs"));
        Assert.True(IsAiCode("src/ReleaseMgmt.Infrastructure/Ai/GovernedAiClient.cs"));
        Assert.True(IsAiCode("src/ReleaseMgmt.Infrastructure/Services/AiRiskService.cs"));
        Assert.False(IsAiCode("src/ReleaseMgmt.Infrastructure/Services/ModuleService.cs"));
        Assert.Contains(SourceFiles().Select(Rel), f => f.StartsWith(ProviderFolder));   // the folder the rule exempts really exists
    }
}
