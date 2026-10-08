;WITH PlanAgg AS (
    SELECT rs.plan_id,
        SUM(rs.avg_cpu_time*rs.count_executions) AS TotalCpuUs,
        SUM(rs.avg_duration*rs.count_executions) AS TotalDurationUs,
        SUM(rs.avg_logical_io_reads*rs.count_executions) AS TotalLogicalReads,
        SUM(rs.avg_logical_io_writes*rs.count_executions) AS TotalLogicalWrites,
        SUM(rs.avg_physical_io_reads*rs.count_executions) AS TotalPhysicalReads,
        SUM(rs.avg_query_max_used_memory*rs.count_executions) AS TotalMemoryPages,
        SUM(rs.count_executions) AS TotalExecutions,
        MAX(rs.last_execution_time) AS LastExecutionTime
    FROM sys.query_store_runtime_stats rs
    WHERE rs.last_execution_time >= DATEADD(HOUR,-24,GETUTCDATE())
    GROUP BY rs.plan_id
),
Ranked AS (
    SELECT p.query_id, pa.plan_id, pa.TotalCpuUs, pa.TotalDurationUs,
        pa.TotalLogicalReads, pa.TotalLogicalWrites, pa.TotalPhysicalReads,
        pa.TotalMemoryPages, pa.TotalExecutions, pa.LastExecutionTime,
        p.is_forced_plan, /*PLAN_TYPE*/ AS plan_type_desc,
        CASE WHEN pa.TotalExecutions>0 THEN pa.TotalCpuUs/pa.TotalExecutions ELSE 0 END AS AvgCpuUs,
        CASE WHEN pa.TotalExecutions>0 THEN pa.TotalDurationUs/pa.TotalExecutions ELSE 0 END AS AvgDurationUs,
        CASE WHEN pa.TotalExecutions>0 THEN pa.TotalLogicalReads/pa.TotalExecutions ELSE 0 END AS AvgLogicalReads,
        CASE WHEN pa.TotalExecutions>0 THEN pa.TotalMemoryPages*8/pa.TotalExecutions ELSE 0 END AS AvgMemoryKB,
        ROW_NUMBER() OVER (PARTITION BY p.query_id ORDER BY pa.TotalCpuUs DESC) AS rn
    FROM PlanAgg pa JOIN sys.query_store_plan p ON pa.plan_id=p.plan_id
    WHERE p.query_plan IS NOT NULL
)
SELECT TOP 20 r.query_id AS QueryId, r.plan_id AS PlanId,
    qt.query_sql_text AS QueryText,
    r.TotalExecutions,
    r.TotalCpuUs/1000.0 AS TotalCpuMs, r.AvgCpuUs/1000.0 AS AvgCpuMs,
    r.TotalDurationUs/1000.0 AS TotalDurationMs, r.AvgDurationUs/1000.0 AS AvgDurationMs,
    r.TotalLogicalReads, r.AvgLogicalReads,
    r.TotalLogicalWrites, r.TotalPhysicalReads,
    r.TotalMemoryPages*8/1024.0 AS TotalMemoryMB, r.AvgMemoryKB*1.0 AS AvgMemoryKB,
    r.is_forced_plan AS IsForcedPlan, r.plan_type_desc AS PlanType,
    r.LastExecutionTime AS LastExecutionTimeUtc, GETDATE() AS CaptureTime
FROM Ranked r
JOIN sys.query_store_query q ON r.query_id=q.query_id
JOIN sys.query_store_query_text qt ON q.query_text_id=qt.query_text_id
WHERE r.rn=1 ORDER BY r.TotalCpuUs DESC OPTION (LOOP JOIN)
