namespace SlaTracker.Api.Domain;

public enum Severity { P1, P2, P3, P4 }

public enum IncidentStatus { Open, Acknowledged, Resolved, Closed }

public enum ReleaseStatus { Planned, Deployed, RolledBack }

/// <summary>Raised when a request breaks a business rule; the API maps it to 409 Conflict.</summary>
public sealed class DomainException(string message) : Exception(message);

/// <summary>Raised when an id or reference does not exist; the API maps it to 404.</summary>
public sealed class NotFoundException(string message) : Exception(message);
