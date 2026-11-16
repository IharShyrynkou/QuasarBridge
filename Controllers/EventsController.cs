using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuasarBridge.Data;

namespace QuasarBridge.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController(QuasarBridgeDbContext db, ILogger<EventsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        try
        {
            var rows = await db.InterceptEvents
                .AsNoTracking()
                .OrderByDescending(e => e.Id)
                .Take(take)
                .Select(e => new
                {
                    e.Id,
                    e.At,
                    e.Direction,
                    e.RuleId,
                    e.RuleName,
                    e.Route,
                    e.Action,
                    e.ActionResult,
                    e.Preview,
                    e.PayloadBytes
                })
                .ToListAsync(ct);
            return Ok(rows);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list InterceptEvents");
            return Problem(detail: ex.Message, statusCode: 500);
        }
    }
}
