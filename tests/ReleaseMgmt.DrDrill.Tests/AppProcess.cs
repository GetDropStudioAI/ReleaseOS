using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ReleaseMgmt.DrDrill;

/// <summary>
/// Runs the real API (<c>ReleaseMgmt.Api.dll</c>, the one that ships) as a child process, exactly as an operator would: working directory = the instance
/// root, so the production defaults apply (<c>data/releasemgmt.db</c>, <c>data/keys</c>, <c>data/secrets</c>, <c>data/attachments</c>, <c>data/backups</c>).
/// Shared by the DR drill and the load test (linked into that project), so both exercise the built application and not a test host.
/// </summary>
public sealed class AppProcess : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly StringBuilder _output = new();
    public Uri BaseUri { get; }
    public string Root { get; }
    public int Pid => _proc.Id;
    public string Output { get { lock (_output) return _output.ToString(); } }
    public TimeSpan CpuTime { get { _proc.Refresh(); return _proc.TotalProcessorTime; } }
    public long WorkingSetBytes { get { _proc.Refresh(); return _proc.WorkingSet64; } }

    private AppProcess(Process p, string root, Uri baseUri) { _proc = p; Root = root; BaseUri = baseUri; }

    /// <summary>
    /// The API exactly as built for the API project (src/ReleaseMgmt.Api/bin/{Configuration}/net10.0), not the copy beside the calling assembly: the test and load
    /// projects carry other packages' dependency graphs, and Serilog's configuration reader then fails to load enrichers that the API's own deps.json does not list.
    /// RELEASEMGMT_API_DLL overrides. The project reference guarantees the API is built before this runs.
    /// </summary>
    public static string ApiDll { get; } = ResolveApiDll();

    private static string ResolveApiDll()
    {
        var overridePath = Environment.GetEnvironmentVariable("RELEASEMGMT_API_DLL");
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var config = bin.Parent?.Name ?? "Release";   // .../bin/{Configuration}/net10.0
        for (var d = bin; d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ReleaseMgmt.sln")))
            {
                var dll = Path.Combine(d.FullName, "src", "ReleaseMgmt.Api", "bin", config, "net10.0", "ReleaseMgmt.Api.dll");
                if (File.Exists(dll)) return dll;
                throw new FileNotFoundException($"Build the API first (dotnet build ReleaseMgmt.sln -c {config}); expected {dll}");
            }
        return Path.Combine(AppContext.BaseDirectory, "ReleaseMgmt.Api.dll");
    }

    public static string DotnetHost()
    {
        var fromCli = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");   // set by `dotnet test` and `dotnet run`
        if (!string.IsNullOrEmpty(fromCli) && File.Exists(fromCli)) return fromCli;
        var self = Environment.ProcessPath;
        return self is not null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? self : "dotnet";
    }

    private static ProcessStartInfo Psi(string root, IReadOnlyDictionary<string, string?>? env, params string[] args)
    {
        var psi = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add(ApiDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["ASPNETCORE_CONTENTROOT"] = Path.GetDirectoryName(ApiDll)!;   // appsettings.json lives beside the dll; relative data paths still resolve against the working directory
        psi.Environment["Logging__File"] = Path.Combine(root, "logs", "releasemgmt-.log");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (env is not null) foreach (var (k, v) in env) { if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v; }
        return psi;
    }

    /// <summary>Runs a one-shot CLI verb (for example <c>restore</c>) and returns its exit code and combined output.</summary>
    public static async Task<(int ExitCode, string Output)> RunCliAsync(string root, IReadOnlyDictionary<string, string?>? env, params string[] args)
    {
        using var p = Process.Start(Psi(root, env, args)) ?? throw new InvalidOperationException("Could not start dotnet");
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, (await so) + (await se));
    }

    public static async Task<AppProcess> StartAsync(string root, IReadOnlyDictionary<string, string?>? env = null, TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(root);
        var port = FreePort();
        var uri = new Uri($"http://127.0.0.1:{port}");
        var merged = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",   // dev-login and the seed endpoints exist only here
            ["ASPNETCORE_URLS"] = uri.ToString().TrimEnd('/'),
            ["Seed__Demo"] = "false",
        };
        if (env is not null) foreach (var (k, v) in env) merged[k] = v;
        var p = Process.Start(Psi(root, merged)) ?? throw new InvalidOperationException("Could not start dotnet");
        var app = new AppProcess(p, root, uri);
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (app._output) app._output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (app._output) app._output.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        try { await app.WaitHealthyAsync(timeout ?? TimeSpan.FromSeconds(90)); }
        catch { await app.DisposeAsync(); throw; }
        return app;
    }

    public HttpClient NewClient(bool cookies = true) => new(new SocketsHttpHandler { UseCookies = cookies, PooledConnectionLifetime = TimeSpan.FromMinutes(5), UseProxy = false }) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(60) };

    private async Task WaitHealthyAsync(TimeSpan timeout)
    {
        using var http = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(3) };
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (_proc.HasExited) throw new InvalidOperationException($"The API exited with code {_proc.ExitCode} before it became healthy:\n{Output}");
            try { using var r = await http.GetAsync("/healthz"); if (r.StatusCode == HttpStatusCode.OK) return; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* not listening yet */ }
            await Task.Delay(100);
        }
        throw new TimeoutException($"/healthz was not healthy within {timeout.TotalSeconds:0}s:\n{Output}");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    /// <summary>Stops the process (host loss / clean shutdown are the same for this purpose: the DB is WAL and crash-safe).</summary>
    public async ValueTask DisposeAsync()
    {
        try { if (!_proc.HasExited) { _proc.Kill(entireProcessTree: true); await _proc.WaitForExitAsync(); } }
        catch (InvalidOperationException) { /* already gone */ }
        _proc.Dispose();
    }
}
