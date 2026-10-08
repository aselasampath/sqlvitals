CREATE EVENT SESSION [/*SESSION*/] ON /*SCOPE*/
ADD EVENT sqlserver.rpc_completed (
    ACTION (/*ACTIONS*/)
    WHERE (/*RPC_PREDICATE*/)),
ADD EVENT sqlserver.module_end (
    SET collect_statement = 1
    ACTION (/*ACTIONS*/)
    WHERE (/*MODULE_PREDICATE*/))
ADD TARGET package0.ring_buffer (
    SET max_memory = /*MAX_MEMORY_KB*/, max_events_limit = /*MAX_EVENTS*/)
WITH (MAX_DISPATCH_LATENCY = 2 SECONDS, STARTUP_STATE = OFF, TRACK_CAUSALITY = ON);
