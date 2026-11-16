using System.ComponentModel.DataAnnotations;

namespace QuasarBridge.Controllers.Dtos;

public sealed class RoutingRuleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Route { get; set; } = "yandex";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public List<string> ContainsAny { get; set; } = new();
    public List<string> ContainsNone { get; set; } = new();
    public string? Regex { get; set; }
    public string Action { get; set; } = "none";
    public string? ActionPayload { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class UpsertRoutingRuleRequest
{
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";

    [Required, MaxLength(32)]
    public string Route { get; set; } = "local";

    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public List<string> ContainsAny { get; set; } = new();
    public List<string> ContainsNone { get; set; } = new();
    public string? Regex { get; set; }

    [MaxLength(64)]
    public string Action { get; set; } = "none";

    [MaxLength(1024)]
    public string? ActionPayload { get; set; }

    [MaxLength(512)]
    public string? Description { get; set; }
}
