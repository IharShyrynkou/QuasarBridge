namespace QuasarBridge.Data.Entities;

public class RoutingRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>yandex | local | drop</summary>
    public string Route { get; set; } = "yandex";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    /// <summary>JSON array of substrings (case-insensitive).</summary>
    public string ContainsAnyJson { get; set; } = "[]";
    /// <summary>JSON array of substrings that must NOT match.</summary>
    public string ContainsNoneJson { get; set; } = "[]";
    public string? Regex { get; set; }
    /// <summary>local action: none | open_app</summary>
    public string Action { get; set; } = "none";
    /// <summary>For open_app: executable path (optional args after |).</summary>
    public string? ActionPayload { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
