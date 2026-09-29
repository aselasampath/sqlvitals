namespace SqlVitals.Engine.QueryStore;

/// <summary>
/// The mean and standard deviation of all of a query's executions, from Query Store's per-interval,
/// per-plan rows (#111). Each row has its own count nᵢ, mean μᵢ and std dev σᵢ; combined:
/// <list type="bullet">
/// <item>N = Σnᵢ</item>
/// <item>μ = Σnᵢμᵢ / N</item>
/// <item>σ² = Σnᵢ(σᵢ² + μᵢ²) / N − μ², clamped at 0</item>
/// </list>
/// Unlike an average of the rows' std devs, this counts the difference between rows too: a query
/// that is steady within each hour but twice as slow some hours varies. The High Variation SQL
/// computes the same from Σn, Σnμ and Σn(σ² + μ²); this is its tested reference.
/// </summary>
public sealed class CombinedStdDev
{
    /// <summary>N: executions added so far.</summary>
    public long Count { get; private set; }

    /// <summary>Σnᵢμᵢ: the total of the metric.</summary>
    public double Sum { get; private set; }

    /// <summary>Σnᵢ(σᵢ² + μᵢ²): the sum of every execution's squared value.</summary>
    public double SumOfSquares { get; private set; }

    /// <summary>Adds one row: <paramref name="count"/> executions with this mean and std dev. Rows without executions add nothing.</summary>
    public CombinedStdDev Add(long count, double mean, double stdDev)
    {
        if (count <= 0)
            return this;
        Count        += count;
        Sum          += count * mean;
        SumOfSquares += count * (stdDev * stdDev + mean * mean);
        return this;
    }

    /// <summary>μ, or 0 with no executions.</summary>
    public double Mean => Count == 0 ? 0 : Sum / Count;

    /// <summary>σ, or 0 with no executions.</summary>
    public double StdDev => FromSums(Count, Sum, SumOfSquares);

    /// <summary>
    /// σ from the three sums, as the SQL computes it. Rounding can leave the variance just below
    /// zero when every execution was the same; that is clamped to 0 rather than failing SQRT.
    /// </summary>
    public static double FromSums(long count, double sum, double sumOfSquares)
    {
        if (count <= 0)
            return 0;
        return Math.Sqrt(Math.Max(0, Variance(count, sum, sumOfSquares)));
    }

    /// <summary>σ² before clamping; can come out a hair below zero.</summary>
    internal static double Variance(long count, double sum, double sumOfSquares)
    {
        var mean = sum / count;
        return sumOfSquares / count - mean * mean;
    }
}
