/*PERIOD*/
SELECT rs.slot AS Slot,
       /*TOTALS*/
FROM (
    SELECT DATEDIFF(MINUTE, @origin, i.start_time) / @slotMinutes AS slot, r.*
    FROM sys.query_store_runtime_stats_interval i
    JOIN sys.query_store_runtime_stats r ON r.runtime_stats_interval_id = i.runtime_stats_interval_id
    WHERE i.start_time >= @periodFrom AND i.start_time < @periodTo
) rs
GROUP BY rs.slot
ORDER BY rs.slot
