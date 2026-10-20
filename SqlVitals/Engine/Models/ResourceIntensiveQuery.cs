using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Models;

/// <summary>A resource-intensive query from dm_exec_query_stats.</summary>
public record ResourceIntensiveQuery(
    string?  QueryText,
    string?  DatabaseName,
    string?  ObjectName,
    long     ExecutionCount,
    long     TotalLogicalReads,
    long     AvgLogicalReads,
    long     TotalCPUTime,
    long     AvgCPUTime,
    long     TotalElapsedTime,
    long     AvgElapsedTime,
    long     TotalPhysicalReads,
    long     TotalLogicalWrites,
    DateTime LastExecutionTime,
    DateTime PlanCreationTime,
    DateTime CaptureTime,
    string?  QueryPlan        = null,
    long?    LastLogicalReads = null,
    string?  ObjectType       = null
)
{
    /// <summary>The text on one line without its comments, as the grid shows and sorts it (#232).</summary>
    public string QueryTextLine => SqlPreview.Line(QueryText);

    /// <summary>The cell's tooltip: the text with its line breaks, cut short when huge; null when there is none.</summary>
    public string? QueryTextTip => SqlPreview.TooltipOrNull(QueryText);
}
