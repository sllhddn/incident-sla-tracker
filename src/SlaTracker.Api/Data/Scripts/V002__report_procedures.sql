-- SLA and change-failure reports as stored procedures: the aggregation runs next to the data,
-- and the query plans can be tuned (see docs/DESIGN.md) without redeploying the API.

CREATE OR ALTER PROCEDURE dbo.usp_SlaSummary
    @From datetimeoffset(3),
    @To   datetimeoffset(3),
    @Now  datetimeoffset(3)
AS
BEGIN
    SET NOCOUNT ON;

    WITH Scoped AS (
        SELECT i.Severity,
               CASE WHEN COALESCE(i.AcknowledgedAt, @Now) <= i.ResponseDueAt THEN 1 ELSE 0 END AS ResponseMet,
               CASE WHEN COALESCE(i.ResolvedAt,     @Now) <= i.ResolveDueAt  THEN 1 ELSE 0 END AS ResolveMet,
               CASE WHEN i.ResolvedAt IS NOT NULL
                    THEN DATEDIFF(MINUTE, i.OpenedAt, i.ResolvedAt) END AS ResolveMinutes,
               CASE WHEN i.Status IN ('Open', 'Acknowledged') THEN 1 ELSE 0 END AS IsOpen
          FROM dbo.Incident AS i
         WHERE i.OpenedAt >= @From
           AND i.OpenedAt <  @To
    ),
    P90 AS (
        SELECT DISTINCT Severity,
               PERCENTILE_CONT(0.9) WITHIN GROUP (ORDER BY ResolveMinutes) OVER (PARTITION BY Severity) AS P90ResolveMinutes
          FROM Scoped
         WHERE ResolveMinutes IS NOT NULL
    )
    SELECT s.Severity,
           COUNT(*)                                   AS Total,
           SUM(s.IsOpen)                              AS StillOpen,
           SUM(s.ResponseMet)                         AS ResponseMet,
           SUM(s.ResolveMet)                          AS ResolveMet,
           AVG(CAST(s.ResolveMinutes AS float))       AS AvgResolveMinutes,
           MAX(p.P90ResolveMinutes)                   AS P90ResolveMinutes
      FROM Scoped AS s
      LEFT JOIN P90 AS p ON p.Severity = s.Severity
     GROUP BY s.Severity
     ORDER BY s.Severity;
END;
GO

-- Change failure rate per month: deployments that were rolled back or caused at least one incident.
CREATE OR ALTER PROCEDURE dbo.usp_ChangeFailureRate
    @From datetimeoffset(3),
    @To   datetimeoffset(3)
AS
BEGIN
    SET NOCOUNT ON;

    WITH Failed AS (
        SELECT DISTINCT CausedByReleaseId AS ReleaseId
          FROM dbo.Incident
         WHERE CausedByReleaseId IS NOT NULL
    ),
    Deploys AS (
        SELECT CONVERT(char(7), r.DeployedAt, 126) AS [Month],
               CASE WHEN r.Status = 'RolledBack' OR f.ReleaseId IS NOT NULL THEN 1 ELSE 0 END AS IsFailure
          FROM dbo.Release AS r
          LEFT JOIN Failed AS f ON f.ReleaseId = r.Id
         WHERE r.Status IN ('Deployed', 'RolledBack')
           AND r.DeployedAt >= @From
           AND r.DeployedAt <  @To
    )
    SELECT [Month],
           COUNT(*)       AS Deployments,
           SUM(IsFailure) AS Failed
      FROM Deploys
     GROUP BY [Month]
     ORDER BY [Month];
END;
GO
