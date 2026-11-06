using Microsoft.EntityFrameworkCore;
using SlaTracker.Api.Data;
using SlaTracker.Api.Domain;

namespace SlaTracker.Api.Endpoints;

public static class IncidentEndpoints
{
    public static RouteGroupBuilder MapIncidents(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/incidents").WithTags("Incidents");

        g.MapPost("/", async (OpenIncidentRequest req, TrackerDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            new Validator()
                .Required("title", req.Title, 200)
                .Required("service", req.Service, 100)
                .Required("openedBy", req.OpenedBy, 100)
                .Check(Enum.IsDefined(req.Severity), "severity", "must be P1, P2, P3 or P4")
                .Optional("causedByReleaseVersion", req.CausedByReleaseVersion, 30)
                .ThrowIfInvalid();

            var policy = await db.SlaPolicies.SingleAsync(p => p.Severity == req.Severity, ct);
            Release? release = null;
            if (!string.IsNullOrWhiteSpace(req.CausedByReleaseVersion))
                release = await db.Releases.SingleOrDefaultAsync(r => r.Version == req.CausedByReleaseVersion, ct)
                          ?? throw new NotFoundException($"Release {req.CausedByReleaseVersion} not found");

            // Reference number comes from a SQL Server sequence: known before insert, never reused.
            // Not composed (no SingleAsync): SQL Server forbids NEXT VALUE FOR inside the derived table EF would wrap it in.
            var next = (await db.Database.SqlQuery<long>($"SELECT NEXT VALUE FOR dbo.SeqIncidentRef AS [Value]").ToListAsync(ct)).Single();
            var incident = Incident.Open($"INC-{next}", req.Title, req.Service, policy, clock.GetUtcNow(), req.OpenedBy.Trim(), release);
            db.Incidents.Add(incident);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/incidents/{incident.Ref}", IncidentDto.From(incident, clock.GetUtcNow(), withEvents: true));
        });

        g.MapGet("/", async (TrackerDbContext db, TimeProvider clock, IncidentStatus? status, Severity? severity,
                             bool? breachingOnly, int? page, int? size, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            int p = Math.Max(page ?? 0, 0), s = Math.Clamp(size ?? 20, 1, 100);
            var q = db.Incidents.AsNoTracking().Include(i => i.CausedByRelease).AsQueryable();
            if (status is { } st) q = q.Where(i => i.Status == st);
            if (severity is { } sev) q = q.Where(i => i.Severity == sev);
            if (breachingOnly == true)
                q = q.Where(i => (i.Status == IncidentStatus.Open || i.Status == IncidentStatus.Acknowledged) && i.ResolveDueAt < now);
            var total = await q.CountAsync(ct);
            var items = await q.OrderBy(i => i.Severity).ThenByDescending(i => i.OpenedAt).Skip(p * s).Take(s).ToListAsync(ct);
            return Results.Ok(new PagedResult<IncidentDto>(items.Select(i => IncidentDto.From(i, now)).ToList(), p, s, total));
        });

        g.MapGet("/{reference}", async (string reference, TrackerDbContext db, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(IncidentDto.From(await Load(db, reference, ct, tracking: false), clock.GetUtcNow(), withEvents: true)));

        g.MapPost("/{reference}/acknowledge", (string reference, ActorRequest req, TrackerDbContext db, TimeProvider clock, CancellationToken ct) =>
            Change(db, clock, reference, ct, i => { RequireActor(req.By); i.Acknowledge(req.By.Trim(), clock.GetUtcNow()); }));

        g.MapPost("/{reference}/resolve", (string reference, ResolveRequest req, TrackerDbContext db, TimeProvider clock, CancellationToken ct) =>
            Change(db, clock, reference, ct, i =>
            {
                new Validator().Required("by", req.By, 100).Required("summary", req.Summary, 2000).ThrowIfInvalid();
                i.Resolve(req.Summary, req.By.Trim(), clock.GetUtcNow());
            }));

        g.MapPost("/{reference}/close", (string reference, ActorRequest req, TrackerDbContext db, TimeProvider clock, CancellationToken ct) =>
            Change(db, clock, reference, ct, i => { RequireActor(req.By); i.Close(req.By.Trim(), clock.GetUtcNow()); }));

        g.MapPost("/{reference}/reopen", (string reference, ReopenRequest req, TrackerDbContext db, TimeProvider clock, CancellationToken ct) =>
            Change(db, clock, reference, ct, i =>
            {
                new Validator().Required("by", req.By, 100).Required("reason", req.Reason, 1000).ThrowIfInvalid();
                i.Reopen(req.Reason, req.By.Trim(), clock.GetUtcNow());
            }));

        return g;
    }

    private static void RequireActor(string? by) => new Validator().Required("by", by, 100).ThrowIfInvalid();

    private static async Task<Incident> Load(TrackerDbContext db, string reference, CancellationToken ct, bool tracking = true)
    {
        var q = db.Incidents.Include(i => i.Events).Include(i => i.CausedByRelease).AsSplitQuery();
        if (!tracking) q = q.AsNoTracking();
        return await q.SingleOrDefaultAsync(i => i.Ref == reference.ToUpperInvariant(), ct)
               ?? throw new NotFoundException($"Incident {reference} not found");
    }

    private static async Task<IResult> Change(TrackerDbContext db, TimeProvider clock, string reference, CancellationToken ct, Action<Incident> change)
    {
        var incident = await Load(db, reference, ct);
        change(incident);
        await db.SaveChangesAsync(ct);   // RowVersion check: a concurrent edit raises DbUpdateConcurrencyException -> 409
        return Results.Ok(IncidentDto.From(incident, clock.GetUtcNow(), withEvents: true));
    }
}
