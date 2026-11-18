using System.Net.WebSockets;
using QuasarBridge.Services.Media;

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
    private DateTimeOffset _started;
    private static readonly TimeSpan HoldFor = SpeechPause.Duration;
    private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DecodeCap = TimeSpan.FromSeconds(11);
    private const int MaxBufferedFrames = 80;
    private int _decodePins;

    public void OnUserAudio()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_buffering)
            {
                _buffering = true;
                _frames.Clear();
                _started = now;
            }

            // Extend while the user is still talking, but never long enough for the station ASR timeout.
            var next = now.Add(HoldFor);
            var cap = _started.Add(MaxHold);
            _deadline = next < cap ? next : cap;
        }
    }

    public bool IsBuffering
    {
        get { lock (_lock) return _buffering; }
    }

    /// <summary>Hold the cloud reply while a model is reading the tokens. Capped so the station does not hit its 12s timeout.</summary>
    public void PinDecode()
    {
        lock (_lock)
        {
            _decodePins++;
            if (!_buffering)
                return;
            var cap = _started.Add(DecodeCap);
            var next = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(4));
            if (next > cap)
                next = cap;
            if (next > _deadline)
                _deadline = next;
        }
    }

    public void UnpinDecode()
    {
        lock (_lock)
        {
            if (_decodePins > 0)
                _decodePins--;
        }
    }

    /// <summary>How long a model call may run without pushing the held reply into the station's 12s timeout.</summary>
    public TimeSpan ModelTimeLeft(TimeSpan wanted)
    {
        lock (_lock)
        {
            if (!_buffering)
                return wanted;
            var left = _started.Add(DecodeCap) - DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(400);
            if (left < TimeSpan.FromSeconds(2))
                return TimeSpan.Zero;
            return left < wanted ? left : wanted;
        }
    }

    private bool DecodeHolds() =>
        _decodePins > 0 && DateTimeOffset.UtcNow < _started.Add(DecodeCap);

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
            var held = DecodeHolds();
            if (overflow || (!held && (forceFlushHint || expired)))
            {
                flush = TakeFrames_NoLock();
                _buffering = false;
            }

            return true;
        }
    }

    public List<BufferedFrame>? TakeIfExpired() => Take(force: false);

    /// <summary>Release whatever is held. <paramref name="force"/> skips the silence deadline.</summary>
    public List<BufferedFrame>? Take(bool force)
    {
        lock (_lock)
        {
            if (!_buffering) return null;
            var overflow = _frames.Count >= MaxBufferedFrames;
            if (!force && !overflow && (DecodeHolds() || DateTimeOffset.UtcNow < _deadline))
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
