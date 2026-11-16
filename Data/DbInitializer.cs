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

        var existing = await db.RoutingRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Name == SampleRuleName, ct);
        if (existing is not null)
        {
            logger.LogInformation(
                "Routing rules already initialized (sample {name} id={id})",
                SampleRuleName, existing.Id);
            return;
        }

        var markersJson = JsonSerializer.Serialize(SeriesCloudMarkers);
        db.RoutingRules.Add(CreateSampleRule(markersJson));
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Seeded sample routing rule {name} (markers={count})",
            SampleRuleName, SeriesCloudMarkers.Length);
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
