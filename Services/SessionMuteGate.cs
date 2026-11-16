namespace QuasarBridge.Services;

/// <summary>Drops cloud Ogg/TTS for a short window after a local turn.</summary>
public sealed class SessionMuteGate
{
    private long _muteUntilUtcTicks;

    public void Mute(TimeSpan duration)
    {
        var until = DateTimeOffset.UtcNow.Add(duration).UtcTicks;
        while (true)
        {
            var cur = Interlocked.Read(ref _muteUntilUtcTicks);
            if (until <= cur) return;
            if (Interlocked.CompareExchange(ref _muteUntilUtcTicks, until, cur) == cur) return;
        }
    }

    public bool IsMuted => DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _muteUntilUtcTicks);
}
