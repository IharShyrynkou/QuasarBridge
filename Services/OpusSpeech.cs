using System.Buffers.Binary;
using Concentus.Enums;
using Concentus.Structs;

namespace QuasarBridge.Services;

/// <summary>
/// WAV → one Quasar audio message: 4-byte stream tag + Ogg Opus.
/// Tag 1 is the microphone (16 kHz). Tag 0x16 is Alice's speaker (48 kHz).
/// </summary>
public static class OpusSpeech
{
    public const int MicTag = 0x01;
    public const int SpeakerTag = 0x16;
    private static ReadOnlySpan<byte> SpeakerMark => "r\f\n\naudio/opus"u8;

    /// <summary>The byte in front of the opus marker is the stream id the player is waiting for.</summary>
    public static int? SpeakerStream(ReadOnlySpan<byte> data)
    {
        var at = data.IndexOf(SpeakerMark);
        if (at <= 0)
            return null;
        return data[at - 1];
    }

    private static readonly uint[] Crc = BuildCrc();

    public static byte[] Pack(byte[] wav, int sampleRate, int streamTag, int leadMs, int tailMs)
    {
        var (pcm, rate) = ReadPcm16(wav);
        pcm = Resample(pcm, rate, sampleRate);
        pcm = WithSilence(pcm, sampleRate, leadMs, tailMs);

        var frame = sampleRate / 50;
        var encoder = new OpusEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 24000;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        var preskip = encoder.Lookahead;

        var packets = new List<byte[]>();
        var packet = new byte[400];
        for (var offset = 0; offset < pcm.Length; offset += frame)
        {
            var slice = new short[frame];
            var n = Math.Min(frame, pcm.Length - offset);
            Array.Copy(pcm, offset, slice, 0, n);
            var written = encoder.Encode(slice, 0, frame, packet, 0, packet.Length);
            if (written <= 0)
                throw new InvalidOperationException("Opus не собрал кадр");
            packets.Add(packet[..written]);
        }

        if (packets.Count == 0)
            throw new InvalidOperationException("Пустая речь");

        var serial = Random.Shared.Next(1, int.MaxValue);
        using var body = new MemoryStream();
        var seq = 0;
        WritePage(body, serial, seq++, 0x02, 0, new[] { OpusHead(sampleRate, preskip) });
        WritePage(body, serial, seq++, 0x00, 0, new[] { OpusTags() });

        long samples = 0;
        for (var i = 0; i < packets.Count; i += 48)
        {
            var take = Math.Min(48, packets.Count - i);
            var chunk = packets.GetRange(i, take);
            samples += (long)take * frame;
            var last = i + take >= packets.Count;
            WritePage(body, serial, seq++, last ? (byte)0x04 : (byte)0x00, preskip + samples, chunk);
        }

        var ogg = body.ToArray();
        var message = new byte[4 + ogg.Length];
        BinaryPrimitives.WriteInt32BigEndian(message, streamTag);
        ogg.CopyTo(message, 4);
        if (!MediaFrame.ContainsOgg(message) || !MediaFrame.ContainsOpusHead(message))
            throw new InvalidOperationException("Собранная речь не похожа на Ogg Opus");
        return message;
    }

    private static byte[] OpusHead(int sampleRate, int preskip)
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)preskip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)sampleRate);
        return head;
    }

    private static byte[] OpusTags()
    {
        var vendor = "irina"u8;
        var tags = new byte[8 + 4 + vendor.Length + 4];
        "OpusTags"u8.CopyTo(tags);
        BinaryPrimitives.WriteInt32LittleEndian(tags.AsSpan(8), vendor.Length);
        vendor.CopyTo(tags.AsSpan(12));
        return tags;
    }

    private static void WritePage(Stream output, int serial, int seq, byte type, long granule, IReadOnlyList<byte[]> packets)
    {
        var lace = 0;
        foreach (var packet in packets)
            lace += packet.Length / 255 + 1;
        if (lace > 255)
            throw new InvalidOperationException("Слишком много сегментов на странице Ogg");

        var size = 27 + lace;
        foreach (var packet in packets)
            size += packet.Length;
        var page = new byte[size];
        page[0] = (byte)'O';
        page[1] = (byte)'g';
        page[2] = (byte)'g';
        page[3] = (byte)'S';
        page[5] = type;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(18), seq);
        page[26] = (byte)lace;

        var at = 27;
        foreach (var packet in packets)
        {
            var left = packet.Length;
            do
            {
                var n = Math.Min(left, 255);
                page[at++] = (byte)(left >= 255 ? 255 : left);
                left -= n;
            }
            while (left > 0);
        }

        foreach (var packet in packets)
        {
            packet.CopyTo(page.AsSpan(at));
            at += packet.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Checksum(page));
        output.Write(page);
    }

    private static uint Checksum(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        foreach (var b in page)
            crc = (crc << 8) ^ Crc[((crc >> 24) ^ b) & 0xff];
        return crc;
    }

    private static uint[] BuildCrc()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var r = i << 24;
            for (var j = 0; j < 8; j++)
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04c11db7 : r << 1;
            table[i] = r;
        }

        return table;
    }

    private static (short[] Pcm, int Rate) ReadPcm16(byte[] wav)
    {
        if (wav.Length < 44)
            throw new InvalidOperationException("Слишком короткий WAV");
        var rate = 0;
        var channels = 0;
        var dataAt = -1;
        var dataLen = 0;
        for (var i = 12; i + 8 <= wav.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, i, 4);
            var len = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(i + 4));
            var body = i + 8;
            if (id == "fmt " && body + 16 <= wav.Length)
            {
                var format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(body + 4));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14));
                if (format != 1 || bits != 16)
                    throw new InvalidOperationException("Нужен WAV PCM 16 бит");
            }
            else if (id == "data")
            {
                dataAt = body;
                dataLen = len;
                break;
            }

            i = body + len + (len & 1);
        }

        if (dataAt < 0 || rate <= 0 || channels is < 1 or > 2)
            throw new InvalidOperationException("В WAV нет PCM");
        dataLen = Math.Min(dataLen, wav.Length - dataAt);
        var samples = dataLen / 2 / channels;
        var pcm = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            var left = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(dataAt + i * channels * 2));
            if (channels == 1)
            {
                pcm[i] = left;
                continue;
            }

            var right = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(dataAt + i * 2 * channels + 2));
            pcm[i] = (short)((left + right) / 2);
        }

        return (pcm, rate);
    }

    private static short[] Resample(short[] src, int srcRate, int dstRate)
    {
        if (srcRate == dstRate || src.Length == 0)
            return src;
        var dstLen = (int)((long)src.Length * dstRate / srcRate);
        var dst = new short[Math.Max(dstLen, 1)];
        for (var i = 0; i < dst.Length; i++)
        {
            var pos = (double)i * srcRate / dstRate;
            var i0 = (int)pos;
            if (i0 >= src.Length)
                i0 = src.Length - 1;
            var i1 = Math.Min(i0 + 1, src.Length - 1);
            var frac = pos - i0;
            dst[i] = (short)(src[i0] * (1 - frac) + src[i1] * frac);
        }

        return dst;
    }

    private static short[] WithSilence(short[] src, int rate, int leadMs, int tailMs)
    {
        var lead = rate * leadMs / 1000;
        var tail = rate * tailMs / 1000;
        var dst = new short[lead + src.Length + tail];
        src.CopyTo(dst, lead);
        return dst;
    }
}
