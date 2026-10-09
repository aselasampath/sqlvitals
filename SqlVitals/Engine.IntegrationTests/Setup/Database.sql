-- The database the integration tests read (#46), made afresh on each run. Run from master,
-- batch by batch (GO). Each object is here so that some query has a row to map:
--   Orders.Notes is in no index, so GetCustomerOrders does key lookups;
--   IX_Orders_Unused is written to and never read; nothing indexes Amount (a missing index);
--   Events is keyed on NEWID(), so its clustered index is fragmented;
--   FindOrdersByCode compares the varchar column with an nvarchar (an implicit conversion);
--   the orphaned user's login is dropped; Held, LockA and LockB are for blocking and a deadlock.

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
