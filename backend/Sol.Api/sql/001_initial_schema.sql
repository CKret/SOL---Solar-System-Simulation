SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.CelestialBodies', N'U') IS NULL
BEGIN
  CREATE TABLE dbo.CelestialBodies (
    BodyId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CelestialBodies PRIMARY KEY,
    Slug NVARCHAR(64) NOT NULL,
    DisplayName NVARCHAR(128) NOT NULL,
    Category NVARCHAR(64) NULL,
    ParentBodyId INT NULL,
    SortOrder INT NOT NULL CONSTRAINT DF_CelestialBodies_SortOrder DEFAULT (0),
    IsActive BIT NOT NULL CONSTRAINT DF_CelestialBodies_IsActive DEFAULT (1),
    MetadataJson NVARCHAR(MAX) NULL,
    CreatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_CelestialBodies_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_CelestialBodies_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_CelestialBodies_Slug UNIQUE (Slug),
    CONSTRAINT FK_CelestialBodies_ParentBody FOREIGN KEY (ParentBodyId) REFERENCES dbo.CelestialBodies (BodyId)
  );
END;
GO

IF OBJECT_ID(N'dbo.EphemerisSamples', N'U') IS NULL
BEGIN
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
END;
GO

IF NOT EXISTS (
  SELECT 1
  FROM sys.indexes
  WHERE name = N'IX_EphemerisSamples_Body_Time'
    AND object_id = OBJECT_ID(N'dbo.EphemerisSamples', N'U')
)
BEGIN
  CREATE INDEX IX_EphemerisSamples_Body_Time
    ON dbo.EphemerisSamples (BodyId, SampleTimeUtc)
    INCLUDE (X_AU, Y_AU, Z_AU, VX_AUPerDay, VY_AUPerDay, VZ_AUPerDay, Frame, Source);
END;
GO

IF NOT EXISTS (
  SELECT 1
  FROM sys.indexes
  WHERE name = N'IX_CelestialBodies_SortOrder'
    AND object_id = OBJECT_ID(N'dbo.CelestialBodies', N'U')
)
BEGIN
  CREATE INDEX IX_CelestialBodies_SortOrder
    ON dbo.CelestialBodies (SortOrder, DisplayName);
END;
GO

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