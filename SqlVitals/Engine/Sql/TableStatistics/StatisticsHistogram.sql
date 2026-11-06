-- Waits on a schema change even under READ UNCOMMITTED: 1222 means locked. range_high_key comes back as
-- text made here: as sql_variant, SqlClient overflows on a large decimal. Dates and times are in ISO
-- form, binary in hex, and each number at its full precision, whatever the server's language.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT h.step_number AS Number,
    CASE CONVERT(sysname, SQL_VARIANT_PROPERTY(h.range_high_key, 'BaseType'))
        WHEN N'date'           THEN CONVERT(nvarchar(10), CONVERT(date, h.range_high_key), 23)
        WHEN N'datetime'       THEN CONVERT(nvarchar(30), CONVERT(datetime, h.range_high_key), 121)
        WHEN N'smalldatetime'  THEN CONVERT(nvarchar(30), CONVERT(smalldatetime, h.range_high_key), 121)
        WHEN N'datetime2'      THEN CONVERT(nvarchar(30), CONVERT(datetime2(7), h.range_high_key), 121)
        WHEN N'datetimeoffset' THEN CONVERT(nvarchar(40), CONVERT(datetimeoffset(7), h.range_high_key), 121)
        WHEN N'time'           THEN CONVERT(nvarchar(20), CONVERT(time(7), h.range_high_key), 121)
        WHEN N'binary'         THEN CONVERT(nvarchar(4000), CONVERT(varbinary(8000), h.range_high_key), 1)
        WHEN N'varbinary'      THEN CONVERT(nvarchar(4000), CONVERT(varbinary(8000), h.range_high_key), 1)
        WHEN N'float'          THEN CONVERT(nvarchar(40), CONVERT(float, h.range_high_key), 3)
        WHEN N'real'           THEN CONVERT(nvarchar(40), CONVERT(real, h.range_high_key), 3)
        WHEN N'money'          THEN CONVERT(nvarchar(40), CONVERT(money, h.range_high_key), 2)
        WHEN N'smallmoney'     THEN CONVERT(nvarchar(40), CONVERT(smallmoney, h.range_high_key), 2)
        ELSE CONVERT(nvarchar(4000), h.range_high_key)
    END AS RangeHiKey,
    h.range_rows AS RangeRows, h.equal_rows AS EqRows,
    h.distinct_range_rows AS DistinctRangeRows, h.average_range_rows AS AvgRangeRows
FROM sys.dm_db_stats_histogram(@ObjectId, @StatsId) h
ORDER BY h.step_number
