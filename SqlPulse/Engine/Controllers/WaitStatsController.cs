using Microsoft.AspNetCore.Mvc;
using SqlPulse.Engine.Errors;
using SqlPulse.Engine.Repositories;

namespace SqlPulse.Engine.Controllers;

[ApiController]
[Route("api/waitstats")]
[Produces("application/json")]
public class WaitStatsController(IWaitStatsRepository repo) : ControllerBase
{
    /// <summary>All cumulative wait types since last SQL Server restart.</summary>
    [HttpGet("cumulative")]
    public async Task<IActionResult> GetCumulative()
    {
        try { return Ok(await repo.GetCumulativeWaitsAsync()); }
        catch (Exception ex) { return QueryError(ex); }
    }

    /// <summary>Currently waiting sessions (live).</summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActive()
    {
        try { return Ok(await repo.GetActiveWaitsAsync()); }
        catch (Exception ex) { return QueryError(ex); }
    }

    /// <summary>Server-wide signal vs resource wait split with CPU pressure level.</summary>
    [HttpGet("signal-resource")]
    public async Task<IActionResult> GetSignalResource()
    {
        try
        {
            var data = await repo.GetSignalVsResourceAsync();
            return data is null ? NotFound() : Ok(data);
        }
        catch (Exception ex) { return QueryError(ex); }
    }

    /// <summary>Top 25 non-benign wait types ranked by total wait time.</summary>
    [HttpGet("top")]
    public async Task<IActionResult> GetTop()
    {
        try { return Ok(await repo.GetTopWaitTypesAsync()); }
        catch (Exception ex) { return QueryError(ex); }
    }

    /// <summary>Application connections grouped by program_name.</summary>
    [HttpGet("application-connections")]
    public async Task<IActionResult> GetApplicationConnections()
    {
        try { return Ok(await repo.GetApplicationConnectionsAsync()); }
        catch (Exception ex) { return QueryError(ex); }
    }

    // Returns a structured 500 that always includes an errorTag clients can use to locate
    // the originating query. Search the codebase for the errorTag value to jump to it.
    private ObjectResult QueryError(Exception ex)
    {
        if (ex is WaitStatsException wse)
            return StatusCode(500, new
            {
                errorTag      = wse.ErrorTag,
                error         = wse.Message,
                sqlErrorNumber = wse.SqlErrorNumber > 0 ? wse.SqlErrorNumber : (int?)null,
            });

        return StatusCode(500, new { error = ex.Message });
    }
}
