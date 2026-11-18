using System.Text;

namespace QuasarBridge.Services.Media;

/// <summary>
/// Pulls the growing speech hypothesis out of Yandex uniproxy frames.
/// Partial results are length-prefixed UTF-8 words inside binary AAPI messages.
/// </summary>
public static class AsrText
{
    public static void Collect(ReadOnlySpan<byte> data, List<string> tokens)
    {
        if (data.IsEmpty || IsCloudReply(data) || MediaFrame.IsCloudAudio(data))
            return;

        var found = new List<string>();
        CollectPrefixed(data, found);
        if (found.Count == 0)
            CollectRuns(data, found);

        foreach (var token in found)
            Add(tokens, token);
    }

    public static bool IsCloudReply(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || MediaFrame.IsCloudAudio(data))
            return false;
        return data.IndexOf("simple_text"u8) >= 0 || data.IndexOf("tts_play_placeholder"u8) >= 0;
    }

    public static string? Join(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return null;

        string? spaced = null;
        foreach (var token in tokens)
        {
            if (token.Contains(' ') && (spaced is null || token.Length > spaced.Length))
                spaced = token;
        }

        return spaced ?? string.Join(' ', tokens);
    }

    /// <summary>
    /// Tokens with Alice's «подключите станцию к экрану» boilerplate removed.
    /// </summary>
    public static List<string> Content(IReadOnlyList<string> tokens)
    {
        var list = new List<string>();
        foreach (var token in tokens)
        {
            if (!IsBoilerplate(token))
                list.Add(token);
        }

        return list;
    }

    /// <summary>
    /// Phrase for a human: kept tokens in order, shorter prefixes dropped.
    /// </summary>
    public static string JoinContent(IReadOnlyList<string> tokens)
    {
        var kept = new List<string>();
        foreach (var token in Content(tokens))
        {
            var covered = false;
            for (var i = 0; i < kept.Count; i++)
            {
                if (kept[i].StartsWith(token, StringComparison.OrdinalIgnoreCase) && kept[i].Length > token.Length)
                {
                    covered = true;
                    break;
                }

                if (token.StartsWith(kept[i], StringComparison.OrdinalIgnoreCase) && token.Length > kept[i].Length)
                {
                    kept.RemoveAt(i);
                    i--;
                }
            }

            if (!covered)
                kept.Add(token);
        }

        return string.Join(' ', kept);
    }

    public static string Show(IReadOnlyList<string> tokens)
    {
        var content = Content(tokens);
        var source = content.Count > 0 ? content : tokens;
        return string.Join(" · ", source);
    }

    /// <summary>
    /// True when a known command is buried in tokens that do not already parse.
    /// A joined phrase like «включи сериал рик морти» is handled locally, without waiting for a model.
    /// </summary>
    public static bool NeedsModel(IReadOnlyList<string> tokens)
    {
        var content = Content(tokens);
        if (content.Count <= 1)
            return false;
        if (MediaIntents.Parse(JoinContent(tokens)) is not null)
            return false;
        if (!content.Exists(static token => MediaIntents.Parse(token) is not null))
            return false;

        foreach (var token in content)
        {
            if (!MediaIntents.IsCommandOnly(token))
                return true;
        }

        return false;
    }

    private static void CollectPrefixed(ReadOnlySpan<byte> data, List<string> found)
    {
        for (var i = 0; i < data.Length - 2; i++)
        {
            var len = data[i];
            if (len < 4 || i + 1 + len > data.Length)
                continue;
            if (!TryPhrase(data.Slice(i + 1, len), out var phrase))
                continue;
            found.Add(phrase);
            i += len;
        }
    }

    private static void CollectRuns(ReadOnlySpan<byte> data, List<string> found)
    {
        var text = Encoding.UTF8.GetString(data);
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var ok = i < text.Length && (IsCyrillic(text[i]) || (start >= 0 && text[i] is ' ' or '-'));
            if (ok)
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start < 0)
                continue;
            var phrase = text[start..i].Trim();
            if (Letters(phrase) >= 2)
                found.Add(phrase);
            start = -1;
        }
    }

    private static bool TryPhrase(ReadOnlySpan<byte> bytes, out string phrase)
    {
        phrase = "";
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Length < 2 || text.Contains('\uFFFD'))
                return false;
            var letters = 0;
            foreach (var ch in text)
            {
                if (IsCyrillic(ch))
                {
                    letters++;
                    continue;
                }

                if (ch is not (' ' or '-' or '\''))
                    return false;
            }

            if (letters < 2)
                return false;
            phrase = text.Trim();
            return phrase.Length >= 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Add(List<string> tokens, string token)
    {
        token = token.Trim();
        if (token.Length < 2)
            return;
        if (token.Length > 72 || token.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 16)
            return;

        for (var i = 0; i < tokens.Count; i++)
        {
            var existing = tokens[i];
            if (existing.Equals(token, StringComparison.OrdinalIgnoreCase))
                return;
            if (existing.StartsWith(token, StringComparison.OrdinalIgnoreCase) && existing.Length > token.Length)
                return;
            if (token.StartsWith(existing, StringComparison.OrdinalIgnoreCase) && token.Length > existing.Length)
            {
                tokens.RemoveAt(i);
                i--;
            }
        }

        tokens.Add(token);
    }

    private static readonly HashSet<string> Boilerplate = new(StringComparer.Ordinal)
    {
        "чтобы", "смотреть", "видеоролики", "видеоролик", "фильмы", "сериалы",
        "нужно", "подключить", "подключите", "подключи", "подключение",
        "яндекс", "станцию", "станция", "станции",
        "экрану", "экран", "экрана", "телевизору", "телевизор", "телевизора",
        "результаты", "поиска", "поиск", "видео", "подряд", "выберите", "удобнее", "открываю"
    };

    private static bool IsBoilerplate(string token)
    {
        var words = Words(token);
        if (words.Count == 0)
            return true;
        return words.All(word => Boilerplate.Contains(word) || word is "к" or "ко" or "на");
    }

    private static List<string> Words(string token)
    {
        var text = token.ToLowerInvariant().Replace('ё', 'е');
        return text.Split([' ', '\t', '-', '—', ',', '.', '!', '?', '«', '»', '"'], StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static int Letters(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (IsCyrillic(ch))
                count++;
        }

        return count;
    }

    private static bool IsCyrillic(char ch) => ch is >= '\u0400' and <= '\u04FF';
}
