SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Drop old structure (dependent table first)
IF OBJECT_ID('dbo.EphemerisSamples', 'U') IS NOT NULL DROP TABLE dbo.EphemerisSamples;
GO
IF OBJECT_ID('dbo.CelestialBodies',  'U') IS NOT NULL DROP TABLE dbo.CelestialBodies;
GO

-- Bodies: replaces CelestialBodies with a cleaner, simulation-focused schema.
-- Physical properties that are rarely needed for simulation are serialised to PhysicsJson.
-- Ephemeris date ranges are stored as Julian Day numbers (FLOAT) so BC dates are representable,
-- plus a human-readable string copy for display.
CREATE TABLE dbo.Bodies (
  BodyId              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Bodies PRIMARY KEY,
  Slug                NVARCHAR(64)  NOT NULL,
  DisplayName         NVARCHAR(128) NOT NULL,
  Kind                NVARCHAR(32)  NOT NULL,   -- star|planet|dwarf-planet|moon|asteroid|comet|comet-fragment|probe
  ParentBodyId        INT           NULL,
  SortOrder           INT           NOT NULL CONSTRAINT DF_Bodies_SortOrder   DEFAULT (0),
  IsActive            BIT           NOT NULL CONSTRAINT DF_Bodies_IsActive    DEFAULT (1),
  Source              NVARCHAR(32)  NULL,        -- horizons|sbdb|mpc-orbit|mpc-comet|jpl-sat|manual
  JplHorizonsId       NVARCHAR(64)  NULL,        -- Horizons COMMAND string (e.g. "199", "DES=1P;CAP;NOFRAG")
  SbdbDesig           NVARCHAR(64)  NULL,        -- JPL SBDB designation (e.g. "1P", "134340")
  H_AbsMag            FLOAT         NULL,        -- absolute magnitude H (MPC/SBDB); used for cutoff filtering
  G_Slope             FLOAT         NULL,        -- phase slope G
  HasEphemeris        BIT           NOT NULL CONSTRAINT DF_Bodies_HasEphemeris DEFAULT (0),
  EphemerisMinJD      FLOAT         NULL,        -- Julian Day of earliest available ephemeris sample
  EphemerisMaxJD      FLOAT         NULL,        -- Julian Day of latest  available ephemeris sample
  EphemerisMinStr     NVARCHAR(48)  NULL,        -- human-readable form (e.g. "BC 9999-Jan-01 12:00")
  EphemerisMaxStr     NVARCHAR(48)  NULL,
  -- Keplerian orbital elements (J2000 ecliptic, heliocentric unless body is a moon)
  Eccentricity        FLOAT         NULL,
  Perihelion_AU       FLOAT         NULL,
  Aphelion_AU         FLOAT         NULL,
  Inclination_deg     FLOAT         NULL,
  LongAscNode_deg     FLOAT         NULL,
  ArgPerihelion_deg   FLOAT         NULL,
  SemiMajorAxis_AU    FLOAT         NULL,
  MeanAnomaly_deg     FLOAT         NULL,
  MeanMotion_degPerDay FLOAT        NULL,
  OrbitalPeriod_days  FLOAT         NULL,
  Epoch_JD            FLOAT         NULL,        -- osculating epoch as Julian Day
  T_Perihelion_JD     FLOAT         NULL,        -- time of perihelion passage (comets)
  -- Simulation-critical physical properties
  GM_km3s2            FLOAT         NULL,
  MeanRadius_km       FLOAT         NULL,
  EquatorialRadius_km FLOAT         NULL,
  Mass_1e23kg         FLOAT         NULL,
  -- Remaining physical properties (density, albedo, temperatures, solar constants, etc.)
  PhysicsJson         NVARCHAR(MAX) NULL,
  CreatedUtc          DATETIME2(0)  NOT NULL CONSTRAINT DF_Bodies_CreatedUtc DEFAULT (SYSUTCDATETIME()),
  UpdatedUtc          DATETIME2(0)  NOT NULL CONSTRAINT DF_Bodies_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
  CONSTRAINT UQ_Bodies_Slug   UNIQUE (Slug),
  CONSTRAINT FK_Bodies_Parent FOREIGN KEY (ParentBodyId) REFERENCES dbo.Bodies (BodyId)
);
GO

CREATE TABLE dbo.EphemerisSamples (
  EphemerisSampleId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EphemerisSamples PRIMARY KEY,
  BodyId            INT          NOT NULL,
  SampleTimeUtc     DATETIME2(3) NOT NULL,
  X_AU              FLOAT        NOT NULL,
  Y_AU              FLOAT        NOT NULL,
  Z_AU              FLOAT        NOT NULL,
  VX_AUPerDay       FLOAT        NULL,
  VY_AUPerDay       FLOAT        NULL,
  VZ_AUPerDay       FLOAT        NULL,
  Frame             NVARCHAR(64) NULL,
  Source            NVARCHAR(64) NULL,
  CreatedUtc        DATETIME2(0) NOT NULL CONSTRAINT DF_EphemerisSamples_CreatedUtc DEFAULT (SYSUTCDATETIME()),
  CONSTRAINT FK_EphemerisSamples_Bodies   FOREIGN KEY (BodyId) REFERENCES dbo.Bodies (BodyId),
  CONSTRAINT UQ_EphemerisSamples_BodyTime UNIQUE (BodyId, SampleTimeUtc)
);
GO

CREATE INDEX IX_EphemerisSamples_Body_Time
  ON dbo.EphemerisSamples (BodyId, SampleTimeUtc)
  INCLUDE (X_AU, Y_AU, Z_AU, VX_AUPerDay, VY_AUPerDay, VZ_AUPerDay, Frame, Source);
GO

CREATE INDEX IX_Bodies_SortOrder ON dbo.Bodies (SortOrder, DisplayName);
GO

CREATE INDEX IX_Bodies_Kind ON dbo.Bodies (Kind) INCLUDE (Slug, DisplayName, SortOrder);
GO

CREATE INDEX IX_Bodies_H ON dbo.Bodies (H_AbsMag) WHERE H_AbsMag IS NOT NULL;
GO

IF DATABASE_PRINCIPAL_ID(N'sol_reader') IS NOT NULL
BEGIN
  GRANT SELECT ON dbo.Bodies          TO sol_reader;
  GRANT SELECT ON dbo.EphemerisSamples TO sol_reader;
END;
GO

IF DATABASE_PRINCIPAL_ID(N'sol_user') IS NOT NULL
BEGIN
  GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Bodies          TO sol_user;
  GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.EphemerisSamples TO sol_user;
END;
GO
