namespace LocalAgent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string Listen { get; set; } = "http://127.0.0.1:8787";
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen3.5:4b";
    public string PiperPath { get; set; } = "runtime/piper/piper/piper.exe";
    public string VoicePath { get; set; } = "runtime/voices/ru_RU-irina-medium.onnx";
    public string ScriptsPath { get; set; } = "scripts";
}
