namespace SqlVitals.Engine.QueryStore;

/// <summary>
/// Query Store's wait categories (sys.query_store_wait_stats.wait_category), which group wait types
/// as Microsoft's docs map them, each with one line on what it means (#111). Query Store records
/// waits while a query runs, not while it compiles.
/// </summary>
public static class QueryStoreWaitCategories
{
    private static readonly Dictionary<int, (string Name, string Explanation)> Categories = new()
    {
        [0]  = ("Unknown",           "Waits Query Store couldn't place in a category."),
        [1]  = ("CPU",               "Waiting for a CPU after using up a time slice (SOS_SCHEDULER_YIELD): the server is short of CPU, or these queries do a lot of work."),
        [2]  = ("Worker Thread",     "Waiting for a worker thread (THREADPOOL): the server ran out of threads, often from blocking or too much parallelism."),
        [3]  = ("Lock",              "Waiting for locks other sessions hold (LCK_M_%): blocking. Processes shows who blocks whom."),
        [4]  = ("Latch",             "Waiting for internal latches that aren't on data pages (LATCH_%)."),
        [5]  = ("Buffer Latch",      "Waiting for data pages already in memory (PAGELATCH_%): hot pages, such as inserts at the end of an index or TempDB allocation."),
        [6]  = ("Buffer IO",         "Waiting for data pages to be read from disk (PAGEIOLATCH_%): usually scans an index would avoid, or too little memory."),
        [7]  = ("Compilation",       "Waiting for memory to compile (RESOURCE_SEMAPHORE_QUERY_COMPILE). Query Store records waits while queries run, so it's rarely seen here."),
        [8]  = ("SQL CLR",           "Waits in CLR code (CLR%, SQLCLR%)."),
        [9]  = ("Mirroring",         "Database mirroring (DBMIRROR%)."),
        [10] = ("Transaction",       "Transaction management (XACT%, DTC%, TRANSACTION_MUTEX…), such as distributed transactions."),
        [11] = ("Idle",              "Idle waits (SLEEP_%, CHECKPOINT_QUEUE…): not a problem."),
        [12] = ("Preemptive",        "Calls out to Windows or other code (PREEMPTIVE_%), such as file, network or authentication operations."),
        [13] = ("Service Broker",    "Service Broker (BROKER_%)."),
        [14] = ("Tran Log IO",       "Waiting for the transaction log to be written (WRITELOG, LOGBUFFER…): a slow log disk, or many small commits."),
        [15] = ("Network IO",        "Waiting for the client to take the results (ASYNC_NETWORK_IO…): usually the application reading rows slowly, or a slow network."),
        [16] = ("Parallelism",       "Parallel threads waiting for each other (CXPACKET, CXCONSUMER…). Look at the plan, MAXDOP and the cost threshold for parallelism."),
        [17] = ("Memory",            "Waiting for a memory grant or memory (RESOURCE_SEMAPHORE, CMEMTHREAD…): often big sorts and hashes asking for large grants."),
        [18] = ("User Wait",         "Waits the code asked for (WAITFOR, WAIT_FOR_RESULTS…): not a problem in itself."),
        [19] = ("Tracing",           "SQL Trace and Profiler (TRACEWRITE, SQLTRACE_%…): a trace running on the server."),
        [20] = ("Full Text Search",  "Full-text search (FT_%, MSSEARCH…)."),
        [21] = ("Other Disk IO",     "Other disk I/O (IO_COMPLETION, ASYNC_IO_COMPLETION, BACKUPIO…), such as spills to TempDB or backups."),
        [22] = ("Replication",       "Replication and availability groups (REPL_%, HADR_%…), such as waiting for a secondary replica."),
        [23] = ("Log Rate Governor", "Azure SQL's log rate limit (LOG_RATE_GOVERNOR…): the service tier caps how fast the log can be written."),
    };

    /// <summary>The category's name: Query Store's own description when it gave one.</summary>
    public static string Name(int category, string? description) =>
        !string.IsNullOrWhiteSpace(description) ? description
        : Categories.TryGetValue(category, out var c) ? c.Name
        : $"Category {category}";

    /// <summary>What the category means, in one line.</summary>
    public static string Explanation(int category) =>
        Categories.TryGetValue(category, out var c) ? c.Explanation : "A wait category this version of SqlVitals doesn't know.";
}
