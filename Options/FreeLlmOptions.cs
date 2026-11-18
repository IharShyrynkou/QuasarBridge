namespace QuasarBridge.Options;

public sealed class FreeLlmOptions
{
    public const string SectionName = "FreeLlm";

    public string BaseUrl { get; set; } = "http://127.0.0.1:3001/v1/chat/completions";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.5-flash-lite";
}
