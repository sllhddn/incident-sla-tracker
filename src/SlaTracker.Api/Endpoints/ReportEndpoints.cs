using Microsoft.EntityFrameworkCore;
using SlaTracker.Api.Data;

namespace SlaTracker.Api.Endpoints;

public static class ReportEndpoints
{
    public static RouteGroupBuilder MapReports(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/reports").WithTags("Reports");

        // SLA compliance per severity for incidents opened in [from, to). Defaults to the last 30 days.
        g.MapGet("/sla", async (TrackerDbContext db, TimeProvider clock, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var (f, t) = Range(from, to, now);
            var rows = await db.Database
                .SqlQuery<SlaSummaryRow>($"EXEC dbo.usp_SlaSummary @From = {f}, @To = {t}, @Now = {now}")
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new SlaSummaryDto(
                r.Severity, r.Total, r.StillOpen, Pct(r.ResponseMet, r.Total), Pct(r.ResolveMet, r.Total),
                Round(r.AvgResolveMinutes), Round(r.P90ResolveMinutes))));
        });

        // Change failure rate per month (a DORA metric): failed deployments / all deployments.
        g.MapGet("/change-failure-rate", async (TrackerDbContext db, TimeProvider clock, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            var (f, t) = Range(from, to, clock.GetUtcNow(), defaultDays: 180);
            var rows = await db.Database
                .SqlQuery<ChangeFailureRow>($"EXEC dbo.usp_ChangeFailureRate @From = {f}, @To = {t}")
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new ChangeFailureDto(r.Month, r.Deployments, r.Failed, Pct(r.Failed, r.Deployments))));
        });

        return g;
    }

    private static (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now, int defaultDays = 30)
    {
        var t = to ?? now.AddMinutes(1);
        var f = from ?? t.AddDays(-defaultDays);
        if (f >= t) throw new RequestValidationException(new Dictionary<string, string[]> { ["from"] = ["must be before to"] });
        return (f, t);
    }

    private static double Pct(int part, int whole) => whole == 0 ? 0 : Math.Round(100.0 * part / whole, 1);
    private static double? Round(double? x) => x is null ? null : Math.Round(x.Value, 1);
}
