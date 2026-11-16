using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QuasarBridge.Data;
using QuasarBridge.Data.Entities;

namespace QuasarBridge.Services;

public enum LocalAction
{
    /// <summary>Do not forward the frame.</summary>
    Drop,
    /// <summary>Forward original bytes unchanged.</summary>
    ForwardAnyway,
    /// <summary>Forward ReplacementPayload (same-length silenced reply).</summary>
    ForwardSilenced
}

public sealed class LocalHandleResult
{
    public required LocalAction Action { get; init; }
    public byte[]? ReplacementPayload { get; init; }
    public string? Note { get; init; }
}

public sealed class LocalCommandHandler
{
    private static readonly TimeSpan OpenAppDebounce = TimeSpan.FromSeconds(8);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LocalCommandHandler> _logger;
    private readonly object _debounceLock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastOpenApp = new(StringComparer.OrdinalIgnoreCase);

    public LocalCommandHandler(IServiceScopeFactory scopeFactory, ILogger<LocalCommandHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<LocalHandleResult> HandleAsync(
        RouteDecision decision,
        byte[] originalPayload,
        CancellationToken ct)
    {
        var action = (decision.Action ?? "none").Trim().ToLowerInvariant();
        var note = $"matched:{decision.Rule}";

        try
        {
            switch (action)
            {
                case "open_app":
                    note = OpenApp(decision.Rule ?? "rule", decision.ActionPayload);
                    break;
                case "none":
                case "":
                    note = $"matched:{decision.Rule}; no local action";
                    break;
                default:
                    note = $"unknown action '{decision.Action}'";
                    _logger.LogWarning("Unknown local action {action} for rule {rule}", decision.Action, decision.Rule);
                    break;
            }
        }
        catch (Exception ex)
        {
            note = $"error:{ex.Message}";
            _logger.LogError(ex, "Local action failed rule={rule} action={action}", decision.Rule, decision.Action);
        }

        await LogEventAsync(decision, originalPayload.Length, note, ct);
        _logger.LogInformation("Local handle rule={rule} action={action}: {note}", decision.Rule, action, note);

        // Always deliver a cloud reply (protocol stays happy), but with blank speech.
        return new LocalHandleResult
        {
            Action = LocalAction.ForwardSilenced,
            ReplacementPayload = PayloadSilencer.BlankReadableSpeech(originalPayload),
            Note = note
        };
    }

    private string OpenApp(string ruleKey, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new InvalidOperationException("ActionPayload is empty for open_app");

        lock (_debounceLock)
        {
            if (_lastOpenApp.TryGetValue(ruleKey, out var last) && DateTimeOffset.UtcNow - last < OpenAppDebounce)
                return $"debounced:{payload}";
            _lastOpenApp[ruleKey] = DateTimeOffset.UtcNow;
        }

        var parts = payload.Split('|', 2, StringSplitOptions.TrimEntries);
        var fileName = parts[0];
        var args = parts.Length > 1 ? parts[1] : "";

        if (!File.Exists(fileName))
            throw new FileNotFoundException("App not found", fileName);

        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = true
        });
        return $"opened:{fileName}";
    }

    private async Task LogEventAsync(RouteDecision decision, int payloadBytes, string result, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<QuasarBridgeDbContext>();
            db.InterceptEvents.Add(new InterceptEvent
            {
                At = DateTimeOffset.UtcNow,
                Direction = "intercept",
                RuleId = decision.RuleId,
                RuleName = decision.Rule ?? "",
                Route = decision.Route.ToString().ToLowerInvariant(),
                Action = decision.Action,
                ActionResult = Truncate(result, 512),
                Preview = Truncate(decision.Preview, 2000),
                PayloadBytes = payloadBytes
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist InterceptEvent");
        }
    }

    public static bool TryExtractReadableText(ReadOnlySpan<byte> payload, out string text)
    {
        text = "";
        try
        {
            var s = Encoding.UTF8.GetString(payload);
            if (LooksMostlyPrintable(s))
            {
                text = s;
                return true;
            }

            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                text = s;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool LooksMostlyPrintable(string s)
    {
        if (s.Length == 0) return false;
        var printable = 0;
        foreach (var ch in s)
        {
            if (!char.IsControl(ch) || ch is '\n' or '\r' or '\t') printable++;
        }
        return printable * 100 / s.Length >= 85;
    }

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s[..max];
    }
}
