namespace QuasarBridge.Services;

public class ProxyState
{
    private long _totalConnections;
    private int _activeConnections;
    private long _messagesStationToUpstream;
    private long _messagesUpstreamToStation;
    private long _bytesStationToUpstream;
    private long _bytesUpstreamToStation;
    private DateTimeOffset? _lastConnectedAt;
    private DateTimeOffset? _lastDisconnectedAt;
    private string? _lastError;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public void OnConnected()
    {
        Interlocked.Increment(ref _totalConnections);
        Interlocked.Increment(ref _activeConnections);
        _lastConnectedAt = DateTimeOffset.Now;
    }

    public void OnDisconnected(string? error = null)
    {
        Interlocked.Decrement(ref _activeConnections);
        _lastDisconnectedAt = DateTimeOffset.Now;
        if (!string.IsNullOrWhiteSpace(error)) _lastError = error;
    }

    public void OnMessage(string direction, int bytes)
    {
        if (direction == "station->upstream")
        {
            Interlocked.Increment(ref _messagesStationToUpstream);
            Interlocked.Add(ref _bytesStationToUpstream, bytes);
        }
        else
        {
            Interlocked.Increment(ref _messagesUpstreamToStation);
            Interlocked.Add(ref _bytesUpstreamToStation, bytes);
        }
    }

    public object Snapshot(string listenUrl, string upstreamUrl, string uniWsPath) => new
    {
        startedAt = StartedAt,
        listenUrl,
        upstreamUrl,
        uniWsPath,
        activeConnections = Volatile.Read(ref _activeConnections),
        totalConnections = Interlocked.Read(ref _totalConnections),
        messagesStationToUpstream = Interlocked.Read(ref _messagesStationToUpstream),
        messagesUpstreamToStation = Interlocked.Read(ref _messagesUpstreamToStation),
        bytesStationToUpstream = Interlocked.Read(ref _bytesStationToUpstream),
        bytesUpstreamToStation = Interlocked.Read(ref _bytesUpstreamToStation),
        lastConnectedAt = _lastConnectedAt,
        lastDisconnectedAt = _lastDisconnectedAt,
        lastError = _lastError
    };
}