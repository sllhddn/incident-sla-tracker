namespace SlaTracker.Api.Domain;

/// <summary>
/// An incident and its SLA clock. State changes only through the methods below, so every caller
/// (API, batch job, admin tool) follows the same workflow and leaves the same audit trail.
/// </summary>
public class Incident
{
    public long Id { get; private set; }
    public string Ref { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Service { get; private set; } = "";
    public Severity Severity { get; private set; }
    public IncidentStatus Status { get; private set; }
    public string? Assignee { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset ResponseDueAt { get; private set; }
    public DateTimeOffset ResolveDueAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public string? ResolutionSummary { get; private set; }
    public long? CausedByReleaseId { get; private set; }
    public Release? CausedByRelease { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public List<IncidentEvent> Events { get; private set; } = [];

    private Incident() { }

    public static Incident Open(string reference, string title, string service, SlaPolicy policy,
                                DateTimeOffset now, string openedBy, Release? causedBy = null)
    {
        var i = new Incident
        {
            Ref = reference,
            Title = title.Trim(),
            Service = service.Trim(),
            Severity = policy.Severity,
            Status = IncidentStatus.Open,
            OpenedAt = now,
            ResponseDueAt = now.AddMinutes(policy.ResponseMinutes),
            ResolveDueAt = now.AddMinutes(policy.ResolveMinutes),
            CausedByRelease = causedBy,
        };
        i.Record(now, "OPENED", openedBy, $"{policy.Severity} on {i.Service}");
        return i;
    }

    public void Acknowledge(string by, DateTimeOffset now)
    {
        Require(IncidentStatus.Open, "acknowledge");
        Status = IncidentStatus.Acknowledged;
        AcknowledgedAt = now;
        Assignee = by;
        Record(now, "ACKNOWLEDGED", by, null);
    }

    public void Resolve(string summary, string by, DateTimeOffset now)
    {
        if (Status is not (IncidentStatus.Open or IncidentStatus.Acknowledged))
            throw new DomainException($"Cannot resolve an incident that is {Status}.");
        if (string.IsNullOrWhiteSpace(summary)) throw new DomainException("A resolution summary is required.");
        AcknowledgedAt ??= now;          // resolving without an explicit acknowledge counts as the response
        Assignee ??= by;
        Status = IncidentStatus.Resolved;
        ResolvedAt = now;
        ResolutionSummary = summary.Trim();
        Record(now, "RESOLVED", by, ResolutionSummary);
    }

    public void Close(string by, DateTimeOffset now)
    {
        Require(IncidentStatus.Resolved, "close");
        Status = IncidentStatus.Closed;
        ClosedAt = now;
        Record(now, "CLOSED", by, null);
    }

    /// <summary>Reopens a resolved incident. The original deadlines stay: a reopen does not reset the SLA clock.</summary>
    public void Reopen(string reason, string by, DateTimeOffset now)
    {
        Require(IncidentStatus.Resolved, "reopen");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to reopen.");
        Status = IncidentStatus.Acknowledged;
        ResolvedAt = null;
        ResolutionSummary = null;
        Record(now, "REOPENED", by, reason.Trim());
    }

    public bool ResponseBreached(DateTimeOffset now) => (AcknowledgedAt ?? now) > ResponseDueAt;
    public bool ResolveBreached(DateTimeOffset now) => (ResolvedAt ?? now) > ResolveDueAt;

    private void Require(IncidentStatus expected, string action)
    {
        if (Status != expected)
            throw new DomainException($"Cannot {action} an incident that is {Status}; it must be {expected}.");
    }

    private void Record(DateTimeOffset at, string kind, string actor, string? detail) =>
        Events.Add(new IncidentEvent(at, kind, actor, detail));
}

public class IncidentEvent
{
    public long Id { get; private set; }
    public long IncidentId { get; private set; }
    public DateTimeOffset At { get; private set; }
    public string Kind { get; private set; } = "";
    public string Actor { get; private set; } = "";
    public string? Detail { get; private set; }

    private IncidentEvent() { }

    public IncidentEvent(DateTimeOffset at, string kind, string actor, string? detail)
    {
        At = at;
        Kind = kind;
        Actor = actor;
        Detail = detail is { Length: > 1000 } ? detail[..1000] : detail;
    }
}
