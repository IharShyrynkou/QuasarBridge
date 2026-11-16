using System.Text;

namespace QuasarBridge.Services;

/// <summary>
/// Keeps AAPI/protobuf framing (same byte length) but removes spoken content.
/// Station plays voice via <c>tts_play_placeholder</c> even when text is blanked —
/// that directive must be neutralized too.
/// </summary>
public static class PayloadSilencer
{
    private static readonly string[] AsciiKillList =
    [
        "tts_play_placeholder",
        "tts_play_placeholder".ToUpperInvariant(), // just in case
    ];

    public static byte[] SilenceAliceReply(byte[] src)
    {
        var dst = (byte[])src.Clone();
        BlankCyrillicUtf8(dst);
        foreach (var token in AsciiKillList)
            ReplaceAsciiInPlace(dst, token, (byte)' ');
        // Extra aliases / related markers seen in traces
        ReplaceAsciiInPlace(dst, "simple_text", "simple_null"u8); // same length 11
        return dst;
    }

    /// <summary>Backward-compatible name used by existing call sites.</summary>
    public static byte[] BlankReadableSpeech(byte[] src) => SilenceAliceReply(src);

    private static void BlankCyrillicUtf8(byte[] dst)
    {
        for (var i = 0; i < dst.Length - 1; i++)
        {
            var b0 = dst[i];
            var b1 = dst[i + 1];
            if ((b0 == 0xD0 || b0 == 0xD1) && b1 is >= 0x80 and <= 0xBF)
            {
                dst[i] = (byte)' ';
                dst[i + 1] = (byte)' ';
                i++;
            }
        }
    }

    private static void ReplaceAsciiInPlace(byte[] dst, string ascii, byte with)
    {
        if (string.IsNullOrEmpty(ascii)) return;
        var needle = Encoding.ASCII.GetBytes(ascii);
        ReplaceAsciiInPlace(dst, needle, with);
    }

    private static void ReplaceAsciiInPlace(byte[] dst, ReadOnlySpan<byte> needle, byte with)
    {
        if (needle.IsEmpty || needle.Length > dst.Length) return;
        for (var i = 0; i <= dst.Length - needle.Length; i++)
        {
            var span = dst.AsSpan(i, needle.Length);
            if (!span.SequenceEqual(needle)) continue;
            span.Fill(with);
            i += needle.Length - 1;
        }
    }

    private static void ReplaceAsciiInPlace(byte[] dst, string ascii, ReadOnlySpan<byte> replacement)
    {
        var needle = Encoding.ASCII.GetBytes(ascii);
        if (needle.Length != replacement.Length)
            throw new ArgumentException("replacement must be same length");
        for (var i = 0; i <= dst.Length - needle.Length; i++)
        {
            var span = dst.AsSpan(i, needle.Length);
            if (!span.SequenceEqual(needle)) continue;
            replacement.CopyTo(span);
            i += needle.Length - 1;
        }
    }
}
