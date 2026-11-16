namespace QuasarBridge.Services;

public static class MediaFrame
{
    /// <summary>
    /// Quasar wraps Ogg pages as [u32 BE length?][OggS...]. Magic is often at offset 4.
    /// </summary>
    public static bool ContainsOgg(ReadOnlySpan<byte> payload)
    {
        var limit = Math.Min(payload.Length - 3, 32);
        for (var i = 0; i <= limit; i++)
        {
            if (payload[i] == (byte)'O' &&
                payload[i + 1] == (byte)'g' &&
                payload[i + 2] == (byte)'g' &&
                payload[i + 3] == (byte)'S')
                return true;
        }

        return false;
    }

    public static bool ContainsOpusHead(ReadOnlySpan<byte> payload)
    {
        // "OpusHead" appears inside Ogg Opus identification headers.
        ReadOnlySpan<byte> needle = "OpusHead"u8;
        if (payload.Length < needle.Length) return false;
        return payload.IndexOf(needle) >= 0;
    }

    public static bool IsCloudAudio(ReadOnlySpan<byte> payload) =>
        ContainsOgg(payload) || ContainsOpusHead(payload);

    public static bool IsAapi(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 &&
        payload[0] == (byte)'A' &&
        payload[1] == (byte)'A' &&
        payload[2] == (byte)'P' &&
        payload[3] == (byte)'I';
}
