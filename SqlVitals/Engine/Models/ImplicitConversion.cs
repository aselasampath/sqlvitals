using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Models;

/// <summary>A cached plan that contains CONVERT_IMPLICIT operations.</summary>
public record ImplicitConversion(
    string?  DatabaseName,
    string?  ObjectName,
    int      ExecutionCount,
    long     PlanSizeKB,
    string?  QueryText,
    DateTime CaptureTime
)
{
    /// <summary>The text on one line without its comments, as the grid shows and sorts it (#232).</summary>
    public string QueryTextLine => SqlPreview.Line(QueryText);

    /// <summary>The cell's tooltip: the text with its line breaks, cut short when huge; null when there is none.</summary>
    public string? QueryTextTip => SqlPreview.TooltipOrNull(QueryText);
}
