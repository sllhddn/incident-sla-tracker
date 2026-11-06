-- incident-sla-tracker schema v1 (SQL Server 2019+)
CREATE TABLE dbo.SlaPolicy (
    Severity         char(2)  NOT NULL CONSTRAINT PK_SlaPolicy PRIMARY KEY,
    ResponseMinutes  int      NOT NULL,
    ResolveMinutes   int      NOT NULL,
    CONSTRAINT CK_SlaPolicy_Severity CHECK (Severity IN ('P1', 'P2', 'P3', 'P4')),
    CONSTRAINT CK_SlaPolicy_Minutes  CHECK (ResponseMinutes > 0 AND ResolveMinutes >= ResponseMinutes)
);
GO

INSERT INTO dbo.SlaPolicy (Severity, ResponseMinutes, ResolveMinutes) VALUES
    ('P1',   15,  240),   -- 15 min response, 4 h resolve
    ('P2',   30,  480),   -- 30 min, 8 h
    ('P3',  240, 2880),   -- 4 h, 2 days
    ('P4', 1440, 7200);   -- 1 day, 5 days
GO

CREATE TABLE dbo.Release (
    Id           bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Release PRIMARY KEY,
    Version      varchar(30)     NOT NULL,
    Status       varchar(12)     NOT NULL,
    ScheduledAt  datetimeoffset(3) NOT NULL,
    DeployedAt   datetimeoffset(3) NULL,
    Notes        nvarchar(2000)  NULL,
    CONSTRAINT UK_Release_Version UNIQUE (Version),
    CONSTRAINT CK_Release_Status  CHECK (Status IN ('Planned', 'Deployed', 'RolledBack'))
);
GO

-- Human-readable incident numbers (INC-1000, INC-1001 ...) come from a sequence, not from IDENTITY,
-- so the number is known before the row is inserted and never depends on insert order.
CREATE SEQUENCE dbo.SeqIncidentRef AS bigint START WITH 1000 INCREMENT BY 1 CACHE 20;
GO

CREATE TABLE dbo.Incident (
    Id                 bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Incident PRIMARY KEY,
    Ref                varchar(20)       NOT NULL,
    Title              nvarchar(200)     NOT NULL,
    Service            nvarchar(100)     NOT NULL,
    Severity           char(2)           NOT NULL CONSTRAINT FK_Incident_SlaPolicy REFERENCES dbo.SlaPolicy (Severity),
    Status             varchar(12)       NOT NULL,
    Assignee           nvarchar(100)     NULL,
    OpenedAt           datetimeoffset(3) NOT NULL,
    ResponseDueAt      datetimeoffset(3) NOT NULL,
    ResolveDueAt       datetimeoffset(3) NOT NULL,
    AcknowledgedAt     datetimeoffset(3) NULL,
    ResolvedAt         datetimeoffset(3) NULL,
    ClosedAt           datetimeoffset(3) NULL,
    ResolutionSummary  nvarchar(2000)    NULL,
    CausedByReleaseId  bigint            NULL CONSTRAINT FK_Incident_Release REFERENCES dbo.Release (Id),
    RowVersion         rowversion        NOT NULL,
    CONSTRAINT UK_Incident_Ref    UNIQUE (Ref),
    CONSTRAINT CK_Incident_Status CHECK (Status IN ('Open', 'Acknowledged', 'Resolved', 'Closed')),
    CONSTRAINT CK_Incident_Dates  CHECK (ResolvedAt IS NULL OR ResolvedAt >= OpenedAt)
);
GO

-- List screens filter by status and severity, newest first.
CREATE INDEX IX_Incident_Status_Severity ON dbo.Incident (Status, Severity, OpenedAt DESC)
    INCLUDE (Ref, Title, Service, ResolveDueAt);
GO

-- "Which open incidents breach next?" Filtered index: only unresolved rows, so it stays small.
CREATE INDEX IX_Incident_Open_ResolveDue ON dbo.Incident (ResolveDueAt)
    INCLUDE (Ref, Severity, Status)
    WHERE Status IN ('Open', 'Acknowledged');
GO

-- Reports scan by the date an incident was opened.
CREATE INDEX IX_Incident_OpenedAt ON dbo.Incident (OpenedAt) INCLUDE (Severity, AcknowledgedAt, ResolvedAt, ResponseDueAt, ResolveDueAt);
GO

CREATE INDEX IX_Incident_CausedByRelease ON dbo.Incident (CausedByReleaseId) WHERE CausedByReleaseId IS NOT NULL;
GO

CREATE TABLE dbo.IncidentEvent (
    Id          bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_IncidentEvent PRIMARY KEY,
    IncidentId  bigint            NOT NULL CONSTRAINT FK_IncidentEvent_Incident REFERENCES dbo.Incident (Id) ON DELETE CASCADE,
    At          datetimeoffset(3) NOT NULL,
    Kind        varchar(20)       NOT NULL,
    Actor       nvarchar(100)     NOT NULL,
    Detail      nvarchar(1000)    NULL
);
GO

CREATE INDEX IX_IncidentEvent_Incident ON dbo.IncidentEvent (IncidentId, At);
GO
