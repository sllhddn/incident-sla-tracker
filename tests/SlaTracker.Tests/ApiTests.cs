using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SlaTracker.Api.Data;
using SlaTracker.Api.Domain;
using SlaTracker.Api.Endpoints;

namespace SlaTracker.Tests;

[Collection("sql")]
public class ApiTests(SqlServerFixture fx)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _http = fx.App.CreateClient();

    private async Task<IncidentDto> Open(Severity sev = Severity.P2, string? release = null)
    {
        var res = await _http.PostAsJsonAsync("/api/incidents",
            new OpenIncidentRequest("Search page slow", "Search", sev, "service.desk", release), Json);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<IncidentDto>(Json))!;
    }

    private async Task<IncidentDto> Post(string url, object body, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var res = await _http.PostAsJsonAsync(url, body, Json);
        Assert.Equal(expect, res.StatusCode);
        return expect == HttpStatusCode.OK ? (await res.Content.ReadFromJsonAsync<IncidentDto>(Json))! : null!;
    }

    [Fact]
    public async Task Health_is_up_and_migrations_are_recorded_once()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/health")).StatusCode);
        var again = await new SchemaMigrator(fx.ConnectionString, NullLogger<SchemaMigrator>.Instance).MigrateAsync();
        Assert.Empty(again);
    }

    [Fact]
    public async Task Editing_an_applied_migration_stops_start_up()
    {
        var edited = SchemaMigrator.EmbeddedScripts()
            .Select(s => s.Name == "V001__tables.sql" ? new SchemaMigrator.Script(s.Name, s.Sql + "\n-- sneaky edit") : s).ToList();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SchemaMigrator(fx.ConnectionString, NullLogger<SchemaMigrator>.Instance).MigrateAsync(edited));
        Assert.Contains("V001__tables.sql", ex.Message);
    }

    [Fact]
    public async Task Opening_an_incident_assigns_a_sequence_reference_and_deadlines()
    {
        var a = await Open(Severity.P1);
        var b = await Open(Severity.P1);
        Assert.Matches(@"^INC-\d{4,}$", a.Ref);
        Assert.Equal(long.Parse(a.Ref[4..]) + 1, long.Parse(b.Ref[4..]));
        Assert.Equal(a.OpenedAt.AddMinutes(15), a.ResponseDueAt);
        Assert.Equal(a.OpenedAt.AddHours(4), a.ResolveDueAt);
        Assert.Equal("OPENED", Assert.Single(a.Events!).Kind);
    }

    [Fact]
    public async Task Workflow_moves_forward_and_refuses_invalid_steps()
    {
        var i = await Open();
        var url = $"/api/incidents/{i.Ref}";
        await Post($"{url}/close", new ActorRequest("ali"), HttpStatusCode.Conflict);         // cannot close an open incident
        Assert.Equal(IncidentStatus.Acknowledged, (await Post($"{url}/acknowledge", new ActorRequest("ali"))).Status);
        Assert.Equal(IncidentStatus.Resolved, (await Post($"{url}/resolve", new ResolveRequest("ali", "Added an index"))).Status);
        var closed = await Post($"{url}/close", new ActorRequest("lead"));
        Assert.Equal(IncidentStatus.Closed, closed.Status);
        Assert.Equal(new[] { "OPENED", "ACKNOWLEDGED", "RESOLVED", "CLOSED" }, closed.Events!.Select(e => e.Kind));
        await Post($"{url}/reopen", new ReopenRequest("lead", "again"), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Invalid_input_returns_field_errors()
    {
        var res = await _http.PostAsJsonAsync("/api/incidents", new { title = "", service = "Search", severity = "P2", openedBy = "x" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty("title", out _));

        var badEnum = await _http.PostAsJsonAsync("/api/incidents", new { title = "t", service = "s", severity = "P9", openedBy = "x" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, badEnum.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/incidents/INC-0")).StatusCode);
    }

    [Fact]
    public async Task Breaching_filter_finds_incidents_past_their_resolve_deadline()
    {
        var i = await Open(Severity.P1);
        fx.Clock.Advance(TimeSpan.FromHours(5));
        try
        {
            var page = await _http.GetFromJsonAsync<PagedResult<IncidentDto>>("/api/incidents?breachingOnly=true&size=100", Json);
            var found = Assert.Single(page!.Items, x => x.Ref == i.Ref);
            Assert.True(found.ResolveBreached);
            Assert.True(found.MinutesToResolveDue < 0);
        }
        finally
        {
            await Post($"/api/incidents/{i.Ref}/resolve", new ResolveRequest("ali", "cleanup"));
        }
    }

    [Fact]
    public async Task Sla_report_computes_compliance_average_and_p90_in_sql()
    {
        var start = new DateTimeOffset(2031, 3, 2, 8, 0, 0, TimeSpan.Zero);   // a window no other test uses
        fx.Clock.Now = start;
        var met = await Open(Severity.P2);
        var missed = await Open(Severity.P2);

        fx.Clock.Now = start.AddMinutes(10);
        await Post($"/api/incidents/{met.Ref}/acknowledge", new ActorRequest("ali"));
        fx.Clock.Now = start.AddMinutes(60);                                     // P2 response target is 30 min
        await Post($"/api/incidents/{missed.Ref}/acknowledge", new ActorRequest("ali"));
        fx.Clock.Now = start.AddHours(2);
        await Post($"/api/incidents/{met.Ref}/resolve", new ResolveRequest("ali", "fixed"));
        fx.Clock.Now = start.AddHours(10);                                       // P2 resolve target is 8 h
        await Post($"/api/incidents/{missed.Ref}/resolve", new ResolveRequest("ali", "fixed late"));

        var from = Uri.EscapeDataString(start.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(start.AddDays(1).ToString("O"));
        var rows = await _http.GetFromJsonAsync<List<SlaSummaryDto>>($"/api/reports/sla?from={from}&to={to}", Json);
        var p2 = Assert.Single(rows!);
        Assert.Equal("P2", p2.Severity);
        Assert.Equal(2, p2.Total);
        Assert.Equal(50.0, p2.ResponseCompliancePct);
        Assert.Equal(50.0, p2.ResolveCompliancePct);
        Assert.Equal(360.0, p2.AvgResolveMinutes);      // (120 + 600) / 2
        Assert.Equal(552.0, p2.P90ResolveMinutes);      // 120 + 0.9 * (600 - 120)
        fx.Clock.Now = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task Change_failure_rate_counts_rollbacks_and_releases_that_caused_incidents()
    {
        var day = new DateTimeOffset(2032, 6, 10, 22, 0, 0, TimeSpan.Zero);
        fx.Clock.Now = day;
        foreach (var v in new[] { "32.6.1", "32.6.2", "32.6.3" })
        {
            Assert.Equal(HttpStatusCode.Created, (await _http.PostAsJsonAsync("/api/releases", new CreateReleaseRequest(v, day, null), Json)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync($"/api/releases/{v}/deploy", null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/releases/32.6.2/rollback", new RollbackRequest("login broken"), Json)).StatusCode);
        var caused = await Open(Severity.P3, release: "32.6.3");
        Assert.Equal("32.6.3", caused.CausedByRelease);

        var rows = await _http.GetFromJsonAsync<List<ChangeFailureDto>>(
            $"/api/reports/change-failure-rate?from={Uri.EscapeDataString(day.AddDays(-30).ToString("O"))}&to={Uri.EscapeDataString(day.AddDays(30).ToString("O"))}", Json);
        var june = Assert.Single(rows!);
        Assert.Equal("2032-06", june.Month);
        Assert.Equal(3, june.Deployments);
        Assert.Equal(2, june.Failed);
        Assert.Equal(66.7, june.ChangeFailureRatePct);
        fx.Clock.Now = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task Concurrent_edits_are_detected_with_rowversion()
    {
        var created = await Open();
        using var s1 = fx.App.Services.CreateScope();
        using var s2 = fx.App.Services.CreateScope();
        var db1 = s1.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var db2 = s2.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var a = await db1.Incidents.SingleAsync(i => i.Ref == created.Ref);
        var b = await db2.Incidents.SingleAsync(i => i.Ref == created.Ref);

        a.Acknowledge("officer.one", fx.Clock.GetUtcNow());
        await db1.SaveChangesAsync();
        b.Resolve("officer two was working from an old copy", "officer.two", fx.Clock.GetUtcNow());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task Schema_has_the_filtered_index_for_open_incidents()
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT filter_definition FROM sys.indexes WHERE name = 'IX_Incident_Open_ResolveDue' AND has_filter = 1", conn);
        var filter = (string?)await cmd.ExecuteScalarAsync();
        Assert.NotNull(filter);
        Assert.Contains("Open", filter);
    }
}
