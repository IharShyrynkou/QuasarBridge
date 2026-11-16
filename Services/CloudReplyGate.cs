using System.Net.WebSockets;

namespace QuasarBridge.Services;

public readonly record struct BufferedFrame(byte[] Data, WebSocketMessageType Type);

/// <summary>
/// After the user speaks (station→upstream Ogg), hold cloud→station frames briefly
/// so we can decide local vs passthrough BEFORE any TTS audio is played.
/// </summary>
public sealed class CloudReplyGate
{
    private readonly object _lock = new();
    private readonly List<BufferedFrame> _frames = new();
    private bool _buffering;
    private DateTimeOffset _deadline;
    private static readonly TimeSpan HoldFor = TimeSpan.FromMilliseconds(3200);
    private const int MaxBufferedFrames = 80;

    public void OnUserAudio()
    {
        lock (_lock)
        {
            if (!_buffering)
            {
                _buffering = true;
                _frames.Clear();
            }
            // Extend hold while the user is still talking; do not wipe cloud frames already held.
            _deadline = DateTimeOffset.UtcNow.Add(HoldFor);
        }
    }

    public bool IsBuffering
    {
        get { lock (_lock) return _buffering; }
    }

    /// <summary>
    /// Returns true if the frame was consumed by the gate (do not forward yet).
    /// When a flush is ready, <paramref name="flush"/> is set.
    /// </summary>
    public bool TryHold(byte[] data, WebSocketMessageType type, bool forceFlushHint, out List<BufferedFrame>? flush)
    {
        flush = null;
        lock (_lock)
        {
            if (!_buffering)
                return false;

            _frames.Add(new BufferedFrame(data, type));

            var expired = DateTimeOffset.UtcNow >= _deadline;
            var overflow = _frames.Count >= MaxBufferedFrames;
            if (forceFlushHint || expired || overflow)
            {
                flush = TakeFrames_NoLock();
                _buffering = false;
            }

            return true;
        }
    }

    public List<BufferedFrame>? TakeIfExpired()
    {
        lock (_lock)
        {
            if (!_buffering) return null;
            if (DateTimeOffset.UtcNow < _deadline && _frames.Count < MaxBufferedFrames)
                return null;
            _buffering = false;
            return TakeFrames_NoLock();
        }
    }

    private List<BufferedFrame> TakeFrames_NoLock()
    {
        var copy = _frames.ToList();
        _frames.Clear();
        return copy;
    }

    public static bool IsOgg(ReadOnlySpan<byte> payload) => MediaFrame.IsCloudAudio(payload);
}
