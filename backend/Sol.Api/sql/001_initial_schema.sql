SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- CelestialBodies table (full physical/orbital schema)
CREATE TABLE dbo.CelestialBodies (
  BodyId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CelestialBodies PRIMARY KEY,
  Slug NVARCHAR(64) NOT NULL,
  DisplayName NVARCHAR(128) NOT NULL,
  Category NVARCHAR(64) NULL,
  Kind NVARCHAR(64) NULL,
  ParentBodyId INT NULL,
  SortOrder INT NOT NULL CONSTRAINT DF_CelestialBodies_SortOrder DEFAULT (0),
  IsActive BIT NOT NULL CONSTRAINT DF_CelestialBodies_IsActive DEFAULT (1),
  MetadataJson NVARCHAR(MAX) NULL,
  JplId NVARCHAR(32) NULL,
  MinEpoch NVARCHAR(32) NULL,
  MaxEpoch NVARCHAR(32) NULL,
  Aphelion_AU FLOAT NULL,
  Perihelion_AU FLOAT NULL,
  Eccentricity FLOAT NULL,
  Inclination_deg FLOAT NULL,
  SemiMajorAxis_AU FLOAT NULL,
  ArgumentOfPerihelion_deg FLOAT NULL,
  LongitudeOfAscendingNode_deg FLOAT NULL,
  MeanAnomaly_deg FLOAT NULL,
  MeanMotion_degPerDay FLOAT NULL,
  OrbitalPeriod_days FLOAT NULL,
  Epoch_JD FLOAT NULL,
  MeanRadius_km FLOAT NULL,
  Density_gcm3 FLOAT NULL,
  Mass_1e23kg FLOAT NULL,
  Volume_1e10km3 FLOAT NULL,
  SiderealRotPeriod_d FLOAT NULL,
  SiderealRotRate_radps FLOAT NULL,
  MeanSolarDay_d FLOAT NULL,
  CoreRadius_km FLOAT NULL,
  GeometricAlbedo FLOAT NULL,
  SurfaceEmissivity FLOAT NULL,
  GM_km3s2 FLOAT NULL,
  EquatorialRadius_km FLOAT NULL,
  MassRatioSunPlanet FLOAT NULL,
  MomentOfInertia FLOAT NULL,
  EquatorialGravity_ms2 FLOAT NULL,
  AtmosPressure_bar FLOAT NULL,
  MaxAngularDiam_arcsec FLOAT NULL,
  MeanTemperature_K FLOAT NULL,
  VisualMag FLOAT NULL,
  ObliquityToOrbit_arcmin FLOAT NULL,
  HillSphereRadius_Rp FLOAT NULL,
  SiderealOrbPeriod_y FLOAT NULL,
  SiderealOrbPeriod_d FLOAT NULL,
  EscapeVelocity_kms FLOAT NULL,
  MeanOrbitVelocity_kms FLOAT NULL,
  SolarConstant_Wm2_Mean FLOAT NULL,
  SolarConstant_Wm2_Perihelion FLOAT NULL,
  SolarConstant_Wm2_Aphelion FLOAT NULL,
  MaxPlanetaryIR_Wm2_Mean FLOAT NULL,
  MaxPlanetaryIR_Wm2_Perihelion FLOAT NULL,
  MaxPlanetaryIR_Wm2_Aphelion FLOAT NULL,
  MinPlanetaryIR_Wm2_Mean FLOAT NULL,
  MinPlanetaryIR_Wm2_Perihelion FLOAT NULL,
  MinPlanetaryIR_Wm2_Aphelion FLOAT NULL,
  CreatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_CelestialBodies_CreatedUtc DEFAULT (SYSUTCDATETIME()),
  UpdatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_CelestialBodies_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
  CONSTRAINT UQ_CelestialBodies_Slug UNIQUE (Slug),
  CONSTRAINT FK_CelestialBodies_ParentBody FOREIGN KEY (ParentBodyId) REFERENCES dbo.CelestialBodies (BodyId)
);
GO

-- EphemerisSamples table
CREATE TABLE dbo.EphemerisSamples (
  EphemerisSampleId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EphemerisSamples PRIMARY KEY,
  BodyId INT NOT NULL,
  SampleTimeUtc DATETIME2(3) NOT NULL,
  X_AU FLOAT NOT NULL,
  Y_AU FLOAT NOT NULL,
  Z_AU FLOAT NOT NULL,
  VX_AUPerDay FLOAT NULL,
  VY_AUPerDay FLOAT NULL,
  VZ_AUPerDay FLOAT NULL,
  Frame NVARCHAR(64) NULL,
  Source NVARCHAR(64) NULL,
  CreatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_EphemerisSamples_CreatedUtc DEFAULT (SYSUTCDATETIME()),
  CONSTRAINT FK_EphemerisSamples_CelestialBodies FOREIGN KEY (BodyId) REFERENCES dbo.CelestialBodies (BodyId),
  CONSTRAINT UQ_EphemerisSamples_BodyTime UNIQUE (BodyId, SampleTimeUtc)
);
GO

-- Indexes
CREATE INDEX IX_EphemerisSamples_Body_Time
  ON dbo.EphemerisSamples (BodyId, SampleTimeUtc)
  INCLUDE (X_AU, Y_AU, Z_AU, VX_AUPerDay, VY_AUPerDay, VZ_AUPerDay, Frame, Source);
GO

CREATE INDEX IX_CelestialBodies_SortOrder
  ON dbo.CelestialBodies (SortOrder, DisplayName);
GO

-- Permissions
IF DATABASE_PRINCIPAL_ID(N'sol_reader') IS NOT NULL
BEGIN
  GRANT SELECT ON dbo.CelestialBodies TO sol_reader;
  GRANT SELECT ON dbo.EphemerisSamples TO sol_reader;
END;
GO

IF DATABASE_PRINCIPAL_ID(N'sol_user') IS NOT NULL
BEGIN
  GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.CelestialBodies TO sol_user;
  GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.EphemerisSamples TO sol_user;
END;
GO
