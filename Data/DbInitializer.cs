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

    public static async Task InitializeAsync(
        QuasarBridgeDbContext db,
        string databasePath,
        ILogger logger,
        CancellationToken ct = default)
    {
        var createdFile = !File.Exists(databasePath);
        if (createdFile)
        {
            var dir = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            logger.LogInformation("SQLite database missing — creating {path}", databasePath);
        }

        var createdSchema = await db.Database.EnsureCreatedAsync(ct);
        if (createdSchema)
            logger.LogInformation("SQLite schema created at {path}", databasePath);
        else if (!createdFile)
            logger.LogInformation("SQLite database ready at {path}", databasePath);

        var existing = await db.RoutingRules.FirstOrDefaultAsync(r => r.Name == SampleRuleName, ct);
        if (existing is { Enabled: true })
        {
            existing.Enabled = false;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.Description = "Отключено: сериал и фильм ведёт распознанная фраза, Jellyfin и PotPlayer.";
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Disabled legacy rule {name}: media dialogue handles series and movies", SampleRuleName);
        }
        else if (existing is null)
        {
            logger.LogInformation("No legacy {name} rule; series and movies are handled in the proxy", SampleRuleName);
        }
    }
}
