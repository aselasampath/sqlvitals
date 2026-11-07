using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A row of Table Statistics' table list (#242). A class, not a record: after a refresh the row with
/// the same figures is still a new row, for the grid to select again. Its Statistics health arrives
/// after its sizes (#243), in place, so the grid keeps its selection.
/// </summary>
/// <param name="largestPages">The largest table's pages: the bars are in proportion to it.</param>
public sealed class TableSizeRow(TableSize table, long largestPages) : ObservableObject
{
    /// <summary>The widest bar, in pixels.</summary>
    private const double MaxBarWidth = 120;

    public TableSize Table { get; } = table;

    public string SchemaName => Table.SchemaName;
    public string TableName  => Table.TableName;
    public long? RowsInTable => Table.RowsInTable;
    public long? DataPages   => Table.DataPages;
    public long? IndexPages  => Table.IndexPages;
    public long? TotalPages  => Table.TotalPages;
    public bool IsLocked     => Table.IsLocked;

    public string DataText  => TableSpace.SizeText(DataPages);
    public string IndexText => TableSpace.SizeText(IndexPages);
    public string TotalText => IsLocked ? "Locked" : TableSpace.SizeText(TotalPages);

    public double BarWidth { get; } = largestPages > 0 ? MaxBarWidth * (table.TotalPages ?? 0) / largestPages : 0;

    // ── Statistics health ────────────────────────────────────────────────

    /// <summary>What was read of its statistics; null until it arrives, or if it never does.</summary>
    public TableStatisticsHealth? Health { get; private set; }

    /// <summary>Good, Fair, Poor or Locked; a dash for a table with nothing to rate; null until it arrives.</summary>
    public string? HealthText => Health switch
    {
        null => null,
        { State: StatisticsHealthState.Locked } => TableStatisticsHealthText.Locked,
        { State: StatisticsHealthState.Rated, Rating: { } rating } => rating.ToString(),
        _ => TableSpace.Unknown,
    };

    /// <summary>Why it says what it says.</summary>
    public string? HealthTip => Health switch
    {
        null => null,
        { State: StatisticsHealthState.Locked } => "Under a schema change, so its statistics can't be read now. Refresh reads them again.",
        { State: StatisticsHealthState.NoStatistics } => "This table has no statistics object that can hold a histogram.",
        { State: StatisticsHealthState.NotReadable } => "This login can't read this table's statistics. The message under the grid says which permission is missing.",
        { Rating: { } rating } => $"{rating}: the worst of this table's statistics, from each one's header. Open one on the Statistics tab: its histogram and density can rate it lower.",
        _ => TableSpace.Unknown,
    };

    /// <summary>The worst first, ascending: Poor, Fair, Locked, Good, then those with nothing to rate, then those not read.</summary>
    public int HealthOrder => Health switch
    {
        null => 6,
        { State: StatisticsHealthState.Locked } => 2,
        { State: StatisticsHealthState.NoStatistics } => 4,
        { State: StatisticsHealthState.NotReadable } => 5,
        { Rating: StatisticsRating.Poor } => 0,
        { Rating: StatisticsRating.Fair } => 1,
        { Rating: StatisticsRating.Good } => 3,
        _ => 4,
    };

    public double? MostChangedPct => Health?.MostChangedPct;

    public string? MostChangedText => Health is null ? null
        : MostChangedPct is { } pct ? pct.ToString("0.#", CultureInfo.CurrentCulture) + " %" : TableSpace.Unknown;

    public DateTime? OldestUpdate => Health?.OldestUpdate;

    public string? OldestUpdateText => Health is null ? null
        : OldestUpdate is { } oldest ? oldest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : TableSpace.Unknown;

    /// <summary>Its Statistics health arrived.</summary>
    public void ShowHealth(TableStatisticsHealth health)
    {
        Health = health;
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HealthTip));
        OnPropertyChanged(nameof(HealthOrder));
        OnPropertyChanged(nameof(MostChangedPct));
        OnPropertyChanged(nameof(MostChangedText));
        OnPropertyChanged(nameof(OldestUpdate));
        OnPropertyChanged(nameof(OldestUpdateText));
    }
}
