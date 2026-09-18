using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Tests.Errors;

public class WaitStatsExceptionTests
{
    [Fact]
    public void ErrorTag_CombinesComponentAndOperation()
    {
        var ex = new WaitStatsException("WaitStatsRepository", "GetCumulativeWaitsAsync", new InvalidOperationException("boom"));

        Assert.Equal("WaitStatsRepository.GetCumulativeWaitsAsync", ex.ErrorTag);
    }

    [Fact]
    public void Message_IsPrefixedWithComponentAndOperation()
    {
        var ex = new WaitStatsException("WaitStatsRepository", "GetActiveWaitsAsync", new InvalidOperationException("connection refused"));

        Assert.Equal("[WaitStatsRepository.GetActiveWaitsAsync] connection refused", ex.Message);
    }

    [Fact]
    public void SqlErrorNumber_IsZero_WhenInnerExceptionIsNotSqlException()
    {
        var ex = new WaitStatsException("WaitStatsRepository", "GetActiveWaitsAsync", new InvalidOperationException("boom"));

        Assert.Equal(0, ex.SqlErrorNumber);
    }

    [Fact]
    public void InnerException_IsPreserved()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new WaitStatsException("WaitStatsRepository", "GetActiveWaitsAsync", inner);

        Assert.Same(inner, ex.InnerException);
    }
}
