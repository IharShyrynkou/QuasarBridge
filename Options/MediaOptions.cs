namespace QuasarBridge.Options;

public sealed class JellyfinOptions
{
    public const string SectionName = "Jellyfin";

    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string? UserId { get; set; }
}

public sealed class PotPlayerOptions
{
    public const string SectionName = "PotPlayer";

    public string Path { get; set; } = @"C:\Program Files\PotPlayer\PotPlayerMini64.exe";
}
