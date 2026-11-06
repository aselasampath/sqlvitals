namespace SqlVitals.Desktop.ViewModels;

/// <summary>One part of the bar showing a table's space (#242): its data, or one of its indexes.</summary>
/// <param name="Share">Its fraction of the table, 0 to 1.</param>
/// <param name="Tip">What it is and how big: "IX_Orders_Date: 116 MB (18.4 %)".</param>
public sealed record SpaceBarPart(double Share, string Tip, SpaceBarPart.Kinds Kind)
{
    /// <summary>The theme colour it's drawn in.</summary>
    public enum Kinds { Data, Index, Costly }
}
