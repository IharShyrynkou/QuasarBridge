using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;
using QuasarBridge.Options;
using QuasarBridge.Services.Media;

namespace QuasarBridge.Services;

public class UniProxyService
{
    private readonly ProxyState _state;
    private readonly IOptionsMonitor<QuasarBridgeOptions> _options;
    private readonly UpstreamDnsResolver _dns;
    private readonly MessageRouter _router;
    private readonly LocalCommandHandler _local;
    private readonly JellyfinLibrary _jellyfin;
    private readonly PotPlayerLauncher _player;
    private readonly FreeLlmCommandDecoder _decoder;
    private readonly PreparedSpeech _speech;
    private readonly ILogger<UniProxyService> _logger;
    private int _connIdSeq;

    public UniProxyService(
        ProxyState state,
        IOptionsMonitor<QuasarBridgeOptions> options,
        UpstreamDnsResolver dns,
        MessageRouter router,
        LocalCommandHandler local,
        JellyfinLibrary jellyfin,
        PotPlayerLauncher player,
        FreeLlmCommandDecoder decoder,
        PreparedSpeech speech,
        ILogger<UniProxyService> logger)
    {
        _state = state;
        _options = options;
        _dns = dns;
        _router = router;
        _local = local;
        _jellyfin = jellyfin;
        _player = player;
        _decoder = decoder;
        _speech = speech;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("Expected WebSocket at /uni.ws");
            return;
        }

        var opts = _options.CurrentValue;
        var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (opts.AllowedClientIps.Length > 0 &&
            !opts.AllowedClientIps.Contains(remote) &&
            !opts.AllowedClientIps.Contains(ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? ""))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync("forbidden");
            return;
        }

        var id = Interlocked.Increment(ref _connIdSeq);
        var upstreamUrl = opts.UpstreamUrl;
        _logger.LogInformation("[{id}] station connected from {remote} sni={sni} mode={mode}",
            id, remote, ctx.Request.Host.Host, opts.Mode);
        _state.OnConnected();

        using var stationWs = await ctx.WebSockets.AcceptWebSocketAsync();
        using var upstreamWs = new ClientWebSocket();
        upstreamWs.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        foreach (var header in ctx.Request.Headers)
        {
            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                try { upstreamWs.Options.SetRequestHeader(header.Key, header.Value.ToString()); } catch { }
            }
        }

        string? error = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            await ConnectUpstreamAsync(upstreamWs, upstreamUrl, opts, cts.Token);
            _logger.LogInformation("[{id}] upstream connected {upstream}", id, upstreamUrl);

            var mute = new SessionMuteGate();
            var gate = new CloudReplyGate();
            var media = new MediaSession(_jellyfin, _player, gate, _decoder, _logger);
            var toUpstream = PumpAsync(id, "station->upstream", stationWs, upstreamWs, cts, intercept: true, mute, gate, media);
            var toStation = PumpAsync(id, "upstream->station", upstreamWs, stationWs, cts, intercept: true, mute, gate, media);
            var done = await Task.WhenAny(toUpstream, toStation);
            try { cts.Cancel(); } catch { }
            await Task.WhenAll(Safe(toUpstream), Safe(toStation));
            _logger.LogInformation("[{id}] session ended ({reason})", id, done.IsFaulted ? "fault" : "closed");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "[{id}] proxy error", id);
            try
            {
                if (stationWs.State == WebSocketState.Open)
                    await stationWs.CloseAsync(WebSocketCloseStatus.InternalServerError, "proxy error", CancellationToken.None);
            }
            catch { }
        }
        finally
        {
            _state.OnDisconnected(error);
            _logger.LogInformation("[{id}] disconnected", id);
        }
    }

    private async Task ConnectUpstreamAsync(ClientWebSocket ws, string upstreamUrl, QuasarBridgeOptions opts, CancellationToken ct)
    {
        var uri = new Uri(upstreamUrl);
        if (!opts.ResolveUpstreamViaDoh || !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            await ws.ConnectAsync(uri, ct);
            return;
        }

        var ips = await _dns.ResolveAsync(uri.Host, ct);
        var ip = ips[0];
        using var invoker = CreateSocketsHttpHandlerInvoker(ip);
        await ws.ConnectAsync(uri, invoker, ct);
        _logger.LogInformation("Upstream TCP via {ip} host/SNI={sni}", ip, uri.Host);
    }

    private static HttpMessageInvoker CreateSocketsHttpHandlerInvoker(IPAddress ip)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpMessageInvoker(handler);
    }

    private async Task PumpAsync(
        int id,
        string dir,
        WebSocket src,
        WebSocket dst,
        CancellationTokenSource cts,
        bool intercept,
        SessionMuteGate mute,
        CloudReplyGate gate,
        MediaSession media)
    {
        var buf = new byte[64 * 1024];
        var toStation = string.Equals(dir, "upstream->station", StringComparison.Ordinal);
        var toUpstream = string.Equals(dir, "station->upstream", StringComparison.Ordinal);
        try
        {
            while (!cts.IsCancellationRequested && src.State == WebSocketState.Open)
            {
                // Time-based flush of held cloud reply.
                if (toStation)
                {
                    var expired = gate.TakeIfExpired();
                    if (expired is { Count: > 0 })
                        await FlushCloudReplyAsync(id, dst, expired, mute, media, cts.Token);
                }

                using var ms = new MemoryStream();
                ValueWebSocketReceiveResult result;
                // Do not cancel ReceiveAsync — that aborts the socket and the station reports «нет интернета».
                var receiveTask = src.ReceiveAsync(buf.AsMemory(), cts.Token).AsTask();
                while (toStation && gate.IsBuffering && !receiveTask.IsCompleted)
                {
                    var wake = await Task.WhenAny(receiveTask, Task.Delay(200, cts.Token));
                    if (await media.TryActOnStableAsrAsync(cts.Token))
                        mute.Mute(TimeSpan.FromSeconds(8));
                    // A finished command must release the held reply now, or the station waits 12s and says «нет интернета».
                    var expired = gate.Take(force: media.HasSettled);
                    if (expired is { Count: > 0 })
                        await FlushCloudReplyAsync(id, dst, expired, mute, media, cts.Token);
                    if (wake == receiveTask || !gate.IsBuffering)
                        break;
                }

                result = await receiveTask;

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        if (dst.State is WebSocketState.Open or WebSocketState.CloseReceived)
                            await dst.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                    catch { }
                    try { cts.Cancel(); } catch { }
                    _logger.LogInformation("[{id}] {dir} close", id, dir);
                    return;
                }

                if (result.Count > 0) ms.Write(buf, 0, result.Count);
                while (!result.EndOfMessage)
                {
                    result = await src.ReceiveAsync(buf.AsMemory(), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try { cts.Cancel(); } catch { }
                        return;
                    }

                    if (result.Count > 0) ms.Write(buf, 0, result.Count);
                }

                var data = ms.ToArray();
                var msgType = result.MessageType;

                if (toUpstream && _speech.ClaimMic(data, out var questionAudio))
                {
                    if (questionAudio is not null)
                    {
                        var asked = media.OnUserAudio();
                        if (asked)
                        {
                            mute.Unmute();
                            _logger.LogInformation("[{id}] речь отправлена на сервер", id);
                        }
                        gate.OnUserAudio();
                        _logger.LogInformation("[{id}] вопрос алисе подменён, {len} байт", id, questionAudio.Length);
                        await SendAsync(id, dir, dst, questionAudio, msgType, cts.Token);
                    }
                    continue;
                }

                // User started/continued speaking → hold the next cloud reply before any TTS plays.
                if (toUpstream && CloudReplyGate.IsOgg(data))
                {
                    var fresh = media.OnUserAudio();
                    if (fresh)
                    {
                        mute.Unmute();
                        _logger.LogInformation("[{id}] речь отправлена на сервер", id);
                    }
                    // Trailing mic audio after a handled command must not hold Alice's reply again.
                    if (fresh || !media.HasSettled)
                        gate.OnUserAudio();
                }

                // ASR hypotheses can show up either way; collect both (skip raw Opus).
                if (!MediaFrame.IsCloudAudio(data))
                {
                    media.Observe(data);
                    if (await media.TryActOnStableAsrAsync(cts.Token))
                        mute.Mute(TimeSpan.FromSeconds(8));
                }

                if (toStation && mute.IsMuted && !_speech.ProtectReply)
                {
                    // Drop ALL cloud audio for this turn (Ogg may be length-prefixed).
                    if (MediaFrame.IsCloudAudio(data) || (!MediaFrame.IsAapi(data) && data.Length > 128))
                    {
                        _state.OnMessage(dir + ":mute-audio", data.Length);
                        _logger.LogDebug("[{id}] dropped cloud audio/non-aapi {len}B during mute", id, data.Length);
                        continue;
                    }

                    // Kill follow-up speak directives / leftover phrases in this turn.
                    if (ContainsSpeakDirective(data))
                        data = PayloadSilencer.SilenceAliceReply(data);
                }

                if (toStation && gate.IsBuffering)
                {
                    // Release only after the speech pause, so the phrase is complete.
                    var forceFlush = !CloudReplyGate.IsOgg(data)
                        && LooksLikeAliceSpeechFrame(data)
                        && media.ShouldReleaseReply();
                    if (gate.TryHold(data, msgType, forceFlush, out var flush))
                    {
                        if (flush is { Count: > 0 })
                            await FlushCloudReplyAsync(id, dst, flush, mute, media, cts.Token);
                        continue;
                    }
                }

                if (toStation && MediaSession.IsReply(data))
                {
                    var turn = await media.DecideAsync(data, cts.Token);
                    if (turn.Kind == MediaTurnKind.Silence)
                    {
                        data = PayloadSilencer.BlankReadableSpeech(data);
                        mute.Mute(TimeSpan.FromSeconds(12));
                        _logger.LogDebug("[{id}] media silence: {note}", id, turn.Note);
                        await SendAsync(id, dir, dst, data, msgType, cts.Token);
                        continue;
                    }

                    if (turn.Kind == MediaTurnKind.Passthrough)
                    {
                        mute.Unmute();
                        _logger.LogDebug("[{id}] media passthrough: {note}", id, turn.Note);
                        await SendAsync(id, dir, dst, data, msgType, cts.Token);
                        continue;
                    }
                }

                if (intercept)
                {
                    var decision = _router.Decide(dir, data, msgType == WebSocketMessageType.Text);
                    if (decision.Route == MessageRoute.Drop)
                    {
                        _state.OnMessage(dir + ":drop", data.Length);
                        continue;
                    }

                    if (decision.Route == MessageRoute.Local)
                    {
                        var local = await _local.HandleAsync(decision, data, cts.Token);
                        if (local.Action == LocalAction.ForwardSilenced && local.ReplacementPayload is not null)
                        {
                            data = local.ReplacementPayload;
                            mute.Mute(TimeSpan.FromSeconds(12));
                            _logger.LogInformation("[{id}] local silenced + audio-mute 12s rule={rule}", id, decision.Rule);
                        }
                        else if (local.Action == LocalAction.Drop)
                        {
                            mute.Mute(TimeSpan.FromSeconds(12));
                            continue;
                        }
                    }
                }

                await SendAsync(id, dir, dst, data, msgType, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogInformation("[{id}] {dir} websocket ended: {msg}", id, dir, ex.Message);
            try { cts.Cancel(); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{id}] {dir} pump error", id, dir);
            try { cts.Cancel(); } catch { }
        }
    }

    private async Task FlushCloudReplyAsync(
        int id,
        WebSocket stationWs,
        List<BufferedFrame> frames,
        SessionMuteGate mute,
        MediaSession media,
        CancellationToken ct)
    {
        _logger.LogInformation("[{id}] ответ сервера, кадров {count}", id, frames.Count);

        MediaTurn? mediaTurn = null;
        foreach (var frame in frames)
        {
            if (!MediaSession.IsReply(frame.Data))
                continue;
            mediaTurn = await media.DecideAsync(frame.Data, ct);
            break;
        }

        if (mediaTurn is { Kind: MediaTurnKind.Silence })
        {
            mute.Mute(TimeSpan.FromSeconds(12));
            _logger.LogDebug("[{id}] media flush silence: {note} frames={count}", id, mediaTurn.Note, frames.Count);
            foreach (var frame in frames)
            {
                if (MediaFrame.IsCloudAudio(frame.Data))
                {
                    _state.OnMessage("upstream->station:drop-ogg", frame.Data.Length);
                    continue;
                }

                var silenced = PayloadSilencer.BlankReadableSpeech(frame.Data);
                await SendAsync(id, "upstream->station:silenced", stationWs, silenced, frame.Type, ct);
            }

            return;
        }

        if (mediaTurn is { Kind: MediaTurnKind.Passthrough })
        {
            mute.Unmute();
            _logger.LogDebug("[{id}] media flush passthrough: {note} frames={count}", id, mediaTurn.Note, frames.Count);
            foreach (var frame in frames)
                await SendAsync(id, "upstream->station", stationWs, frame.Data, frame.Type, ct);
            return;
        }

        RouteDecision? localHit = null;
        foreach (var frame in frames)
        {
            if (CloudReplyGate.IsOgg(frame.Data)) continue;
            var decision = _router.Decide("upstream->station", frame.Data, frame.Type == WebSocketMessageType.Text);
            if (decision.Route == MessageRoute.Local)
            {
                localHit = decision;
                break;
            }
        }

        if (localHit is null)
        {
            _logger.LogDebug("[{id}] cloud reply passthrough frames={count}", id, frames.Count);
            foreach (var frame in frames)
                await SendAsync(id, "upstream->station", stationWs, frame.Data, frame.Type, ct);
            return;
        }

        _logger.LogDebug("[{id}] cloud reply LOCAL rule={rule} frames={count} → silenced AAPI, drop Ogg",
            id, localHit.Rule, frames.Count);

        // Run side effect once (PotPlayer etc.) on the matching payload.
        var matchFrame = frames.First(f => !CloudReplyGate.IsOgg(f.Data) &&
            _router.Decide("upstream->station", f.Data, f.Type == WebSocketMessageType.Text).Route == MessageRoute.Local);
        await _local.HandleAsync(localHit, matchFrame.Data, ct);
        mute.Mute(TimeSpan.FromSeconds(8));

        foreach (var frame in frames)
        {
            if (MediaFrame.IsCloudAudio(frame.Data))
            {
                _state.OnMessage("upstream->station:drop-ogg", frame.Data.Length);
                _logger.LogDebug("[{id}] flush: drop cloud audio {len}B", id, frame.Data.Length);
                continue; // silence: never play cloud TTS for this turn
            }

            var silenced = PayloadSilencer.BlankReadableSpeech(frame.Data);
            await SendAsync(id, "upstream->station:silenced", stationWs, silenced, frame.Type, ct);
        }
    }

    private static bool LooksLikeAliceSpeechFrame(byte[] data)
    {
        if (CloudReplyGate.IsOgg(data) || data.Length < 64) return false;
        try
        {
            var s = Encoding.UTF8.GetString(data);
            if (ContainsSpeakDirective(data)) return true;
            if (s.Contains("simple_text", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Contains("сериал", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Contains("экран", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Contains("интернет", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var ch in s)
                if (ch is >= '\u0400' and <= '\u04FF') return true;
        }
        catch { }
        return false;
    }

    private static bool ContainsSpeakDirective(byte[] data)
    {
        var needle = "tts_play_placeholder"u8;
        if (data.Length < needle.Length) return false;
        return data.AsSpan().IndexOf(needle) >= 0;
    }

    private async Task SendAsync(int id, string dir, WebSocket dst, byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        if (dst.State != WebSocketState.Open) return;
        if (dir.StartsWith("upstream->station", StringComparison.Ordinal))
        {
            _speech.Note(data);
            if (_speech.ClaimSpeaker(data, out var spoken))
            {
                if (spoken is null)
                {
                    _state.OnMessage(dir + ":drop-cloud-voice", data.Length);
                    return;
                }

                var stream = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(spoken);
                _logger.LogInformation("[{id}] алиса говорит подготовленной речью, поток {stream}, {len} байт", id, stream, spoken.Length);
                data = spoken;
            }
        }
        await dst.SendAsync(data, type, true, ct);
        _state.OnMessage(dir, data.Length);
        LogPayload(id, dir, type, data);
    }

    private void LogPayload(int id, string dir, WebSocketMessageType type, byte[] data)
    {
        try
        {
            if (type == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(data);
                _logger.LogDebug("[{id}] {dir} TEXT {len}B: {payload}", id, dir, data.Length, Truncate(text.Replace("\r", "\\r").Replace("\n", "\\n"), 4000));
            }
            else
            {
                var asText = TryDecodeText(data);
                if (asText != null)
                    _logger.LogDebug("[{id}] {dir} BINARY {len}B (utf8): {payload}", id, dir, data.Length, Truncate(asText.Replace("\r", "\\r").Replace("\n", "\\n"), 4000));
                else
                    _logger.LogDebug("[{id}] {dir} BINARY {len}B hex[0..128]: {hex}", id, dir, data.Length, ToHexPreview(data, 128));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{id}] {dir} payload log failed ({len}B)", id, dir, data.Length);
        }
    }

    private static string? TryDecodeText(byte[] data)
    {
        try
        {
            var s = Encoding.UTF8.GetString(data);
            int printable = 0, total = 0;
            foreach (var ch in s)
            {
                total++;
                if (!char.IsControl(ch) || ch is '\n' or '\r' or '\t') printable++;
            }
            if (total == 0) return s;
            return (printable * 100 / total) >= 85 ? s : null;
        }
        catch { return null; }
    }

    private static string ToHexPreview(byte[] data, int maxBytes)
    {
        var n = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder(n * 2);
        for (int i = 0; i < n; i++) sb.Append(data[i].ToString("x2"));
        if (data.Length > n) sb.Append("...");
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + $"...(+{s.Length - max} chars)";

    private static async Task Safe(Task t) { try { await t; } catch { } }
}
