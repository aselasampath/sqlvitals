SELECT
    ps.database_id                                       AS DatabaseId,
    ps.object_id                                         AS ObjectId,
    ps.cached_time                                       AS CachedTime,
    ISNULL(DB_NAME(ps.database_id), '')                  AS DatabaseName,
    ISNULL(OBJECT_SCHEMA_NAME(ps.object_id, ps.database_id), '') AS SchemaName,
    ISNULL(OBJECT_NAME(ps.object_id, ps.database_id), '') AS ProcedureName,
    ps.execution_count                                   AS ExecutionCount,
    ps.total_worker_time                                 AS TotalWorkerTimeUs,
    ps.total_elapsed_time                                AS TotalElapsedTimeUs,
    ps.max_elapsed_time                                  AS MaxElapsedTimeUs,
    ps.total_logical_reads                               AS TotalLogicalReads,
    ps.total_logical_writes                              AS TotalLogicalWrites,
    ps.total_physical_reads                              AS TotalPhysicalReads,
    ps.last_execution_time                               AS LastExecutionTime
FROM sys.dm_exec_procedure_stats ps
/*DB_FILTER*/
