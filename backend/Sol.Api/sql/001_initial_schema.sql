SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================
-- dbo.Bodies
-- ============================================================
CREATE TABLE dbo.Bodies (
  BodyId               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Bodies PRIMARY KEY,
  Slug                 NVARCHAR(64)  NOT NULL,
  DisplayName          NVARCHAR(128) NOT NULL,
  Kind                 NVARCHAR(32)  NOT NULL,   -- star|planet|dwarf-planet|moon|asteroid|comet|comet-fragment|probe
  ParentBodyId         INT           NULL,
  SortOrder            INT           NOT NULL CONSTRAINT DF_Bodies_SortOrder            DEFAULT (0),
  IsActive             BIT           NOT NULL CONSTRAINT DF_Bodies_IsActive             DEFAULT (1),
  Source               NVARCHAR(32)  NULL,        -- horizons|mpcorb|manual
  JplHorizonsId        NVARCHAR(64)  NULL,        -- Horizons COMMAND string (e.g. "199", "DES=1P;CAP;NOFRAG")
  SbdbDesig            NVARCHAR(64)  NULL,        -- JPL SBDB designation
  H_AbsMag             FLOAT         NULL,        -- absolute magnitude H; used for cutoff filtering
  G_Slope              FLOAT         NULL,        -- phase slope G
  HasEphemeris         BIT           NOT NULL CONSTRAINT DF_Bodies_HasEphemeris         DEFAULT (0),
  CompletedEphemeris   BIT           NOT NULL CONSTRAINT DF_Bodies_CompletedEphemeris   DEFAULT (0),
  EphemerisMinJD       FLOAT         NULL,        -- Julian Day of earliest Horizons data for this body
  EphemerisMaxJD       FLOAT         NULL,        -- Julian Day of latest   Horizons data for this body
  EphemerisMinStr      NVARCHAR(48)  NULL,        -- human-readable (e.g. "BC 9999-Jan-01 12:00")
  EphemerisMaxStr      NVARCHAR(48)  NULL,
  -- Keplerian orbital elements (J2000 ecliptic, heliocentric unless body is a moon)
  Eccentricity         FLOAT         NULL,
  Perihelion_AU        FLOAT         NULL,
  Aphelion_AU          FLOAT         NULL,
  Inclination_deg      FLOAT         NULL,
  LongAscNode_deg      FLOAT         NULL,
  ArgPerihelion_deg    FLOAT         NULL,
  SemiMajorAxis_AU     FLOAT         NULL,
  MeanAnomaly_deg      FLOAT         NULL,
  MeanMotion_degPerDay FLOAT         NULL,
  OrbitalPeriod_days   FLOAT         NULL,
  Epoch_JD             FLOAT         NULL,        -- osculating epoch as Julian Day
  T_Perihelion_JD      FLOAT         NULL,        -- time of perihelion passage (comets)
  -- Simulation-critical physical properties
  GM_km3s2             FLOAT         NULL,
  MeanRadius_km        FLOAT         NULL,
  EquatorialRadius_km  FLOAT         NULL,
  Mass_1e23kg          FLOAT         NULL,
  -- Remaining physical properties (density, albedo, temperatures, etc.)
  PhysicsJson          NVARCHAR(MAX) NULL,
  CreatedUtc           DATETIME2(0)  NOT NULL CONSTRAINT DF_Bodies_CreatedUtc DEFAULT (SYSUTCDATETIME()),
  UpdatedUtc           DATETIME2(0)  NOT NULL CONSTRAINT DF_Bodies_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
  CONSTRAINT UQ_Bodies_Slug   UNIQUE (Slug),
  CONSTRAINT FK_Bodies_Parent FOREIGN KEY (ParentBodyId) REFERENCES dbo.Bodies (BodyId)
);
GO

CREATE INDEX IX_Bodies_SortOrder ON dbo.Bodies (SortOrder, DisplayName);
GO

CREATE INDEX IX_Bodies_Kind ON dbo.Bodies (Kind) INCLUDE (Slug, DisplayName, SortOrder);
GO

CREATE INDEX IX_Bodies_H ON dbo.Bodies (H_AbsMag) WHERE H_AbsMag IS NOT NULL;
GO

-- ============================================================
-- dbo.EphemerisSamples
-- SampleJd is a Julian Day Number (FLOAT) so BC dates are representable.
-- ============================================================
CREATE TABLE dbo.EphemerisSamples (
  EphemerisSampleId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EphemerisSamples PRIMARY KEY,
  BodyId            INT          NOT NULL,
  SampleJd          FLOAT        NOT NULL,
  X_AU              FLOAT        NOT NULL,
  Y_AU              FLOAT        NOT NULL,
  Z_AU              FLOAT        NOT NULL,
  VX_AUPerDay       FLOAT        NULL,
  VY_AUPerDay       FLOAT        NULL,
  VZ_AUPerDay       FLOAT        NULL,
  Frame             NVARCHAR(64) NULL,
  Source            NVARCHAR(64) NULL,
  CreatedUtc        DATETIME2(0) NOT NULL CONSTRAINT DF_EphemerisSamples_CreatedUtc DEFAULT (SYSUTCDATETIME()),
  CONSTRAINT FK_EphemerisSamples_Bodies FOREIGN KEY (BodyId) REFERENCES dbo.Bodies (BodyId),
  CONSTRAINT UQ_EphemerisSamples_BodyJd UNIQUE (BodyId, SampleJd)
);
GO

CREATE INDEX IX_EphemerisSamples_Body_Jd
  ON dbo.EphemerisSamples (BodyId, SampleJd)
  INCLUDE (X_AU, Y_AU, Z_AU, VX_AUPerDay, VY_AUPerDay, VZ_AUPerDay, Frame, Source);
GO

-- ============================================================
-- dbo.EphemerisImportLog
-- Tracks every chunk [StartJd, EndJd] attempted per body so
-- interrupted imports can resume without re-fetching data.
-- SampleCount = 0 means Horizons confirmed no data for that chunk.
-- ============================================================
CREATE TABLE dbo.EphemerisImportLog (
  BodyId      INT          NOT NULL,
  StartJd     FLOAT        NOT NULL,
  EndJd       FLOAT        NOT NULL,
  SampleCount INT          NOT NULL,
  ImportedUtc DATETIME2(3) NOT NULL CONSTRAINT DF_EphemerisImportLog_ImportedUtc DEFAULT SYSUTCDATETIME(),
  CONSTRAINT PK_EphemerisImportLog      PRIMARY KEY (BodyId, StartJd, EndJd),
  CONSTRAINT FK_EphemerisImportLog_Body FOREIGN KEY (BodyId) REFERENCES dbo.Bodies (BodyId)
);
GO

-- ============================================================
-- Permissions
-- ============================================================
IF DATABASE_PRINCIPAL_ID(N'sol_reader') IS NOT NULL
BEGIN
  GRANT SELECT ON dbo.Bodies             TO sol_reader;
  GRANT SELECT ON dbo.EphemerisSamples   TO sol_reader;
  GRANT SELECT ON dbo.EphemerisImportLog TO sol_reader;
END;
GO

IF DATABASE_PRINCIPAL_ID(N'sol_user') IS NOT NULL
BEGIN
  GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Bodies             TO sol_user;
  GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.EphemerisSamples   TO sol_user;
  GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.EphemerisImportLog TO sol_user;
END;
GO
