namespace SqlVitals.Engine.TableStatistics;

/// <summary>How well statistics serve the optimizer (#243).</summary>
public enum StatisticsRating
{
    Good,
    Fair,
    Poor,
}

/// <summary>What the Statistics health column can say of a table.</summary>
public enum StatisticsHealthState
{
    /// <summary>Its statistics were read, and <see cref="TableStatisticsHealth.Rating"/> holds the worst.</summary>
    Rated,

    /// <summary>Under a schema change: its statistics can't be read now.</summary>
    Locked,

    /// <summary>It has no statistics object that can hold a histogram.</summary>
    NoStatistics,

    /// <summary>The login can't read them: the batch's failures say which permission is missing.</summary>
    NotReadable,
}

/// <summary>A row of <c>sys.dm_db_stats_properties</c>. The function has none for statistics never built.</summary>
/// <param name="LastUpdated">When it was last built.</param>
/// <param name="Rows">Rows in the table (or the filter) when it was last built.</param>
/// <param name="RowsSampled">Rows read to build it.</param>
/// <param name="Steps">Histogram steps.</param>
/// <param name="Modifications">Changes to its leading column since it was built.</param>
public sealed record StatisticsProperties(DateTime? LastUpdated, long Rows, long RowsSampled, int Steps, long Modifications)
{
    /// <summary>Rows read as a percentage of the rows there were; null with no rows.</summary>
    public double? SamplePct => Rows > 0 ? 100.0 * RowsSampled / Rows : null;

    /// <summary>Changes as a percentage of the rows there were; null with no rows.</summary>
    public double? ChangedPct => Rows > 0 ? 100.0 * Modifications / Rows : null;
}

/// <summary>A table's line in the Statistics health column.</summary>
/// <param name="Rating">The worst of its statistics; null unless <paramref name="State"/> is Rated.</param>
/// <param name="MostChangedPct">The largest <see cref="StatisticsProperties.ChangedPct"/>.</param>
/// <param name="OldestUpdate">The earliest <see cref="StatisticsProperties.LastUpdated"/>.</param>
public sealed record TableStatisticsHealth(int ObjectId, StatisticsHealthState State, StatisticsRating? Rating, double? MostChangedPct, DateTime? OldestUpdate);

/// <summary>Health for a batch of tables, in the order asked for, and what couldn't be read of them.</summary>
public sealed record TableHealthBatch(IReadOnlyList<TableStatisticsHealth> Tables, IReadOnlyList<TableReadFailure> Failures);

/// <summary>A row of <c>sys.stats</c>.</summary>
/// <param name="IsIndex">It belongs to an index, not to column statistics.</param>
/// <param name="IsClustered">It belongs to the clustered index: the page starts on it.</param>
/// <param name="LeadingColumn">The column its histogram is on; null if that couldn't be read.</param>
public sealed record StatisticsObject(int StatsId, string Name, bool IsIndex, bool IsClustered, bool IsFiltered, string? LeadingColumn);

/// <summary>A table's statistics, the clustered index's first and then by name.</summary>
/// <param name="IsLocked">The table was under a schema change.</param>
public sealed record TableStatisticsList(IReadOnlyList<StatisticsObject> Statistics, bool IsLocked, IReadOnlyList<TableReadFailure> Failures);

/// <summary>
/// A row of <c>sys.dm_db_stats_histogram</c>. <paramref name="Key"/> is a real value from the table,
/// so it stays on the screen: see the privacy note on <see cref="StatisticsFinding"/>.
/// </summary>
/// <param name="Key"><c>RANGE_HI_KEY</c> as text, whatever the column's type; null for the NULL step.</param>
/// <param name="RangeRows"><c>RANGE_ROWS</c>: rows between the last step's key and this one's.</param>
/// <param name="EqRows"><c>EQ_ROWS</c>: rows equal to this key.</param>
/// <param name="DistinctRangeRows"><c>DISTINCT_RANGE_ROWS</c>: distinct values between the keys.</param>
/// <param name="AvgRangeRows"><c>AVG_RANGE_ROWS</c>: the estimate for a value between the keys.</param>
public sealed record HistogramStep(int Number, string? Key, double RangeRows, double EqRows, double DistinctRangeRows, double AvgRangeRows)
{
    public bool IsNullStep => Key is null;

    public double TotalRows => RangeRows + EqRows;
}

/// <summary>A row of <c>DBCC SHOW_STATISTICS ... WITH DENSITY_VECTOR</c>: one column prefix.</summary>
/// <param name="Columns">"Region, Status": the prefix, as the server lists it.</param>
/// <param name="AllDensity">1 / the distinct values of the prefix.</param>
/// <param name="AverageLength">Average bytes per value of the prefix.</param>
public sealed record DensityEntry(string Columns, double AllDensity, double AverageLength)
{
    /// <summary>The distinct values: also the <c>GROUP BY</c> estimate. Null at a density of 0.</summary>
    public double? DistinctValues => AllDensity > 0 ? 1 / AllDensity : null;

    /// <summary>The average rows per value in a table of this many rows.</summary>
    public double RowsPerValue(long rows) => AllDensity * rows;
}

/// <summary>What one statistics object holds. A part that couldn't be read is empty or null, and in failures.</summary>
/// <param name="Properties">Null when never built, or not read.</param>
/// <param name="Histogram">The steps in order; empty with no histogram yet.</param>
/// <param name="Density">The prefixes, the leading column first.</param>
/// <param name="CompatibilityLevel">The database's, which picks the auto-update rule; null if not read.</param>
/// <param name="IsLocked">The table was under a schema change.</param>
public sealed record StatisticsDetails(StatisticsProperties? Properties, IReadOnlyList<HistogramStep> Histogram, IReadOnlyList<DensityEntry> Density,
                                       int? CompatibilityLevel, bool IsLocked, IReadOnlyList<TableReadFailure> Failures);

/// <summary>What a finding is about.</summary>
public enum StatisticsFindingKind
{
    Skew,
    ParameterSensitivity,
    HiddenFrequentValue,
    FewSteps,
    LowSampleRate,
    NullShare,
    PastAutoUpdateThreshold,
}

/// <summary>
/// A plain-language finding. Its words name steps by number and never hold a histogram key: a key is
/// a real value from the table (an email, a name), and only the chart and grid show it (#243, #175).
/// </summary>
/// <param name="Severity">What it does to the rating. Good: worth knowing, but nothing against the statistics.</param>
/// <param name="What">What was found.</param>
/// <param name="Try">What to try.</param>
/// <param name="Steps">The step numbers it's about, if any.</param>
public sealed record StatisticsFinding(StatisticsFindingKind Kind, StatisticsRating Severity, string What, string Try, IReadOnlyList<int> Steps);

/// <summary>A statistics object's rating and findings.</summary>
/// <param name="Rating">The worst finding's, or Good; null when there's nothing to rate: never built, or not read.</param>
public sealed record StatisticsAssessment(StatisticsRating? Rating, IReadOnlyList<StatisticsFinding> Findings);
