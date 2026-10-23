using Microsoft.Data.Sqlite;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-110: the domain's table of deployment state moves (<see cref="DeploymentStates.CanMove"/>, which the service checks first) and trg_Deployment_Transition (the backstop) agree on every pair of states.</summary>
public sealed class DeploymentStateTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    [Fact]
    public void The_service_table_and_the_trigger_allow_exactly_the_same_moves()
    {
        const string now = TriggerSuiteFixture.Now;
        using var db = fx.Fresh();
        TriggerSuiteFixture.Run(db, $@"
            INSERT INTO Environments(Id,Name,Kind,CreatedAt) VALUES('e1','Staging','Staging','{now}');
            INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv1','payments-api','Payments','{now}');");
        var disagreements = new List<string>();
        var n = 0;
        foreach (var from in DeploymentStates.All)
            foreach (var to in DeploymentStates.All.Where(t => t != from))
            {
                var id = $"d{++n}";
                var finished = DeploymentStates.IsTerminal(from) ? $"'{now}'" : "NULL";
                TriggerSuiteFixture.Run(db, $@"INSERT INTO Deployments(Id,ExternalId,CatalogServiceId,EnvironmentId,ReleaseVersion,State,StartedAt,FinishedAt,CreatedAt,UpdatedAt)
                    VALUES('{id}','x-{id}','sv1','e1','1.0','{from}','{now}',{finished},'{now}','{now}')");
                var toFinished = DeploymentStates.IsTerminal(to) ? $"'{now}'" : "NULL";
                bool allowedByDb;
                try { TriggerSuiteFixture.Run(db, $"UPDATE Deployments SET State='{to}', FinishedAt={toFinished} WHERE Id='{id}'"); allowedByDb = true; }
                catch (SqliteException e) when (e.SqliteErrorCode == 19 && e.Message.Contains("Illegal deployment state transition")) { allowedByDb = false; }
                if (allowedByDb != DeploymentStates.CanMove(from, to)) disagreements.Add($"{from} -> {to}: trigger {(allowedByDb ? "allows" : "refuses")}, CanMove {(DeploymentStates.CanMove(from, to) ? "allows" : "refuses")}");
            }
        Assert.Empty(disagreements);
        Assert.Equal(11, DeploymentStates.All.SelectMany(f => DeploymentStates.All.Select(t => (f, t))).Count(x => DeploymentStates.CanMove(x.f, x.t)));
    }

    [Theory]
    [InlineData("RLS", true)] [InlineData("AB", true)] [InlineData("ABCDE12345", true)] [InlineData("A", false)] [InlineData("ABCDEFGHIJK", false)] [InlineData("rls", false)] [InlineData("RL-S", false)] [InlineData("", false)] [InlineData(null, false)]
    public void A_key_prefix_is_two_to_ten_capital_letters_or_digits(string? prefix, bool valid) => Assert.Equal(valid, ReleaseKeys.IsValidPrefix(prefix));

    [Fact]
    public void The_release_line_of_the_documents_lists_what_is_set_and_nothing_else()
    {
        Assert.Null(new ReleaseMgmt.Infrastructure.Exports.ReleaseInfo(null, null, null, null, null, null, []).Line());
        Assert.Equal("RLS-12 · Owner Rae Tran · Team Operations · Branch release/27.01 · On call SRE · Type CERT · Environments Staging, Production",
            new ReleaseMgmt.Infrastructure.Exports.ReleaseInfo("RLS-12", "Rae Tran", "Operations", "release/27.01", "SRE", "CERT", ["Staging", "Production"]).Line());
        Assert.Equal("RLS-3 · Branch main", new ReleaseMgmt.Infrastructure.Exports.ReleaseInfo("RLS-3", null, null, "main", null, null, []).Line());
    }
}
