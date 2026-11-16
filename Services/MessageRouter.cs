using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuasarBridge.Data;
using QuasarBridge.Options;

namespace QuasarBridge.Services;

public enum MessageRoute
{
    Yandex,
    Local,
    Drop
}

public sealed class RouteDecision
{
    public required MessageRoute Route { get; init; }
    public string? Rule { get; init; }
    public int? RuleId { get; init; }
    public string? Action { get; init; }
    public string? ActionPayload { get; init; }
    public string? Preview { get; init; }
}

/// <summary>
/// Voice leaves the station as Opus/Ogg audio — the spoken phrase is NOT in station→upstream text.
/// Alice ASR/NLU runs in the cloud, so series intents are detected on upstream→station replies
/// (e.g. «Какой сериал вам включить?», video_play / tv_show markers).
/// </summary>
public sealed class MessageRouter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<QuasarBridgeOptions> _options;
    private readonly ILogger<MessageRouter> _logger;
    private readonly List<CompiledRule> _rules = new();
    private readonly object _lock = new();
    private string _revision = "00000000";

    public MessageRouter(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<QuasarBridgeOptions> options,
        ILogger<MessageRouter> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    public string Revision
    {
        get { lock (_lock) return _revision; }
    }

    public int RuleCount
    {
        get { lock (_lock) return _rules.Count; }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QuasarBridgeDbContext>();
        var rows = await db.RoutingRules
            .AsNoTracking()
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);

        var compiled = new List<CompiledRule>();
        foreach (var row in rows)
        {
            var route = ParseRoute(row.Route);
            Regex? regex = null;
            if (!string.IsNullOrWhiteSpace(row.Regex))
            {
                try
                {
                    regex = new Regex(row.Regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Invalid routing regex in rule {name}", row.Name);
                    continue;
                }
            }

            compiled.Add(new CompiledRule(
                row.Id,
                row.Name,
                route,
                DeserializeList(row.ContainsAnyJson).Select(s => s.ToLowerInvariant()).ToArray(),
                DeserializeList(row.ContainsNoneJson).Select(s => s.ToLowerInvariant()).ToArray(),
                regex,
                row.Action,
                row.ActionPayload));
        }

        var fingerprint = string.Join('|', rows.Select(r => $"{r.Id}:{r.UpdatedAt.UtcTicks}:{r.Enabled}:{r.Priority}:{r.ContainsAnyJson}"));
        var rev = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..8];

        lock (_lock)
        {
            _rules.Clear();
            _rules.AddRange(compiled);
            _revision = rev;
        }

        _logger.LogInformation("Routing rebuilt revision={rev} rules={count} mode={mode}", rev, compiled.Count, _options.CurrentValue.Mode);
    }

    public RouteDecision Decide(string direction, ReadOnlySpan<byte> payload, bool isText)
    {
        var opts = _options.CurrentValue;
        if (!string.Equals(opts.Mode, "hybrid", StringComparison.OrdinalIgnoreCase))
            return new RouteDecision { Route = MessageRoute.Yandex, Rule = "observe" };

        // Skip pure audio (Ogg/Opus, possibly length-prefixed) — no searchable text.
        if (MediaFrame.IsCloudAudio(payload))
            return new RouteDecision { Route = MessageRoute.Yandex, Rule = "audio_ogg" };

        string text;
        try { text = Encoding.UTF8.GetString(payload); }
        catch { return new RouteDecision { Route = MessageRoute.Yandex, Rule = "undecodable" }; }

        if (!isText && !LooksSearchable(text))
            return new RouteDecision { Route = MessageRoute.Yandex, Rule = "binary_opaque" };

        var preview = Truncate(text.Replace('\r', ' ').Replace('\n', ' '), 240);
        var lower = text.ToLowerInvariant();

        lock (_lock)
        {
            foreach (var rule in _rules)
            {
                if (!rule.Matches(lower, text)) continue;
                _logger.LogInformation("Route match dir={dir} rule={rule} -> {route} action={action}: {preview}",
                    direction, rule.Name, rule.Route, rule.Action, preview);
                return new RouteDecision
                {
                    Route = rule.Route,
                    Rule = rule.Name,
                    RuleId = rule.Id,
                    Action = rule.Action,
                    ActionPayload = rule.ActionPayload,
                    Preview = preview
                };
            }
        }

        return new RouteDecision { Route = MessageRoute.Yandex, Rule = "default", Preview = preview };
    }

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    private static MessageRoute ParseRoute(string route) => route.Trim().ToLowerInvariant() switch
    {
        "local" or "llm" or "home" or "smarttube" or "open_app" => MessageRoute.Local,
        "drop" => MessageRoute.Drop,
        _ => MessageRoute.Yandex
    };

    private static bool LooksSearchable(string s)
    {
        if (s.Length < 4) return false;
        var printable = 0;
        foreach (var ch in s)
        {
            if (!char.IsControl(ch) || ch is '\n' or '\r' or '\t') printable++;
        }
        return printable * 100 / s.Length >= 40;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private sealed class CompiledRule(
        int id,
        string name,
        MessageRoute route,
        string[] any,
        string[] none,
        Regex? regex,
        string action,
        string? actionPayload)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public MessageRoute Route { get; } = route;
        public string Action { get; } = action;
        public string? ActionPayload { get; } = actionPayload;

        public bool Matches(string lower, string original)
        {
            if (any.Length > 0 && !any.Any(lower.Contains)) return false;
            if (none.Any(lower.Contains)) return false;
            if (regex is not null && !regex.IsMatch(original)) return false;
            return any.Length > 0 || regex is not null;
        }
    }
}
