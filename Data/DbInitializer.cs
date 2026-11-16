using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuasarBridge.Data.Entities;

namespace QuasarBridge.Data;

public static class DbInitializer
{
    public const string PotPlayerPath = @"C:\Program Files\PotPlayer\PotPlayerMini64.exe";
    public const string SampleRuleName = "open_series_potplayer";

    /// <summary>
    /// Cloud reply markers that mean user asked to play a series.
    /// Spoken text never appears on station→upstream (only Ogg audio).
    /// </summary>
    public static readonly string[] SeriesCloudMarkers =
    [
        "какой сериал вам включить",
        "какой сериал включить",
        // «Включи сериал» без «мне» часто даёт этот ответ вместо уточнения названия:
        "подключить яндекс станцию к экрану",
        "подключите яндекс станцию к экрану",
        "фильмы и сериалы",
        "personal_assistant.scenarios.video_play",
        "\"tv_show\""
    ];

    public static async Task InitializeAsync(QuasarBridgeDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);

        var markersJson = JsonSerializer.Serialize(SeriesCloudMarkers);
        var existing = await db.RoutingRules.FirstOrDefaultAsync(r => r.Name == SampleRuleName, ct);
        if (existing is null)
        {
            db.RoutingRules.Add(CreateSampleRule(markersJson));
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded sample routing rule {name}", SampleRuleName);
            return;
        }

        // Keep sample rule pointed at cloud-side series markers (voice ASR is remote).
        existing.Route = "local";
        existing.Enabled = true;
        existing.Priority = 10;
        existing.ContainsAnyJson = markersJson;
        existing.ContainsNoneJson = "[]";
        existing.Action = "open_app";
        existing.ActionPayload = PotPlayerPath;
        existing.Description = "Просьба включить сериал → PotPlayer (по ответу Алисы в облаке)";
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Refreshed sample routing rule {name}", SampleRuleName);
    }

    private static RoutingRule CreateSampleRule(string markersJson) => new()
    {
        Name = SampleRuleName,
        Route = "local",
        Enabled = true,
        Priority = 10,
        ContainsAnyJson = markersJson,
        ContainsNoneJson = "[]",
        Action = "open_app",
        ActionPayload = PotPlayerPath,
        Description = "Просьба включить сериал → PotPlayer (по ответу Алисы в облаке)"
    };
}
