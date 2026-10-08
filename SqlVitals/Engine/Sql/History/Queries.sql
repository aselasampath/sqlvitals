SELECT TOP (@maxQueries)
       CONVERT(varchar(18), query_hash, 1) AS QueryHash,
       SUM(execution_count)      AS Executions,
       SUM(total_worker_time)    AS WorkerTimeUs,
       SUM(total_elapsed_time)   AS ElapsedTimeUs,
       SUM(total_logical_reads)  AS LogicalReads,
       SUM(total_logical_writes) AS LogicalWrites,
       MIN(creation_time)        AS FirstPlanCreated
FROM sys.dm_exec_query_stats
WHERE query_hash <> 0x0000000000000000
GROUP BY query_hash
HAVING @since IS NULL OR MAX(last_execution_time) >= @since
ORDER BY SUM(total_worker_time) DESC
