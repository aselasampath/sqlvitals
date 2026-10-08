SELECT wait_type AS WaitType, waiting_tasks_count AS WaitingTasks,
       wait_time_ms AS WaitTimeMs, signal_wait_time_ms AS SignalWaitTimeMs
FROM /*WAIT_STATS*/
WHERE wait_time_ms > 0 AND wait_type NOT IN (/*BENIGN_WAITS*/)
