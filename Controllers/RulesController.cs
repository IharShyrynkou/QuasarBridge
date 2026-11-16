using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuasarBridge.Controllers.Dtos;
using QuasarBridge.Data;
using QuasarBridge.Data.Entities;
using QuasarBridge.Services;

namespace QuasarBridge.Controllers;

[ApiController]
[Route("api/rules")]
public class RulesController(QuasarBridgeDbContext db, MessageRouter router) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<RoutingRuleDto>>> List(CancellationToken ct)
    {
        var rows = await db.RoutingRules.AsNoTracking().OrderBy(r => r.Priority).ThenBy(r => r.Id).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<RoutingRuleDto>> Get(int id, CancellationToken ct)
    {
        var row = await db.RoutingRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return row is null ? NotFound() : ToDto(row);
    }

    [HttpPost]
    public async Task<ActionResult<RoutingRuleDto>> Create([FromBody] UpsertRoutingRuleRequest req, CancellationToken ct)
    {
        if (await db.RoutingRules.AnyAsync(r => r.Name == req.Name, ct))
            return Conflict(new { error = "name already exists" });

        var now = DateTimeOffset.UtcNow;
        var row = new RoutingRule
        {
            Name = req.Name.Trim(),
            Route = req.Route.Trim().ToLowerInvariant(),
            Enabled = req.Enabled,
            Priority = req.Priority,
            ContainsAnyJson = JsonSerializer.Serialize(req.ContainsAny ?? new()),
            ContainsNoneJson = JsonSerializer.Serialize(req.ContainsNone ?? new()),
            Regex = req.Regex,
            Action = string.IsNullOrWhiteSpace(req.Action) ? "none" : req.Action.Trim().ToLowerInvariant(),
            ActionPayload = req.ActionPayload,
            Description = req.Description,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.RoutingRules.Add(row);
        await db.SaveChangesAsync(ct);
        await router.ReloadAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = row.Id }, ToDto(row));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<RoutingRuleDto>> Update(int id, [FromBody] UpsertRoutingRuleRequest req, CancellationToken ct)
    {
        var row = await db.RoutingRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return NotFound();

        if (!string.Equals(row.Name, req.Name, StringComparison.Ordinal) &&
            await db.RoutingRules.AnyAsync(r => r.Name == req.Name && r.Id != id, ct))
            return Conflict(new { error = "name already exists" });

        row.Name = req.Name.Trim();
        row.Route = req.Route.Trim().ToLowerInvariant();
        row.Enabled = req.Enabled;
        row.Priority = req.Priority;
        row.ContainsAnyJson = JsonSerializer.Serialize(req.ContainsAny ?? new());
        row.ContainsNoneJson = JsonSerializer.Serialize(req.ContainsNone ?? new());
        row.Regex = req.Regex;
        row.Action = string.IsNullOrWhiteSpace(req.Action) ? "none" : req.Action.Trim().ToLowerInvariant();
        row.ActionPayload = req.ActionPayload;
        row.Description = req.Description;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await router.ReloadAsync(ct);
        return ToDto(row);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var row = await db.RoutingRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return NotFound();
        db.RoutingRules.Remove(row);
        await db.SaveChangesAsync(ct);
        await router.ReloadAsync(ct);
        return NoContent();
    }

    [HttpPost("reload")]
    public async Task<IActionResult> Reload(CancellationToken ct)
    {
        await router.ReloadAsync(ct);
        return Ok(new { revision = router.Revision, rules = router.RuleCount });
    }

    private static RoutingRuleDto ToDto(RoutingRule row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Route = row.Route,
        Enabled = row.Enabled,
        Priority = row.Priority,
        ContainsAny = Deserialize(row.ContainsAnyJson),
        ContainsNone = Deserialize(row.ContainsNoneJson),
        Regex = row.Regex,
        Action = row.Action,
        ActionPayload = row.ActionPayload,
        Description = row.Description,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };

    private static List<string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }
}
