using System.Net;
using System.Text.Json;
using SqlVitals.Engine;

namespace SqlVitals.Engine.Tests;

/// <summary>
/// Exercises the real Kestrel host + DI + controller pipeline end to end, using a
/// connection string that cannot succeed (nothing listens on it). This verifies the
/// error-handling contract (structured 500 with an errorTag) without needing a live
/// SQL Server instance.
/// </summary>
public class ApiHostTests
{
    private static Microsoft.AspNetCore.Builder.WebApplication BuildUnreachableApp() =>
        ApiHost.Build([], port: 0, extraConfig: new()
        {
            ["ConnectionStrings:SqlServer"] =
                "Server=127.0.0.1,1;Database=nonexistent;Connect Timeout=1;TrustServerCertificate=true;",
        });

    [Fact]
    public async Task GetCumulative_WhenDatabaseIsUnreachable_Returns500WithErrorTag()
    {
        var app = BuildUnreachableApp();
        await app.StartAsync();
        try
        {
            var port = ApiHost.GetBoundPort(app);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var response = await client.GetAsync("/api/waitstats/cumulative");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(
                "WaitStatsRepository.GetCumulativeWaitsAsync",
                body.GetProperty("errorTag").GetString());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task GetBoundPort_AfterStart_ReturnsANonZeroPort()
    {
        var app = BuildUnreachableApp();
        await app.StartAsync();
        try
        {
            Assert.True(ApiHost.GetBoundPort(app) > 0);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
