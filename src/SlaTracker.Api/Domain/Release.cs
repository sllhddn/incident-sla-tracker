namespace SlaTracker.Api.Domain;

/// <summary>A production release. Incidents can point at the release that caused them (change failure rate).</summary>
public class Release
{
    public long Id { get; private set; }
    public string Version { get; private set; } = "";
    public ReleaseStatus Status { get; private set; }
    public DateTimeOffset ScheduledAt { get; private set; }
    public DateTimeOffset? DeployedAt { get; private set; }
    public string? Notes { get; private set; }

    private Release() { }

    public Release(string version, DateTimeOffset scheduledAt, string? notes)
    {
        Version = version.Trim();
        ScheduledAt = scheduledAt;
        Status = ReleaseStatus.Planned;
        Notes = notes;
    }

    public void MarkDeployed(DateTimeOffset now)
    {
        if (Status != ReleaseStatus.Planned) throw new DomainException($"Release {Version} is already {Status}.");
        Status = ReleaseStatus.Deployed;
        DeployedAt = now;
    }

    public void RollBack(string reason, DateTimeOffset now)
    {
        if (Status != ReleaseStatus.Deployed) throw new DomainException($"Only a deployed release can be rolled back; {Version} is {Status}.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A rollback reason is required.");
        Status = ReleaseStatus.RolledBack;
        Notes = string.IsNullOrEmpty(Notes) ? $"Rolled back {now:yyyy-MM-dd HH:mm}: {reason}" : $"{Notes}\nRolled back {now:yyyy-MM-dd HH:mm}: {reason}";
    }
}
