using System.Buffers.Binary;

namespace QuasarBridge.Services;

/// <summary>
/// One shot: the next microphone burst is replaced with a question,
/// and the cloud voice that answers it is replaced with prepared speech.
/// </summary>
public sealed class PreparedSpeech
{
    private byte[]? _question;
    private byte[]? _answer;
    private long _deadline;
    private int _questionSent;
    private int _answerSent;
    private int _streamId = -1;
    private long _dropUntil;

    public void Arm(byte[] question, byte[] answer)
    {
        Volatile.Write(ref _answer, answer);
        Volatile.Write(ref _question, question);
        Interlocked.Exchange(ref _answerSent, 0);
        Interlocked.Exchange(ref _questionSent, 0);
        Volatile.Write(ref _streamId, -1);
        Volatile.Write(ref _dropUntil, 0);
        Volatile.Write(ref _deadline, Environment.TickCount64 + 20_000);
    }

    public void Note(byte[] data)
    {
        var id = OpusSpeech.SpeakerStream(data);
        if (id is int stream)
            Volatile.Write(ref _streamId, stream);
    }

    public void Disarm()
    {
        Volatile.Write(ref _question, null);
        Volatile.Write(ref _answer, null);
        Interlocked.Exchange(ref _questionSent, 0);
        Interlocked.Exchange(ref _answerSent, 0);
    }

    public bool ProtectReply =>
        Volatile.Read(ref _questionSent) == 1 && Environment.TickCount64 < Volatile.Read(ref _dropUntil);

    /// <summary>True when this microphone frame is part of the staged question. <paramref name="send"/> is the replacement, or null to drop.</summary>
    public bool ClaimMic(byte[] data, out byte[]? send)
    {
        send = null;
        var question = Volatile.Read(ref _question);
        if (question is null || !MediaFrame.IsCloudAudio(data))
            return false;
        if (Volatile.Read(ref _questionSent) == 0 && Environment.TickCount64 > Volatile.Read(ref _deadline))
        {
            Disarm();
            return false;
        }

        if (Interlocked.Exchange(ref _questionSent, 1) == 0)
        {
            send = question;
            Volatile.Write(ref _dropUntil, Environment.TickCount64 + 12_000);
            return true;
        }

        if (Environment.TickCount64 < Volatile.Read(ref _dropUntil))
            return true;

        return false;
    }

    /// <summary>True when this cloud-audio frame belongs to the staged answer.</summary>
    public bool ClaimSpeaker(byte[] data, out byte[]? send)
    {
        send = null;
        if (Volatile.Read(ref _questionSent) == 0 || Volatile.Read(ref _answer) is null)
            return false;
        if (!MediaFrame.IsCloudAudio(data))
            return false;
        if (Environment.TickCount64 > Volatile.Read(ref _dropUntil))
        {
            Disarm();
            return false;
        }

        if (Interlocked.Exchange(ref _answerSent, 1) == 0)
        {
            var payload = (byte[])Volatile.Read(ref _answer)!.Clone();
            var stream = Volatile.Read(ref _streamId);
            if (stream >= 0)
                BinaryPrimitives.WriteInt32BigEndian(payload, stream);
            send = payload;
            Volatile.Write(ref _dropUntil, Environment.TickCount64 + 8_000);
            return true;
        }

        return true;
    }
}
