namespace QuasarBridge.Data.Entities;

public class InterceptEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Direction { get; set; } = "";
    public int? RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public string Route { get; set; } = "";
    public string? Action { get; set; }
    public string? ActionResult { get; set; }
    public string? Preview { get; set; }
    public int PayloadBytes { get; set; }
}
