using System.Text.Json;
using QuasarBridge.Services;

namespace QuasarBridge.Services.Media;

public enum MediaTurnKind
{
    Ignore,
    Silence,
    Passthrough
}

public sealed record MediaTurn(MediaTurnKind Kind, string Note);

/// <summary>
/// One station websocket. Reads the recognized phrase, then either resumes the last
/// series/movie or asks which title to find in Jellyfin.
/// </summary>
public sealed class MediaSession
{
    private static readonly TimeSpan UtteranceGap = SpeechPause.Duration + TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AskFor = TimeSpan.FromSeconds(45);

    private readonly JellyfinLibrary _jellyfin;
    private readonly PotPlayerLauncher _player;
    private readonly CloudReplyGate _cloud;
    private readonly FreeLlmCommandDecoder _decoder;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<string> _tokens = new();

    private bool _utterance;
    private DateTimeOffset _lastAudio;
    private int _generation;
    private int _announcedGeneration = -1;
    private MediaTurn? _settled;
    private PendingAsk? _pending;
    private Task<Decoded>? _decode;
    private int _decodeGeneration = -1;
    private string? _decodeSignature;
    private CancellationTokenSource? _decodeCts;
    private Task<MediaTurn>? _apply;
    private int _applyGeneration = -1;
    private bool _pinHeld;
    private int _pinGeneration = -1;

    public MediaSession(
        JellyfinLibrary jellyfin,
        PotPlayerLauncher player,
        CloudReplyGate cloud,
        FreeLlmCommandDecoder decoder,
        ILogger logger)
    {
        _jellyfin = jellyfin;
        _player = player;
        _cloud = cloud;
        _decoder = decoder;
        _logger = logger;
        _tokens.Clear();
    }

    public static bool IsReply(ReadOnlySpan<byte> data) => AsrText.IsCloudReply(data);

    public bool HasSettled
    {
        get { lock (_gate) return _settled is not null; }
    }

    /// <summary>Returns true when this audio starts a new utterance (previous mute should drop).</summary>
    public bool OnUserAudio()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_utterance && now - _lastAudio < UtteranceGap)
            {
                _lastAudio = now;
                return false;
            }

            _utterance = true;
            _lastAudio = now;
            _tokens.Clear();
            _settled = null;
            _decodeCts?.Cancel();
            _decode = null;
            _decodeSignature = null;
            if (_pinHeld)
            {
                _cloud.UnpinDecode();
                _pinHeld = false;
            }
            _generation++;
            return true;
        }
    }

    public void Observe(byte[] data)
    {
        lock (_gate)
        {
            AsrText.Collect(data, _tokens);
            // Start while the phrase is still arriving, so the model overlaps the speech pause.
            if (_settled is not null || !AsrText.NeedsModel(_tokens))
                return;
            var signature = AsrText.JoinContent(_tokens);
            if (_decode is not null && _decodeGeneration == _generation && _decodeSignature == signature)
                return;
            StartDecode_NoLock(_tokens.ToList(), _generation, signature);
        }
    }

    /// <summary>True once speech has paused long enough to handle the phrase, or the turn is already done.</summary>
    public bool ShouldReleaseReply()
    {
        lock (_gate)
        {
            if (_settled is not null)
                return true;
            if (_tokens.Count == 0)
                return false;
            if (_pinHeld || _decode is { IsCompleted: false })
                return false;
            return UserSilent(SpeechPause.Duration);
        }
    }

    /// <summary>Шаг 1: фраза из накопленных ASR-токенов.</summary>
    public MediaPhrase? CapturePhrase(PhraseCaptureOptions options)
    {
        lock (_gate)
        {
            if (_settled is not null || _tokens.Count == 0)
                return null;
            if (options.RequireUserSilent && !UserSilent(SpeechPause.Duration))
                return null;

            if (_pending is { } wait && DateTimeOffset.UtcNow - wait.Since > AskFor)
                _pending = null;

            var snapshot = _tokens.ToList();
            if(snapshot.First() != null && snapshot.First() == "сериал")
            {
                _tokens.Remove(snapshot.First());
                snapshot.Remove(snapshot.First());
            }


            var generation = _generation;
            var needsModel = (_applyGeneration == generation && _apply is not null) || AsrText.NeedsModel(snapshot);

            if (options.LogWhenNew)
            {
                var announce = ClaimPhrase(generation, AsrText.Show(snapshot));
                if (announce is not null)
                    LogPhrase(announce);
            }

            PendingAskSnapshot? pendingSnap = _pending is null
                ? null
                : new PendingAskSnapshot(_pending.Kind, _pending.Since);

            return new MediaPhrase(
                generation,
                snapshot,
                Hypothesis(snapshot),
                AsrText.Show(snapshot),
                needsModel,
                pendingSnap);
        }
    }

    /// <summary>Шаг 2: распознать команду и выполнить (Jellyfin / PotPlayer).</summary>
    public async Task<MediaTurn> DecodeAsync(MediaPhrase phrase, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_settled is not null)
                return _settled;
            if (phrase.Generation != _generation)
                return new MediaTurn(MediaTurnKind.Ignore, "new utterance");
        }

        var pending = ToPending(phrase.Pending);
        var snapshot = phrase.Tokens.ToList();

        if (phrase.NeedsModel)
        {
            try
            {
                Task<MediaTurn>? apply;
                lock (_gate)
                    apply = _applyGeneration == phrase.Generation ? _apply : null;

                if (apply is not null)
                    return await apply;
                var decoded = await EnsureDecode(phrase.Generation, snapshot, ct);
                return await TurnAsync(phrase.Generation, decoded, snapshot, ct);
            }
            finally
            {
                ReleasePin(phrase.Generation);
            }
        }

        var outcome = await ResolveAsync(phrase.Hypothesis, pending, [], ct);
        return CommitOutcome(phrase.Generation, outcome);
    }

    /// <summary>
    /// Ранний запуск без ответа облака: после паузы в речи.
    /// </summary>
    public async Task<bool> TryActOnStableAsrAsync(CancellationToken ct)
    {
        var phrase = CapturePhrase(new PhraseCaptureOptions(RequireUserSilent: true, LogWhenNew: true));
        if (phrase is null)
            return false;

        if (!phrase.NeedsModel)
        {
            var preview = MediaIntents.Parse(phrase.Hypothesis);
            if (preview is null && phrase.Pending is not null
                && MediaIntents.ParseAnswer(phrase.Hypothesis).Kind is AnswerKind.Title or AnswerKind.Last)
                preview = new MediaIntent(MediaAct.PlayTitle, phrase.Pending.Kind, phrase.Hypothesis);
            if (preview is null)
                return false;
        }

        var turn = await DecodeAsync(phrase, ct);
        return turn.Kind == MediaTurnKind.Silence;
    }

  /// <summary>Ответ облака: шаг 1 + 2.</summary>
    public async Task<MediaTurn> DecideAsync(byte[] reply, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_settled is not null)
                return _settled;
        }

        var requireSilent = UserSilent(SpeechPause.Duration);
        var phrase = CapturePhrase(new PhraseCaptureOptions(requireSilent, LogWhenNew: requireSilent));
        if (phrase is null)
            return new MediaTurn(MediaTurnKind.Ignore, "no phrase");

        return await DecodeAsync(phrase, ct);
    }

    private static PendingAsk? ToPending(PendingAskSnapshot? snap) =>
        snap is null ? null : new PendingAsk(snap.Kind, snap.Since);

    private MediaTurn CommitOutcome(int generation, Outcome outcome)
    {
        lock (_gate)
        {
            if (generation != _generation)
                return new MediaTurn(MediaTurnKind.Ignore, "new utterance");
            if (_settled is not null)
                return _settled;
            if (outcome.ReplacePending)
                _pending = outcome.Pending;
            if (outcome.Turn.Kind != MediaTurnKind.Ignore && outcome.Settle)
            {
                _tokens.Clear();
                _settled = outcome.Turn;
            }

            if (outcome.Turn.Kind != MediaTurnKind.Ignore && outcome.Settle)
                _logger.LogInformation("команда: {note}", outcome.Turn.Note);
            return outcome.Turn;
        }
    }

    private Task<Decoded> EnsureDecode(int generation, List<string> tokens, CancellationToken ct)
    {
        lock (_gate)
        {
            var signature = AsrText.JoinContent(tokens);
            if (_decode is not null && _decodeGeneration == generation && _decodeSignature == signature)
                return _decode;
            StartDecode_NoLock(tokens, generation, signature);
            return _decode ?? Task.FromResult(new Decoded(false, MediaIntents.Parse(signature)));
        }
    }

    private void StartDecode_NoLock(List<string> tokens, int generation, string signature)
    {
        var previous = _decode;
        _decodeCts?.Cancel();
        _decodeCts = new CancellationTokenSource();
        _decodeGeneration = generation;
        _decodeSignature = signature;
        if (_pinGeneration != generation)
        {
            if (_pinHeld)
                _cloud.UnpinDecode();
            _cloud.PinDecode();
            _pinHeld = true;
            _pinGeneration = generation;
        }

        var cts = _decodeCts;
        _decode = CallModelAsync(tokens, cts.Token);
        if (previous is not null)
            _ = previous.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void ReleasePin(int generation)
    {
        var unpin = false;
        lock (_gate)
        {
            if (!_pinHeld || _pinGeneration != generation)
                return;
            _pinHeld = false;
            unpin = true;
        }

        if (unpin)
            _cloud.UnpinDecode();
    }

    private async Task<Decoded> CallModelAsync(List<string> tokens, CancellationToken ct)
    {
        var budget = _cloud.ModelTimeLeft(TimeSpan.FromSeconds(8));
        if (budget < TimeSpan.FromSeconds(2))
        {
            _logger.LogInformation("ии не зову, уже пора отдать ответ");
            return new Decoded(false, MediaIntents.Parse(AsrText.JoinContent(tokens)));
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(budget);
            var intent = await _decoder.DecodeAsync(tokens, timeout.Token);
            if (intent is null)
                _logger.LogInformation("ии: не команда");
            else
                _logger.LogInformation("ии: {summary}", Summary(intent));
            return new Decoded(true, intent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new Decoded(false, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ии не успела, разбираю токены локально");
            return new Decoded(false, MediaIntents.Parse(AsrText.JoinContent(tokens)));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "ии не разобрала токены, разбираю локально");
            return new Decoded(false, MediaIntents.Parse(AsrText.JoinContent(tokens)));
        }
    }

    private Task<MediaTurn> TurnAsync(int generation, Decoded decoded, List<string> tokens, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_apply is not null && _applyGeneration == generation)
                return _apply;
            _applyGeneration = generation;
            _apply = Task.Run(() => ApplyBodyAsync(generation, decoded, tokens, ct));
            return _apply;
        }
    }

    private async Task<MediaTurn> ApplyBodyAsync(int generation, Decoded decoded, List<string> tokens, CancellationToken ct)
    {
        var intent = decoded.Intent;
        if (!decoded.FromModel && intent is null)
            intent = MediaIntents.Parse(AsrText.JoinContent(tokens));

        if (intent is null)
        {
            lock (_gate)
            {
                if (generation != _generation)
                    return new MediaTurn(MediaTurnKind.Ignore, "new utterance");
                if (_settled is not null)
                    return _settled;
                if (decoded.FromModel)
                {
                    _tokens.Clear();
                    _settled = new MediaTurn(MediaTurnKind.Passthrough, "не команда");
                    return _settled;
                }

                return new MediaTurn(MediaTurnKind.Ignore, "");
            }
        }

        var outcome = await ExecuteAsync(intent, ct);
        lock (_gate)
        {
            if (generation != _generation)
                return new MediaTurn(MediaTurnKind.Ignore, "new utterance");
            if (_settled is not null)
                return _settled;
            if (!outcome.Settle || outcome.Turn.Kind == MediaTurnKind.Ignore)
                return outcome.Turn;
            if (outcome.ReplacePending)
                _pending = outcome.Pending;
            _tokens.Clear();
            _settled = outcome.Turn;
            _logger.LogInformation("команда: {note}", outcome.Turn.Note);
            return _settled;
        }
    }

    private static string Hypothesis(List<string> tokens)
    {
        var text = AsrText.JoinContent(tokens);
        return string.IsNullOrWhiteSpace(text) ? AsrText.Join(tokens) ?? "" : text;
    }

    private static string Summary(MediaIntent intent) => intent.Act switch
    {
        MediaAct.PlayTitle => $"включить {(intent.Kind == MediaKind.Movie ? "фильм" : "сериал")} «{intent.Title}»",
        MediaAct.ContinueLast => $"включить последний {(intent.Kind == MediaKind.Movie ? "фильм" : "сериал")}",
        MediaAct.Close => "выключить",
        MediaAct.AskTitle => "уточнить название",
        _ => intent.Act.ToString()
    };

    private async Task<Outcome> ResolveAsync(string hypothesis, PendingAsk? pending, byte[] reply, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hypothesis))
            return Outcome.Ignore;

        var intent = MediaIntents.Parse(hypothesis);
        if (intent is null && pending is not null)
        {
            var answer = MediaIntents.ParseAnswer(hypothesis);
            switch (answer.Kind)
            {
                case AnswerKind.Empty:
                    return Outcome.Ignore;
                case AnswerKind.Cancel:
                    return Outcome.Pass("отмена", pending: null);
                case AnswerKind.Release:
                    return Outcome.IgnoreWithPending(null, "другая команда");
                case AnswerKind.Last:
                    intent = new MediaIntent(MediaAct.ContinueLast, answer.KindOverride ?? pending.Kind, null);
                    break;
                case AnswerKind.Title:
                    intent = new MediaIntent(MediaAct.PlayTitle, answer.KindOverride ?? pending.Kind, answer.Title);
                    break;
            }
        }

        return await ExecuteAsync(intent, ct);
    }

    private async Task<Outcome> ExecuteAsync(MediaIntent? intent, CancellationToken ct)
    {
        if (intent is null)
            return Outcome.Ignore;

        if (!UserSilent(SpeechPause.Duration))
            return Outcome.Ignore;

        if (intent.Act == MediaAct.Close)
            return await ClosePlaybackAsync(ct);
        if (intent.Act == MediaAct.Pause)
            return await PausePlaybackAsync(ct);
        if (intent.Act == MediaAct.Seek)
            return SeekPlayback(intent.SeekSeconds);
        if (intent.Act is MediaAct.NextEpisode or MediaAct.PreviousEpisode or MediaAct.NextSeason)
            return await ShiftPlaybackAsync(intent.Act, ct);

        if (intent.Act == MediaAct.AskTitle)
            intent = new MediaIntent(MediaAct.ContinueLast, intent.Kind, null);

        try
        {
            var searchTitle = MediaIntents.CompactSearchTitle(intent.Title);
            var item = intent.Act == MediaAct.ContinueLast
                ? await _jellyfin.FindLastAsync(intent.Kind, ct)
                : await _jellyfin.FindByTitleAsync(intent.Kind, searchTitle, ct);
            if (item is null)
            {
                var missing = intent.Act == MediaAct.ContinueLast
                    ? "нечего продолжить"
                    : intent.Kind == MediaKind.Series ? $"сериал не найден: {intent.Title}" : $"фильм не найден: {intent.Title}";
                return Outcome.Quiet(missing, pending: null);
            }

            _player.Play(item);
            var where = string.IsNullOrEmpty(item.Detail) ? item.Title : $"{item.Title}, {item.Detail}";
            return Outcome.Quiet($"play {where}", pending: null);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Файл Jellyfin недоступен");
            return Outcome.Quiet("file missing", pending: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось включить {kind}", intent.Kind);
            return Outcome.Quiet(ex.Message, pending: null);
        }
    }

    private async Task<Outcome> PausePlaybackAsync(CancellationToken ct)
    {
        try
        {
            var current = _player.ReadCurrent();
            try
            {
                var saved = await _jellyfin.SavePositionAsync(current.FileName, current.PositionSeconds, ct);
                _player.Pause();
                var where = string.IsNullOrEmpty(saved.Detail) ? saved.Title : $"{saved.Title}, {saved.Detail}";
                return Outcome.Quiet($"paused {where} at {saved.PositionSeconds}s", pending: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Не удалось запомнить позицию, плеер всё равно поставлю на паузу");
                _player.Pause();
                return Outcome.Quiet($"paused without jellyfin: {ex.Message}", pending: null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось поставить на паузу");
            return Outcome.Quiet(ex.Message, pending: null);
        }
    }

    private Outcome SeekPlayback(int deltaSeconds)
    {
        try
        {
            var move = _player.SeekBy(TimeSpan.FromSeconds(deltaSeconds));
            var note = move.AtStart ? "seek start"
                : move.AtEnd ? "seek end"
                : $"seek {(deltaSeconds < 0 ? "назад" : "вперёд")} {Math.Abs(deltaSeconds)}s";
            return Outcome.Quiet(note, pending: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось перемотать");
            return Outcome.Quiet(ex.Message, pending: null);
        }
    }

    private async Task<Outcome> ShiftPlaybackAsync(MediaAct act, CancellationToken ct)
    {
        try
        {
            string fileName;
            try
            {
                fileName = _player.ReadCurrent().FileName;
            }
            catch (InvalidOperationException)
            {
                var last = await _jellyfin.FindLastAsync(MediaKind.Series, ct);
                if (last is null || string.IsNullOrWhiteSpace(last.Path))
                    return Outcome.Quiet("нечего переключать", pending: null);
                fileName = Path.GetFileName(last.Path);
            }

            var shift = act switch
            {
                MediaAct.PreviousEpisode => EpisodeShift.Previous,
                MediaAct.NextSeason => EpisodeShift.NextSeason,
                _ => EpisodeShift.Next
            };
            var item = await _jellyfin.ShiftEpisodeAsync(fileName, shift, ct);
            _player.Play(item);
            var where = string.IsNullOrEmpty(item.Detail) ? item.Title : $"{item.Title}, {item.Detail}";
            return Outcome.Quiet($"play {where}", pending: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось переключить серию");
            return Outcome.Quiet(ex.Message, pending: null);
        }
    }

    private async Task<Outcome> ClosePlaybackAsync(CancellationToken ct)
    {
        try
        {
            var current = _player.ReadCurrent();
            try
            {
                var saved = await _jellyfin.SavePositionAsync(current.FileName, current.PositionSeconds, ct);
                _player.Close();
                var where = string.IsNullOrEmpty(saved.Detail) ? saved.Title : $"{saved.Title}, {saved.Detail}";
                return Outcome.Quiet($"closed {where} at {saved.PositionSeconds}s", pending: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Не удалось запомнить позицию, плеер всё равно закрою");
                _player.Close();
                return Outcome.Quiet($"closed without jellyfin: {ex.Message}", pending: null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось закрыть плеер");
            return Outcome.Quiet(ex.Message, pending: null);
        }
    }

    private string? ClaimPhrase(int generation, string hypothesis)
    {
        if (_announcedGeneration == generation || string.IsNullOrWhiteSpace(hypothesis))
            return null;
        _announcedGeneration = generation;
        return hypothesis;
    }

    private void LogPhrase(string phrase) =>
        _logger.LogInformation("\u001b[1;93mфраза: «{phrase}»\u001b[0m", phrase);

    private bool UserSilent(TimeSpan duration)
    {
        if (_lastAudio == DateTimeOffset.MinValue)
            return false;
        return DateTimeOffset.UtcNow - _lastAudio >= duration;
    }

    private readonly record struct Decoded(bool FromModel, MediaIntent? Intent);

    private sealed record PendingAsk(MediaKind Kind, DateTimeOffset Since);

    private readonly record struct Outcome(MediaTurn Turn, bool ReplacePending, PendingAsk? Pending, bool Settle)
    {
        public static Outcome Ignore { get; } = new(new MediaTurn(MediaTurnKind.Ignore, ""), false, null, false);

        public static Outcome IgnoreWithPending(PendingAsk? pending, string note) =>
            new(new MediaTurn(MediaTurnKind.Ignore, note), true, pending, false);

        public static Outcome Pass(string note, PendingAsk? pending) =>
            new(new MediaTurn(MediaTurnKind.Passthrough, note), true, pending, true);

        public static Outcome Quiet(string note, PendingAsk? pending, bool settle = true) =>
            new(new MediaTurn(MediaTurnKind.Silence, note), true, pending, settle);
    }
}
