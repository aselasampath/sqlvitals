using System.Text;

namespace SqlVitals.Engine.QueryStore;

/// <summary>How the Query Store page shows statements and the objects they belong to (#111).</summary>
public static class QueryStoreText
{
    /// <summary>Longest one-line text a grid cell is given; the full text is in Query details.</summary>
    public const int MaxOneLineLength = 1000;

    /// <summary>Longest text a tooltip is given, so a huge statement can't fill the screen.</summary>
    public const int MaxTooltipLength = 4000;

    /// <summary>
    /// The statement on one line: line breaks, tabs and runs of spaces become one space, so
    /// <c>SELECT⏎  s.session_id</c> reads <c>SELECT s.session_id</c> instead of just <c>SELECT</c>.
    /// Anything else, such as a leading parameter list, is kept as it is.
    /// </summary>
    public static string OneLine(string? text, int maxLength = MaxOneLineLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
            if (sb.Length > maxLength)
                return sb.ToString(0, maxLength) + "…";
        }
        return sb.ToString();
    }

    /// <summary>The full statement for a tooltip, cut short past <see cref="MaxTooltipLength"/>.</summary>
    public static string Tooltip(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return trimmed.Length <= MaxTooltipLength
            ? trimmed
            : trimmed[..MaxTooltipLength] + "\n… (the full text is in Query details)";
    }

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
