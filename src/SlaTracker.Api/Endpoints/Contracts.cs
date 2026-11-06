using SlaTracker.Api.Domain;

namespace SlaTracker.Api.Endpoints;

public sealed record OpenIncidentRequest(string Title, string Service, Severity Severity, string OpenedBy, string? CausedByReleaseVersion);
public sealed record ActorRequest(string By);
public sealed record ResolveRequest(string By, string Summary);
public sealed record ReopenRequest(string By, string Reason);
public sealed record CreateReleaseRequest(string Version, DateTimeOffset ScheduledAt, string? Notes);
public sealed record RollbackRequest(string Reason);

public sealed record IncidentEventDto(DateTimeOffset At, string Kind, string Actor, string? Detail);

public sealed record IncidentDto(
    string Ref, string Title, string Service, Severity Severity, IncidentStatus Status, string? Assignee,
    DateTimeOffset OpenedAt, DateTimeOffset ResponseDueAt, DateTimeOffset ResolveDueAt,
    DateTimeOffset? AcknowledgedAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt,
    string? ResolutionSummary, string? CausedByRelease,
    bool ResponseBreached, bool ResolveBreached, int MinutesToResolveDue,
    IReadOnlyList<IncidentEventDto>? Events)
{
    public static IncidentDto From(Incident i, DateTimeOffset now, bool withEvents = false) => new(
        i.Ref, i.Title, i.Service, i.Severity, i.Status, i.Assignee,
        i.OpenedAt, i.ResponseDueAt, i.ResolveDueAt, i.AcknowledgedAt, i.ResolvedAt, i.ClosedAt,
        i.ResolutionSummary, i.CausedByRelease?.Version,
        i.ResponseBreached(now), i.ResolveBreached(now),
        (int)Math.Round((i.ResolveDueAt - (i.ResolvedAt ?? now)).TotalMinutes),
        withEvents ? i.Events.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new IncidentEventDto(e.At, e.Kind, e.Actor, e.Detail)).ToList() : null);
}

public sealed record ReleaseDto(string Version, ReleaseStatus Status, DateTimeOffset ScheduledAt, DateTimeOffset? DeployedAt, string? Notes)
{
    public static ReleaseDto From(Release r) => new(r.Version, r.Status, r.ScheduledAt, r.DeployedAt, r.Notes);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

/// <summary>Row shape returned by dbo.usp_SlaSummary.</summary>
public sealed class SlaSummaryRow
{
    public string Severity { get; set; } = "";
    public int Total { get; set; }
    public int StillOpen { get; set; }
    public int ResponseMet { get; set; }
    public int ResolveMet { get; set; }
    public double? AvgResolveMinutes { get; set; }
    public double? P90ResolveMinutes { get; set; }
}

public sealed record SlaSummaryDto(string Severity, int Total, int StillOpen, double ResponseCompliancePct, double ResolveCompliancePct,
                                   double? AvgResolveMinutes, double? P90ResolveMinutes);

/// <summary>Row shape returned by dbo.usp_ChangeFailureRate.</summary>
public sealed class ChangeFailureRow
{
    public string Month { get; set; } = "";
    public int Deployments { get; set; }
    public int Failed { get; set; }
}

public sealed record ChangeFailureDto(string Month, int Deployments, int Failed, double ChangeFailureRatePct);
