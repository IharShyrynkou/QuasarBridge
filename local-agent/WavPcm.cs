namespace LocalAgent;

internal static class WavPcm
{
    public static byte[] ToMono48K(byte[] wav)
    {
        if (!TryRead(wav, out var rate, out var channels, out var bits, out var pcm))
            throw new InvalidOperationException("Синтез отдал не wav");
        if (bits != 16)
            throw new InvalidOperationException("Нужен 16-битный wav");

        var mono = channels == 1 ? pcm : ToMono(pcm, channels);
        var atSpeakerRate = rate == 48000 ? mono : Resample(mono, rate, 48000);
        return Write(atSpeakerRate, 48000);
    }

    private static bool TryRead(byte[] wav, out int rate, out short channels, out short bits, out byte[] pcm)
    {
        rate = 0;
        channels = 0;
        bits = 0;
        pcm = [];
        if (wav.Length < 44 || wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F')
            return false;

        var offset = 12;
        var sawFormat = false;
        while (offset + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, offset, 4);
            var size = BitConverter.ToInt32(wav, offset + 4);
            var body = offset + 8;
            if (size < 0 || body > wav.Length)
                return false;
            var available = Math.Min(size, wav.Length - body);
            if (id == "fmt " && available >= 16)
            {
                channels = BitConverter.ToInt16(wav, body + 2);
                rate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToInt16(wav, body + 14);
                sawFormat = true;
            }
            else if (id == "data" && sawFormat)
            {
                pcm = wav.AsSpan(body, available).ToArray();
                return channels > 0 && rate > 0 && bits > 0;
            }

            offset = body + size + (size & 1);
        }

        return false;
    }

    private static byte[] ToMono(byte[] interleaved, int channels)
    {
        var frames = interleaved.Length / (2 * channels);
        var mono = new byte[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0;
            for (var channel = 0; channel < channels; channel++)
                sum += BitConverter.ToInt16(interleaved, (frame * channels + channel) * 2);
            BitConverter.TryWriteBytes(mono.AsSpan(frame * 2, 2), (short)(sum / channels));
        }

        return mono;
    }

    private static byte[] Resample(byte[] mono, int fromRate, int toRate)
    {
        var inputFrames = mono.Length / 2;
        if (inputFrames == 0)
            return mono;
        var outputFrames = Math.Max(1, (int)((long)inputFrames * toRate / fromRate));
        var output = new byte[outputFrames * 2];
        for (var i = 0; i < outputFrames; i++)
        {
            var position = (double)i * fromRate / toRate;
            var left = (int)position;
            var right = Math.Min(left + 1, inputFrames - 1);
            var mix = position - left;
            var sample = BitConverter.ToInt16(mono, left * 2) * (1 - mix) + BitConverter.ToInt16(mono, right * 2) * mix;
            BitConverter.TryWriteBytes(output.AsSpan(i * 2, 2), (short)Math.Clamp(sample, short.MinValue, short.MaxValue));
        }

        return output;
    }

    private static byte[] Write(byte[] pcm, int rate)
    {
        var wav = new byte[44 + pcm.Length];
        wav[0] = (byte)'R';
        wav[1] = (byte)'I';
        wav[2] = (byte)'F';
        wav[3] = (byte)'F';
        BitConverter.TryWriteBytes(wav.AsSpan(4, 4), 36 + pcm.Length);
        wav[8] = (byte)'W';
        wav[9] = (byte)'A';
        wav[10] = (byte)'V';
        wav[11] = (byte)'E';
        wav[12] = (byte)'f';
        wav[13] = (byte)'m';
        wav[14] = (byte)'t';
        wav[15] = (byte)' ';
        BitConverter.TryWriteBytes(wav.AsSpan(16, 4), 16);
        BitConverter.TryWriteBytes(wav.AsSpan(20, 2), (short)1);
        BitConverter.TryWriteBytes(wav.AsSpan(22, 2), (short)1);
        BitConverter.TryWriteBytes(wav.AsSpan(24, 4), rate);
        BitConverter.TryWriteBytes(wav.AsSpan(28, 4), rate * 2);
        BitConverter.TryWriteBytes(wav.AsSpan(32, 2), (short)2);
        BitConverter.TryWriteBytes(wav.AsSpan(34, 2), (short)16);
        wav[36] = (byte)'d';
        wav[37] = (byte)'a';
        wav[38] = (byte)'t';
        wav[39] = (byte)'a';
        BitConverter.TryWriteBytes(wav.AsSpan(40, 4), pcm.Length);
        pcm.CopyTo(wav.AsSpan(44));
        return wav;
    }
}
