using Microsoft.EntityFrameworkCore;
using SlaTracker.Api.Data;
using SlaTracker.Api.Domain;

namespace SlaTracker.Api.Endpoints;

public static class ReleaseEndpoints
{
    public static RouteGroupBuilder MapReleases(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/releases").WithTags("Releases");

        g.MapGet("/", async (TrackerDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Releases.AsNoTracking().OrderByDescending(r => r.ScheduledAt).Take(200).ToListAsync(ct)).Select(ReleaseDto.From)));

        g.MapPost("/", async (CreateReleaseRequest req, TrackerDbContext db, CancellationToken ct) =>
        {
            new Validator()
                .Required("version", req.Version, 30)
                .Check(req.Version is null || System.Text.RegularExpressions.Regex.IsMatch(req.Version.Trim(), @"^\d+\.\d+\.\d+([-.][A-Za-z0-9]+)?$"),
                       "version", "must look like 1.4.2")
                .Optional("notes", req.Notes, 2000)
                .ThrowIfInvalid();
            if (await db.Releases.AnyAsync(r => r.Version == req.Version.Trim(), ct))
                throw new DomainException($"Release {req.Version} already exists.");
            var r = new Release(req.Version, req.ScheduledAt, req.Notes);
            db.Releases.Add(r);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/releases/{r.Version}", ReleaseDto.From(r));
        });

        g.MapPost("/{version}/deploy", async (string version, TrackerDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var r = await Find(db, version, ct);
            r.MarkDeployed(clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return Results.Ok(ReleaseDto.From(r));
        });

        g.MapPost("/{version}/rollback", async (string version, RollbackRequest req, TrackerDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            new Validator().Required("reason", req.Reason, 500).ThrowIfInvalid();
            var r = await Find(db, version, ct);
            r.RollBack(req.Reason, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return Results.Ok(ReleaseDto.From(r));
        });

        return g;
    }

    private static async Task<Release> Find(TrackerDbContext db, string version, CancellationToken ct) =>
        await db.Releases.SingleOrDefaultAsync(r => r.Version == version, ct) ?? throw new NotFoundException($"Release {version} not found");
}
