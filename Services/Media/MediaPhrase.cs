namespace QuasarBridge.Services.Media;

/// <summary>Параметры шага «собрать фразу».</summary>
public readonly record struct PhraseCaptureOptions(
    bool RequireUserSilent,
    bool LogWhenNew);

/// <summary>Шаг 1 — итог сбора ASR.</summary>
public sealed record MediaPhrase(
    int Generation,
    IReadOnlyList<string> Tokens,
    string Hypothesis,
    string Display,
    bool NeedsModel,
    PendingAskSnapshot? Pending);

public sealed record PendingAskSnapshot(MediaKind Kind, DateTimeOffset Since);