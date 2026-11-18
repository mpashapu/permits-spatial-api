-- Permits schema for SQL Server 2019+ / Azure SQL.
-- Idempotent: safe to run on every startup. Batches are separated by GO.

IF OBJECT_ID(N'dbo.Permits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Permits
    (
        Id                    INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Permits PRIMARY KEY CLUSTERED,
        PermitNumber          NVARCHAR(50)   NOT NULL,
        Address               NVARCHAR(200)  NULL,
        Folio                 NVARCHAR(20)   NULL,
        PermitType            NVARCHAR(10)   NULL,
        Description           NVARCHAR(400)  NULL,
        ResidentialCommercial NVARCHAR(10)   NULL,
        Status                NVARCHAR(20)   NULL,
        IssuedDate            DATE           NULL,
        EstimatedValue        DECIMAL(14, 2) NULL,
        ContractorName        NVARCHAR(200)  NULL,
        -- geography (not geometry): distances come back in meters on the WGS84 ellipsoid
        Location              GEOGRAPHY      NOT NULL,
        SourceUpdatedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_Permits_SourceUpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_Permits_PermitNumber UNIQUE (PermitNumber)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'SIX_Permits_Location' AND object_id = OBJECT_ID(N'dbo.Permits'))
    CREATE SPATIAL INDEX SIX_Permits_Location
        ON dbo.Permits (Location)
        USING GEOGRAPHY_AUTO_GRID;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Permits_Type_IssuedDate' AND object_id = OBJECT_ID(N'dbo.Permits'))
    CREATE INDEX IX_Permits_Type_IssuedDate
        ON dbo.Permits (PermitType, IssuedDate)
        INCLUDE (EstimatedValue);
GO

-- Bulk-loaded by the importer, then merged into dbo.Permits in one transaction.
-- Holds plain longitude/latitude so SqlBulkCopy doesn't need SQL Server spatial types.
IF OBJECT_ID(N'dbo.PermitsStaging', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PermitsStaging
    (
        PermitNumber          NVARCHAR(50)   NOT NULL,
        Address               NVARCHAR(200)  NULL,
        Folio                 NVARCHAR(20)   NULL,
        PermitType            NVARCHAR(10)   NULL,
        Description           NVARCHAR(400)  NULL,
        ResidentialCommercial NVARCHAR(10)   NULL,
        Status                NVARCHAR(20)   NULL,
        IssuedDate            DATE           NULL,
        EstimatedValue        DECIMAL(14, 2) NULL,
        ContractorName        NVARCHAR(200)  NULL,
        Longitude             FLOAT          NOT NULL,
        Latitude              FLOAT          NOT NULL
    );
END
GO
