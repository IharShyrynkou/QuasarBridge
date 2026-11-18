namespace QuasarBridge.Services.Media;

public enum StationReplyMode
{
    Ignore,
    Passthrough,
    SilenceReadableText
}

/// <summary>Шаг 3 — что отправить на станцию.</summary>
public sealed record StationDeliveryPlan(
    StationReplyMode Mode,
    byte[] Payload,
    TimeSpan MuteFor,
    string Note);

public static class MediaStationDelivery
{
  /// <param name="upstreamReply">Кадр ответа облака (AAPI), если есть.</param>
    public static StationDeliveryPlan Plan(MediaTurn turn, byte[] upstreamReply)
    {
        upstreamReply ??= [];
        return turn.Kind switch
        {
            MediaTurnKind.Silence => new StationDeliveryPlan(
                StationReplyMode.SilenceReadableText,
                PayloadSilencer.BlankReadableSpeech(upstreamReply),
                TimeSpan.FromSeconds(12),
                turn.Note),
            MediaTurnKind.Passthrough => new StationDeliveryPlan(
                StationReplyMode.Passthrough,
                upstreamReply,
                TimeSpan.Zero,
                turn.Note),
            _ => new StationDeliveryPlan(
                StationReplyMode.Ignore,
                upstreamReply,
                TimeSpan.Zero,
                turn.Note)
        };
    }

    public static StationDeliveryPlan ForFlush(MediaTurn turn, byte[] frame) => Plan(turn, frame);
}