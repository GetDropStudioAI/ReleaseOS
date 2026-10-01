using System.Net;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Infrastructure.Tests.SyncTestKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-75 / Q-SEC-C3 (decided 2026-09-30): outside Development, connectors reach only Atlassian Cloud and ServiceNow on 443 unless Sync:AllowedHosts says otherwise.</summary>
public sealed class ConnectorHostAllowlistTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private static SyncOptions Opts(string? hosts, bool dev) =>
        SyncOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:AllowedHosts"] = hosts }).Build(), isDevelopment: dev);

    [Theory]
    [InlineData("https://acme.atlassian.net", null)]
    [InlineData("https://acme.atlassian.net/jira", null)]
    [InlineData("https://acme.atlassian.net:443", null)]
    [InlineData("https://acme.service-now.com", null)]
    [InlineData("https://acme.atlassian.net:8443", "Port 8443")]
    [InlineData("https://acme.service-now.com:444", "Port 444")]
    [InlineData("https://jira.example.com", "not on the allowed list")]
    [InlineData("https://atlassian.net", "not on the allowed list")]                 // "*.x" is a subdomain, not x itself
    [InlineData("https://acme.atlassian.net.evil.example", "not on the allowed list")]
    [InlineData("https://evil-service-now.com", "not on the allowed list")]
    public void Outside_Development_the_default_is_Atlassian_Cloud_and_ServiceNow_on_443(string url, string? problem)
    {
        var o = Opts(null, dev: false);
        Assert.Equal(SyncOptions.DefaultAllowedHosts, o.AllowedHosts);
        var p = ConnectorUrlPolicy.Validate(url, false, o);
        if (problem is null) Assert.Null(p);
        else { Assert.Contains(problem, p); Assert.Contains("Sync:AllowedHosts", p); }
    }

    [Fact]
    public void Explicit_configuration_replaces_the_default_entries_carry_their_port_and_star_means_any_public_host()
    {
        var onPrem = Opts("jira.example.com:8443; servicenow.example.com", dev: false);
        Assert.Null(ConnectorUrlPolicy.Validate("https://jira.example.com:8443", false, onPrem));
        Assert.Null(ConnectorUrlPolicy.Validate("https://servicenow.example.com", false, onPrem));
        Assert.Contains("Port 443", ConnectorUrlPolicy.Validate("https://jira.example.com", false, onPrem));         // the port is part of the entry
        Assert.Contains("Port 8443", ConnectorUrlPolicy.Validate("https://servicenow.example.com:8443", false, onPrem));
        Assert.Contains("not on the allowed list", ConnectorUrlPolicy.Validate("https://acme.atlassian.net", false, onPrem));   // replaced, not added to

        var any = Opts("*", dev: false);
        Assert.Empty(any.AllowedHosts);
        Assert.Null(ConnectorUrlPolicy.Validate("https://jira.example.com:8443", false, any));
        Assert.Contains("private", ConnectorUrlPolicy.Validate("https://10.0.0.5", false, any, ip => ip.ToString().StartsWith("10.")));   // the address rule still holds
    }

    [Fact]
    public void Development_keeps_any_host_so_the_fakes_work()
    {
        var o = Opts(null, dev: true);
        Assert.Empty(o.AllowedHosts);
        Assert.Null(ConnectorUrlPolicy.Validate("http://localhost:6081", true, o));
        Assert.Null(ConnectorUrlPolicy.Validate("https://jira.example.com:8443", true, o));
    }

    [Fact]
    public async Task Saving_a_connector_on_another_host_is_a_readable_422_naming_the_key_and_nothing_is_stored()
    {
        var e = NewEnv(fx, serviceNow: false);   // a Production environment with the default list
        var r = await e.Service.SaveSettingsAsync("Jira", "https://jira.example.com", null, new Actor("rte"), null);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        Assert.Equal(ConnectorGuards.ConnectorInvalid, r.Failures[0].Guard);
        Assert.Contains("jira.example.com", r.Failures[0].Message);
        Assert.Contains("Sync:AllowedHosts", r.Failures[0].Message);
        Assert.Equal(0, e.Count("SELECT COUNT(*) FROM ConnectorState"));
        Assert.True((await e.Service.SaveSettingsAsync("Jira", JiraUrl, null, new Actor("rte"), null)).IsOk);
    }

    [Fact]
    public async Task A_connector_saved_before_the_default_is_refused_at_the_next_cycle_visibly()
    {
        var e = NewEnv(fx, serviceNow: false);
        e.Creds.Items["Jira"] = new ConnectorCredentials(ConnectorCredentials.ApiToken, "rae@example.com", Secret);
        e.Sql("INSERT INTO ConnectorState(SourceSystem,BaseUrl) VALUES('Jira','https://jira.example.com')");   // stored under the old open default
        var test = await e.Service.TestAsync("Jira", new Actor("rte"));
        Assert.Equal(ConnectorGuards.ConnectorTestFailed, test.Failures.Single().Guard);
        Assert.Contains("Sync:AllowedHosts", test.Failures[0].Message);
        e.Sql("INSERT INTO ExternalLinks(Id,ReleaseTrainId,EntityType,EntityId,SourceSystem,ExternalKey) VALUES('l1','t1','Train','t1','Jira','PAY-1')");
        var cycle = await e.Poller.RunConnectorAsync("Jira");
        Assert.Equal(ConnectorCycleResult.Failed, cycle.Outcome);       // a failed cycle (counted, alerted after the threshold), never silent
        Assert.Contains("Sync:AllowedHosts", cycle.Message);
        Assert.Equal(0, e.Http.Count);                                  // and nothing was sent there
    }
}
