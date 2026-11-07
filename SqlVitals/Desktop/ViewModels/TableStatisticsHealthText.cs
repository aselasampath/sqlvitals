namespace SqlVitals.Desktop.ViewModels;

/// <summary>The words of the Statistics health column and its chips (#243).</summary>
public static class TableStatisticsHealthText
{
    public const string Locked = "Locked";

    /// <summary>The chips: a table with nothing to rate has a dash, and none of them matches it.</summary>
    public static readonly IReadOnlyList<string> Chips = ["Good", "Fair", "Poor", Locked];
}
