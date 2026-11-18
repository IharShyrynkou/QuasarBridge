namespace QuasarBridge.Services.Media;

/// <summary>
/// How long the microphone must stay quiet before the recognized phrase is handled.
/// </summary>
public static class SpeechPause
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1500);
}
