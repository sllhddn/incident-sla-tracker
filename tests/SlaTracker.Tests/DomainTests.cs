using SlaTracker.Api.Data;
using SlaTracker.Api.Domain;

namespace SlaTracker.Tests;

public class IncidentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly SlaPolicy P1 = new(Severity.P1, 15, 240);

    private static Incident NewIncident() => Incident.Open("INC-1", "Payments down", "Payments", P1, T0, "desk");

    [Fact]
    public void Open_sets_deadlines_from_the_policy()
    {
        var i = NewIncident();
        Assert.Equal(IncidentStatus.Open, i.Status);
        Assert.Equal(T0.AddMinutes(15), i.ResponseDueAt);
        Assert.Equal(T0.AddHours(4), i.ResolveDueAt);
        Assert.Single(i.Events, e => e.Kind == "OPENED");
    }

    [Fact]
    public void Full_workflow_records_every_step()
    {
        var i = NewIncident();
        i.Acknowledge("ali", T0.AddMinutes(5));
        i.Resolve("Renewed gateway certificate", "ali", T0.AddHours(1));
        i.Close("lead", T0.AddHours(2));
        Assert.Equal(IncidentStatus.Closed, i.Status);
        Assert.Equal(new[] { "OPENED", "ACKNOWLEDGED", "RESOLVED", "CLOSED" }, i.Events.Select(e => e.Kind));
        Assert.False(i.ResponseBreached(T0.AddDays(1)));
        Assert.False(i.ResolveBreached(T0.AddDays(1)));
    }

    [Fact]
    public void Breaches_are_measured_against_the_moment_of_response_and_resolution()
    {
        var i = NewIncident();
        Assert.False(i.ResponseBreached(T0.AddMinutes(10)));
        Assert.True(i.ResponseBreached(T0.AddMinutes(16)));          // still open after 16 min
        i.Acknowledge("ali", T0.AddMinutes(20));
        Assert.True(i.ResponseBreached(T0.AddDays(2)));              // acknowledged late, stays breached
        i.Resolve("fixed", "ali", T0.AddHours(3));
        Assert.False(i.ResolveBreached(T0.AddDays(2)));              // resolved inside 4 h
    }

    [Theory]
    [InlineData("close")]
    [InlineData("reopen")]
    public void Open_incident_cannot_skip_steps(string action)
    {
        var i = NewIncident();
        var ex = Assert.Throws<DomainException>(() =>
        {
            if (action == "close") i.Close("x", T0);
            else i.Reopen("why", "x", T0);
        });
        Assert.Contains("Open", ex.Message);
    }

    [Fact]
    public void Resolve_requires_a_summary_and_counts_as_response()
    {
        var i = NewIncident();
        Assert.Throws<DomainException>(() => i.Resolve(" ", "ali", T0));
        i.Resolve("restarted node", "ali", T0.AddMinutes(10));
        Assert.Equal(T0.AddMinutes(10), i.AcknowledgedAt);
        Assert.Equal("ali", i.Assignee);
    }

    [Fact]
    public void Reopen_keeps_the_original_deadline()
    {
        var i = NewIncident();
        i.Resolve("first fix", "ali", T0.AddHours(1));
        i.Reopen("came back", "desk", T0.AddHours(2));
        Assert.Equal(IncidentStatus.Acknowledged, i.Status);
        Assert.Null(i.ResolvedAt);
        Assert.Equal(T0.AddHours(4), i.ResolveDueAt);
        Assert.True(i.ResolveBreached(T0.AddHours(5)));
    }

    [Fact]
    public void Policy_rejects_impossible_targets()
    {
        Assert.Throws<ArgumentException>(() => new SlaPolicy(Severity.P2, 60, 30));
        Assert.Throws<ArgumentException>(() => new SlaPolicy(Severity.P2, 0, 30));
    }
}

public class ReleaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_a_deployed_release_can_be_rolled_back()
    {
        var r = new Release("1.4.2", T0, null);
        Assert.Throws<DomainException>(() => r.RollBack("bad", T0));
        r.MarkDeployed(T0);
        Assert.Throws<DomainException>(() => r.MarkDeployed(T0));
        r.RollBack("login broken", T0.AddMinutes(30));
        Assert.Equal(ReleaseStatus.RolledBack, r.Status);
        Assert.Contains("login broken", r.Notes);
    }
}

public class SchemaMigratorTests
{
    [Fact]
    public void Splits_batches_on_GO_lines_only()
    {
        var sql = "CREATE TABLE a (x int);\nGO\nCREATE PROCEDURE p AS SELECT 'GO' AS g;\ngo  \n\nGO\n";
        var batches = SchemaMigrator.SplitBatches(sql).ToList();
        Assert.Equal(2, batches.Count);
        Assert.Contains("'GO'", batches[1]);
    }

    [Fact]
    public void Embedded_scripts_are_found_in_version_order()
    {
        var names = SchemaMigrator.EmbeddedScripts().Select(s => s.Name).ToList();
        Assert.Equal(new[] { "V001__tables.sql", "V002__report_procedures.sql" }, names);
    }

    [Fact]
    public void Checksum_ignores_line_ending_style()
    {
        Assert.Equal(new SchemaMigrator.Script("a", "x\r\ny").Checksum, new SchemaMigrator.Script("a", "x\ny").Checksum);
    }
}
