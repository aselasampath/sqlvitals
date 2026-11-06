-- The database the integration tests read (#46), made afresh on each run. Run from master,
-- batch by batch (GO). Each object is here so that some query has a row to map:
--   Orders.Notes is in no index, so GetCustomerOrders does key lookups;
--   IX_Orders_Unused is written to and never read; nothing indexes Amount (a missing index);
--   Events is keyed on NEWID(), so its clustered index is fragmented;
--   FindOrdersByCode compares the varchar column with an nvarchar (an implicit conversion);
--   the orphaned user's login is dropped; Held, LockA and LockB are for blocking and a deadlock.

IF DB_ID(N'SqlVitalsITOther') IS NOT NULL
    DROP DATABASE SqlVitalsITOther;
IF DB_ID(N'SqlVitalsITOffline') IS NOT NULL
    DROP DATABASE SqlVitalsITOffline;
GO
IF DB_ID(N'SqlVitalsIT') IS NOT NULL
BEGIN
    ALTER DATABASE SqlVitalsIT SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SqlVitalsIT;
END
GO
CREATE DATABASE SqlVitalsIT;
GO
ALTER DATABASE SqlVitalsIT SET RECOVERY FULL;
ALTER DATABASE SqlVitalsIT SET QUERY_STORE = ON;
ALTER DATABASE SqlVitalsIT SET QUERY_STORE (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL, INTERVAL_LENGTH_MINUTES = 1);
GO
USE SqlVitalsIT;
GO
CREATE TABLE dbo.Customers
(
    CustomerId int IDENTITY CONSTRAINT PK_Customers PRIMARY KEY,
    Code       varchar(20)   NOT NULL,
    Name       nvarchar(100) NOT NULL,
    Region     char(2)       NOT NULL
);
CREATE TABLE dbo.Orders
(
    OrderId      int IDENTITY CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId   int            NOT NULL,
    CustomerCode varchar(20)    NOT NULL,
    Amount       decimal(12, 2) NOT NULL,
    CreatedAt    datetime2      NOT NULL,
    Notes        char(200)      NOT NULL
);
CREATE INDEX IX_Orders_CustomerCode ON dbo.Orders (CustomerCode);
CREATE INDEX IX_Orders_Unused ON dbo.Orders (CreatedAt, Notes);
CREATE TABLE dbo.Events
(
    EventId uniqueidentifier NOT NULL CONSTRAINT DF_Events_EventId DEFAULT NEWID() CONSTRAINT PK_Events PRIMARY KEY,
    Payload char(400)        NOT NULL CONSTRAINT DF_Events_Payload DEFAULT 'event'
);
CREATE TABLE dbo.Held  (Id int CONSTRAINT PK_Held  PRIMARY KEY, V int NOT NULL);
CREATE TABLE dbo.LockA (Id int CONSTRAINT PK_LockA PRIMARY KEY, V int NOT NULL);
CREATE TABLE dbo.LockB (Id int CONSTRAINT PK_LockB PRIMARY KEY, V int NOT NULL);
GO
WITH n AS (SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
           FROM sys.all_columns a CROSS JOIN sys.all_columns b)
INSERT dbo.Customers (Code, Name, Region)
SELECT 'C' + RIGHT('0000' + CAST(N AS varchar(10)), 5), N'Customer ' + CAST(N AS nvarchar(10)),
       CASE N % 3 WHEN 0 THEN 'EU' WHEN 1 THEN 'US' ELSE 'AP' END
FROM n WHERE N <= 500;

WITH n AS (SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
           FROM sys.all_columns a CROSS JOIN sys.all_columns b)
INSERT dbo.Orders (CustomerId, CustomerCode, Amount, CreatedAt, Notes)
SELECT N % 500 + 1, 'C' + RIGHT('0000' + CAST(N % 500 + 1 AS varchar(10)), 5),
       N % 1000, DATEADD(MINUTE, -N, SYSDATETIME()), 'order ' + CAST(N AS varchar(10))
FROM n;

INSERT dbo.Held  VALUES (1, 0);
INSERT dbo.LockA VALUES (1, 0);
INSERT dbo.LockB VALUES (1, 0);
GO
-- One row at a time, so each NEWID() lands mid-index and splits pages.
SET NOCOUNT ON;
DECLARE @i int = 0;
WHILE @i < 3000
BEGIN
    INSERT dbo.Events DEFAULT VALUES;
    SET @i += 1;
END
GO
CREATE STATISTICS ST_Orders_Amount ON dbo.Orders (Amount);
GO
-- Changes since the statistics were made: modification_counter > 0.
UPDATE dbo.Orders SET Amount = Amount + 1 WHERE OrderId % 5 = 0;
GO
CREATE PROCEDURE dbo.GetCustomerOrders @Code varchar(20)
AS
SELECT o.OrderId, o.Amount, o.Notes FROM dbo.Orders o WHERE o.CustomerCode = @Code;
GO
CREATE PROCEDURE dbo.FindOrdersByCode @Code nvarchar(20)
AS
SELECT COUNT(*) AS Orders FROM dbo.Orders WHERE CustomerCode = @Code;
GO
-- A join, so the plan is fully optimized: a trivial plan records no missing index.
CREATE PROCEDURE dbo.OrdersOfAmount @Amount decimal(12, 2)
AS
SELECT o.OrderId, o.CreatedAt, c.Name
FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId
WHERE o.Amount = @Amount;
GO
-- SP Trace's Called by and Level (#238): NestOuter calls NestMiddle twice, which calls NestInner.
CREATE PROCEDURE dbo.NestInner
AS
WAITFOR DELAY '00:00:00.020';
GO
CREATE PROCEDURE dbo.NestMiddle
AS
BEGIN
    EXEC dbo.NestInner;
    WAITFOR DELAY '00:00:00.010';
END
GO
CREATE PROCEDURE dbo.NestOuter
AS
BEGIN
    EXEC dbo.NestMiddle;
    EXEC dbo.NestMiddle;
END
GO
EXEC sp_create_plan_guide
    @name = N'PG_Orders_MaxDop1',
    @stmt = N'SELECT COUNT(*) FROM dbo.Orders WHERE Amount > 100',
    @type = N'SQL', @module_or_batch = NULL, @params = NULL,
    @hints = N'OPTION (MAXDOP 1)';
GO
USE master;
GO
IF SUSER_ID(N'SqlVitalsIT_Orphan') IS NULL
    CREATE LOGIN SqlVitalsIT_Orphan WITH PASSWORD = N'Orphan-Login-1!', CHECK_POLICY = OFF;
GO
USE SqlVitalsIT;
CREATE USER SqlVitalsIT_Orphan FOR LOGIN SqlVitalsIT_Orphan;
GO
USE master;
DROP LOGIN SqlVitalsIT_Orphan;
GO
-- A file name without a path goes to the server's default backup folder, on Windows or Linux.
BACKUP DATABASE SqlVitalsIT TO DISK = N'SqlVitalsIT_full.bak' WITH INIT, CHECKSUM;
BACKUP DATABASE SqlVitalsIT TO DISK = N'SqlVitalsIT_diff.bak' WITH DIFFERENTIAL, INIT;
BACKUP LOG SqlVitalsIT TO DISK = N'SqlVitalsIT_log.trn' WITH INIT;
GO
GO
-- Table Statistics (#242): one table of each shape, to compare with sp_spaceused, and one to lock.
USE SqlVitalsIT;
GO
CREATE SCHEMA sizes;
GO
CREATE TABLE sizes.HeapTable (Id int NOT NULL, Filler char(500) NOT NULL);
CREATE INDEX IX_HeapTable_Id ON sizes.HeapTable (Id);

-- Its nvarchar(max) goes off the row as LOB; its two varchar(5000) can't share a row: row-overflow.
CREATE TABLE sizes.ClusteredTable
(
    Id int NOT NULL CONSTRAINT PK_ClusteredTable PRIMARY KEY,
    Doc nvarchar(max) NOT NULL,
    A varchar(5000) NOT NULL,
    B varchar(5000) NOT NULL
);
CREATE INDEX IX_ClusteredTable_A ON sizes.ClusteredTable (Id) INCLUDE (A);

CREATE PARTITION FUNCTION PF_Sizes (int) AS RANGE RIGHT FOR VALUES (1000, 2000);
CREATE PARTITION SCHEME PS_Sizes AS PARTITION PF_Sizes ALL TO ([PRIMARY]);
CREATE TABLE sizes.PartitionedTable (Id int NOT NULL, Filler char(500) NOT NULL) ON PS_Sizes (Id);
CREATE CLUSTERED INDEX CX_PartitionedTable ON sizes.PartitionedTable (Id) ON PS_Sizes (Id);
CREATE INDEX IX_PartitionedTable_Filler ON sizes.PartitionedTable (Filler) ON PS_Sizes (Id);

CREATE TABLE sizes.ColumnstoreTable (Id int NOT NULL, V int NOT NULL);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_ColumnstoreTable ON sizes.ColumnstoreTable;

CREATE TABLE sizes.LockedTable (Id int NOT NULL CONSTRAINT PK_LockedTable PRIMARY KEY, V int NOT NULL);
CREATE INDEX IX_LockedTable_V ON sizes.LockedTable (V);
GO
WITH n AS (SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N FROM sys.all_columns a CROSS JOIN sys.all_columns b)
INSERT sizes.HeapTable SELECT N, 'x' FROM n;

WITH n AS (SELECT TOP (300) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N FROM sys.all_columns)
INSERT sizes.ClusteredTable SELECT N, REPLICATE(CAST(N'd' AS nvarchar(max)), 20000), REPLICATE('a', 5000), REPLICATE('b', 5000) FROM n;

WITH n AS (SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N FROM sys.all_columns a CROSS JOIN sys.all_columns b)
INSERT sizes.PartitionedTable SELECT N, 'x' FROM n;

-- Enough rows for a compressed row group, and some deleted, so its delete bitmap holds something.
WITH n AS (SELECT TOP (150000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N FROM sys.all_columns a CROSS JOIN sys.all_columns b)
INSERT sizes.ColumnstoreTable WITH (TABLOCK) SELECT N, N % 100 FROM n;
ALTER INDEX CCI_ColumnstoreTable ON sizes.ColumnstoreTable REBUILD;
DELETE sizes.ColumnstoreTable WHERE Id <= 500;

INSERT sizes.LockedTable VALUES (1, 1), (2, 2);
GO
-- Table Statistics' details (#243): one column of each key type, a full histogram with a NULL step,
-- statistics empty and never built, index types with no histogram, an awkward name, and stale ones.
CREATE SCHEMA stats;
GO
CREATE TABLE stats.KeyTypes
(
    KInt int, KBigint bigint, KDecimal decimal(38, 10), KDate date, KDatetime datetime, KSmalldatetime smalldatetime,
    KDatetime2 datetime2, KDatetimeoffset datetimeoffset, KTime time, KVarchar varchar(50), KNvarchar nvarchar(50),
    KGuid uniqueidentifier, KVarbinary varbinary(50), KBinary binary(2), KBit bit, KFloat float, KReal real, KMoney money, KEmpty varchar(10)
);
INSERT stats.KeyTypes VALUES
(
    1234, 9007199254740993, 1234567890123456789012345678.0123456789, '2026-01-15', '2026-01-15T13:45:30.123', '2026-01-15T13:46:00',
    '2026-01-15T13:45:30.1234567', '2026-01-15T13:45:30.1234567+05:30', '13:45:30.1234567', 'a varchar key', N'ключ nvarchar',
    '6F9619FF-8B86-D011-B42D-00C04FC964FF', 0x0A0B0C, 0x0D0E, 1, 0.1, 1.5, 12345.6789, ''
);
CREATE STATISTICS S_KInt ON stats.KeyTypes (KInt) WITH FULLSCAN;
CREATE STATISTICS S_KBigint ON stats.KeyTypes (KBigint) WITH FULLSCAN;
CREATE STATISTICS S_KDecimal ON stats.KeyTypes (KDecimal) WITH FULLSCAN;
CREATE STATISTICS S_KDate ON stats.KeyTypes (KDate) WITH FULLSCAN;
CREATE STATISTICS S_KDatetime ON stats.KeyTypes (KDatetime) WITH FULLSCAN;
CREATE STATISTICS S_KSmalldatetime ON stats.KeyTypes (KSmalldatetime) WITH FULLSCAN;
CREATE STATISTICS S_KDatetime2 ON stats.KeyTypes (KDatetime2) WITH FULLSCAN;
CREATE STATISTICS S_KDatetimeoffset ON stats.KeyTypes (KDatetimeoffset) WITH FULLSCAN;
CREATE STATISTICS S_KTime ON stats.KeyTypes (KTime) WITH FULLSCAN;
CREATE STATISTICS S_KVarchar ON stats.KeyTypes (KVarchar) WITH FULLSCAN;
CREATE STATISTICS S_KNvarchar ON stats.KeyTypes (KNvarchar) WITH FULLSCAN;
CREATE STATISTICS S_KGuid ON stats.KeyTypes (KGuid) WITH FULLSCAN;
CREATE STATISTICS S_KVarbinary ON stats.KeyTypes (KVarbinary) WITH FULLSCAN;
CREATE STATISTICS S_KBinary ON stats.KeyTypes (KBinary) WITH FULLSCAN;
CREATE STATISTICS S_KBit ON stats.KeyTypes (KBit) WITH FULLSCAN;
CREATE STATISTICS S_KFloat ON stats.KeyTypes (KFloat) WITH FULLSCAN;
CREATE STATISTICS S_KReal ON stats.KeyTypes (KReal) WITH FULLSCAN;
CREATE STATISTICS S_KMoney ON stats.KeyTypes (KMoney) WITH FULLSCAN;
CREATE STATISTICS S_KEmpty ON stats.KeyTypes (KEmpty) WITH FULLSCAN;

-- Counts per value that differ widely keep the histogram from merging steps; some NULLs give it a NULL step.
CREATE TABLE stats.Steps (K int NULL);
WITH n AS (SELECT TOP (1024) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N FROM sys.all_columns),
     counts AS (SELECT N AS K, POWER(2, (N * 7) % 11) AS Times FROM n)
INSERT stats.Steps
SELECT c.K FROM counts c JOIN n ON n.N <= c.Times;
INSERT stats.Steps SELECT TOP (100) NULL FROM sys.all_columns;
CREATE STATISTICS S_Steps ON stats.Steps (K) WITH FULLSCAN;
CREATE STATISTICS S_Steps_Filtered ON stats.Steps (K) WHERE K > 100 WITH FULLSCAN;

CREATE TABLE stats.EmptyTable (Id int NOT NULL CONSTRAINT PK_EmptyTable PRIMARY KEY, V int NOT NULL);
CREATE STATISTICS S_EmptyTable_V ON stats.EmptyTable (V) WITH FULLSCAN;

-- IX_NeverBuilt_V was made on no rows and has had no reason to update; S_NeverBuilt_Id was made after them.
CREATE TABLE stats.NeverBuilt (Id int NOT NULL, V int NOT NULL);
CREATE INDEX IX_NeverBuilt_V ON stats.NeverBuilt (V);
GO
WITH n AS (SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N FROM sys.all_columns)
INSERT stats.NeverBuilt SELECT N, N FROM n;
CREATE STATISTICS S_NeverBuilt_Id ON stats.NeverBuilt (Id) WITH FULLSCAN;

-- Only the primary key has a histogram here.
CREATE TABLE stats.NoHistogram (Id int NOT NULL CONSTRAINT PK_NoHistogram PRIMARY KEY, V int NOT NULL, Doc xml NULL, Place geography NULL);
INSERT stats.NoHistogram (Id, V) VALUES (1, 1), (2, 2), (3, 3);
UPDATE STATISTICS stats.NoHistogram WITH FULLSCAN;
CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_NoHistogram ON stats.NoHistogram (V);
CREATE PRIMARY XML INDEX PX_NoHistogram ON stats.NoHistogram (Doc);
CREATE SPATIAL INDEX SX_NoHistogram ON stats.NoHistogram (Place);
-- Not a real index: it has only a row in the catalog, for the Database Tuning Advisor.
CREATE INDEX IX_NoHistogram_Hypothetical ON stats.NoHistogram (V) WITH STATISTICS_ONLY = -1;

CREATE TABLE stats.ColumnOnly (Id int NOT NULL, V int NOT NULL);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_ColumnOnly ON stats.ColumnOnly;
INSERT stats.ColumnOnly VALUES (1, 1), (2, 2);

CREATE TABLE stats.NoIndex (Id int NOT NULL);

CREATE TABLE stats.[Odd ]]name's table] (Id int NOT NULL, [V ]]'s] int NOT NULL);
INSERT stats.[Odd ]]name's table] VALUES (1, 1), (2, 2), (3, 3);
CREATE INDEX [IX odd ]]'s.stat] ON stats.[Odd ]]name's table] ([V ]]'s]);

-- Past the auto-update threshold: 900 changes of 1,000 rows, with nothing since to recompile for.
CREATE TABLE stats.Stale (Id int NOT NULL, K int NOT NULL);
WITH n AS (SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N FROM sys.all_columns)
INSERT stats.Stale SELECT N, N FROM n;
CREATE STATISTICS S_Stale_K ON stats.Stale (K) WITH FULLSCAN;
CREATE STATISTICS S_Stale_Id ON stats.Stale (Id) WITH FULLSCAN;
GO
UPDATE stats.Stale SET K = K + 1 WHERE Id <= 900;
GO
-- Another database, to read without connecting to it, and one that's offline, never to be offered.
CREATE DATABASE SqlVitalsITOther;
GO
USE SqlVitalsITOther;
GO
CREATE TABLE dbo.OnlyInOther (Id int NOT NULL CONSTRAINT PK_OnlyInOther PRIMARY KEY, V int NOT NULL);
CREATE INDEX IX_OnlyInOther_V ON dbo.OnlyInOther (V);
INSERT dbo.OnlyInOther VALUES (1, 1);
UPDATE STATISTICS dbo.OnlyInOther WITH FULLSCAN;
GO
-- Its rule for automatic updates is the older one.
ALTER DATABASE SqlVitalsITOther SET COMPATIBILITY_LEVEL = 120;
GO
USE master;
GO
CREATE DATABASE SqlVitalsITOffline;
GO
ALTER DATABASE SqlVitalsITOffline SET OFFLINE WITH ROLLBACK IMMEDIATE;
GO
