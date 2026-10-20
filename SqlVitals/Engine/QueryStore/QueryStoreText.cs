using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.QueryStore;

/// <summary>How the Query Store page shows statements and the objects they belong to (#111).</summary>
public static class QueryStoreText
{
    /// <summary>Longest one-line text a grid cell is given; the full text is in Query details.</summary>
    public const int MaxOneLineLength = SqlPreview.MaxLineLength;

    /// <summary>Longest text a tooltip is given, so a huge statement can't fill the screen.</summary>
    public const int MaxTooltipLength = SqlPreview.MaxTooltipLength;

    /// <summary>
    /// The statement on one line, as every grid shows query text (#232): see <see cref="SqlPreview.Line"/>.
    /// <c>SELECT⏎  s.session_id</c> reads <c>SELECT s.session_id</c>, and a comment above it is left out.
    /// </summary>
    public static string OneLine(string? text, int maxLength = MaxOneLineLength) => SqlPreview.Line(text, maxLength);

    /// <summary>The full statement for a tooltip, cut short past <see cref="MaxTooltipLength"/>.</summary>
    public static string Tooltip(string? text) => SqlPreview.Tooltip(text, "the full text is in Query details");

    /// <summary>
    /// "schema.name" of the procedure, function or trigger the statement is in; "ad hoc" when it's in
    /// none (object_id 0), or "object 1234" when the name can't be seen (dropped, or no permission).
    /// </summary>
    public static string ObjectLabel(long objectId, string? objectName) =>
        objectId == 0 ? "ad hoc" : string.IsNullOrEmpty(objectName) ? $"object {objectId}" : objectName;

    /// <summary>The script that unforces a plan, for the user to run. SqlVitals never runs it.</summary>
    public static string UnforceScript(long queryId, long planId) =>
        $"EXEC sys.sp_query_store_unforce_plan @query_id = {queryId}, @plan_id = {planId};";

    /// <summary>"Manual" or "Automatic plan correction", from plan_forcing_type_desc (MANUAL or AUTO).</summary>
    public static string ForcedBy(string? forcingType) => forcingType?.ToUpperInvariant() switch
    {
        "AUTO"   => "Automatic plan correction",
        "MANUAL" => "Manual",
        null or "" or "NONE" => "Manual",
        _        => forcingType,
    };
}
