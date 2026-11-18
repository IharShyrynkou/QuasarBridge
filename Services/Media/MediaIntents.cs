namespace QuasarBridge.Services.Media;

public enum MediaKind
{
    Series,
    Movie
}

public enum MediaAct
{
    ContinueLast,
    AskTitle,
    PlayTitle,
    Close,
    Pause,
    Seek,
    NextEpisode,
    PreviousEpisode,
    NextSeason
}

public enum AnswerKind
{
    Empty,
    Cancel,
    Release,
    Last,
    Title
}

public sealed record MediaIntent(MediaAct Act, MediaKind Kind, string? Title, int SeekSeconds = 0);

public readonly record struct MediaAnswer(AnswerKind Kind, MediaKind? KindOverride, string? Title);

public static class MediaIntents
{
    public static MediaIntent? Parse(string? phrase)
    {
        var words = Words(phrase);
        if (words.Length == 0)
            return null;

        var text = string.Join(' ', words);
        if (text.Contains("какой сериал", StringComparison.Ordinal)
            || text.Contains("какой фильм", StringComparison.Ordinal)
            || text.Contains("подключите", StringComparison.Ordinal)
            || text.Contains("подключить яндекс", StringComparison.Ordinal))
            return null;

        if (IsClose(words))
            return new MediaIntent(MediaAct.Close, KindOf(words) ?? MediaKind.Series, null);
        if (IsPause(words))
            return new MediaIntent(MediaAct.Pause, KindOf(words) ?? MediaKind.Series, null);
        if (SpeechTime.TryDelta(words, out var delta))
            return new MediaIntent(MediaAct.Seek, MediaKind.Series, null, delta);
        if (IsNextSeason(words))
            return new MediaIntent(MediaAct.NextSeason, MediaKind.Series, null);
        if (IsNextEpisode(words))
            return new MediaIntent(MediaAct.NextEpisode, MediaKind.Series, null);
        if (IsPreviousEpisode(words))
            return new MediaIntent(MediaAct.PreviousEpisode, MediaKind.Series, null);

        var kind = KindOf(words);
        if (kind is null)
            return null;

        var play = words.Any(IsPlayVerb) || words.Any(IsContinue);
        if (!play)
            return null;

        var titleWords = DropEchoes(words.Where(w => !IsNoise(w) && !IsPlayVerb(w) && !IsContinue(w) && !IsKind(w)).ToArray());
        var onlyLast = titleWords.Length > 0 && titleWords.All(IsLast);
        var noTitle = titleWords.Length == 0 || onlyLast;
        if (noTitle && (onlyLast || words.Any(IsContinue) || words.Any(IsLast)))
            return new MediaIntent(MediaAct.ContinueLast, kind.Value, null);
        if (noTitle)
            return new MediaIntent(MediaAct.AskTitle, kind.Value, null);

        var title = CompactSearchTitle(string.Join(' ', titleWords));
        return string.IsNullOrWhiteSpace(title)
            ? new MediaIntent(MediaAct.AskTitle, kind.Value, null)
            : new MediaIntent(MediaAct.PlayTitle, kind.Value, title);
    }

    /// <summary>
    /// Очистка названия после ASR: синопсис с экрана Яндекса, UI-фразы, хвост из 2–5 слов.
    /// </summary>
    public static string CompactSearchTitle(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return "";
        var words = Words(phrase);
        if (words.Length == 0)
            return "";
        words = words.Where(w => !IsSynopsisWord(w) && !IsUiChatter(w)).ToArray();
        if (words.Length == 0)
            return "";
        words = DropEchoes(words.Where(w => !IsKind(w)).ToArray());
        if (words.Length <= 6 && string.Join(' ', words).Length <= 52)
            return string.Join(' ', words);
        var tail = words.TakeLast(Math.Min(5, words.Length)).Where(w => !IsNoise(w)).ToArray();
        tail = DropEchoes(tail);
        return tail.Length >= 1 ? string.Join(' ', tail) : string.Join(' ', words.TakeLast(3));
    }

    public static MediaAnswer ParseAnswer(string? phrase)
    {
        var words = Words(phrase);
        if (words.Length == 0)
            return new MediaAnswer(AnswerKind.Empty, null, null);
        if (IsCancel(words))
            return new MediaAnswer(AnswerKind.Cancel, null, null);
        if (IsForeign(words))
            return new MediaAnswer(AnswerKind.Release, null, null);

        var meaningful = words.Where(w => !IsNoise(w) && !IsPlayVerb(w) && !IsContinue(w)).ToArray();
        if (meaningful.Length == 0)
            return new MediaAnswer(AnswerKind.Empty, null, null);
        if (meaningful.All(w => IsLast(w) || IsKind(w)) && meaningful.Any(IsLast))
            return new MediaAnswer(AnswerKind.Last, KindOf(meaningful), null);

        var title = CompactSearchTitle(string.Join(' ', meaningful.Where(w => !IsKind(w))));
        if (string.IsNullOrWhiteSpace(title))
            return new MediaAnswer(AnswerKind.Empty, null, null);
        return new MediaAnswer(AnswerKind.Title, KindOf(meaningful), title);
    }

    /// <summary>True when the phrase is still only a verb and may yet grow into a media command.</summary>
    public static bool MayGrowIntoCommand(string? phrase)
    {
        var words = Words(phrase);
        if (words.Length == 0 || words.Length > 4)
            return false;
        return words.Any(IsPlayVerb) || words.Any(IsContinue) || words.Any(IsCloseWord);
    }

    /// <summary>True when every word is part of a player command and there is no title fragment.</summary>
    public static bool IsCommandOnly(string? phrase)
    {
        var words = Words(phrase);
        return words.Length > 0 && words.All(IsCommandWord);
    }

    public static bool CloudAsks(string replyText, MediaKind kind)
    {
        var text = Norm(replyText);
        if (text.Contains("что включить", StringComparison.Ordinal) || text.Contains("что посмотреть", StringComparison.Ordinal))
            return true;
        return kind == MediaKind.Series
            ? text.Contains("какой сериал", StringComparison.Ordinal)
            : text.Contains("какой фильм", StringComparison.Ordinal) || text.Contains("какое кино", StringComparison.Ordinal);
    }

    private static MediaKind? KindOf(IEnumerable<string> words)
    {
        MediaKind? kind = null;
        foreach (var word in words)
        {
            if (word.StartsWith("сериал", StringComparison.Ordinal))
                kind = MediaKind.Series;
            else if (word.StartsWith("фильм", StringComparison.Ordinal) || word.StartsWith("кино", StringComparison.Ordinal))
                kind = MediaKind.Movie;
        }

        return kind;
    }

    private static bool IsCommandWord(string word) =>
        IsNoise(word) || IsPlayVerb(word) || IsContinue(word) || IsCloseWord(word) || IsKind(word) || IsLast(word)
        || word is "и" or "а" or "или";

    private static bool IsKind(string word) =>
        word.StartsWith("сериал", StringComparison.Ordinal)
        || word.StartsWith("фильм", StringComparison.Ordinal)
        || word.StartsWith("кино", StringComparison.Ordinal);

    private static bool IsCloseWord(string word) =>
        word.StartsWith("закр", StringComparison.Ordinal)
        || word.StartsWith("выключ", StringComparison.Ordinal)
        || word is "выруби" or "вырубай";

    /// <summary>«морти» and a misheard «морси» are the same word. Keep the first.</summary>
    private static string[] DropEchoes(string[] words)
    {
        var kept = new List<string>();
        foreach (var word in words)
        {
            if (word.Length >= 4 && kept.Any(previous => previous.Length >= 4 && EditDistance(previous, word) <= 1))
                continue;
            kept.Add(word);
        }

        return kept.ToArray();
    }

    private static int EditDistance(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1)
            return 2;
        var rows = left.Length + 1;
        var cols = right.Length + 1;
        var prev = new int[cols];
        var curr = new int[cols];
        for (var col = 0; col < cols; col++)
            prev[col] = col;
        for (var row = 1; row < rows; row++)
        {
            curr[0] = row;
            var min = curr[0];
            for (var col = 1; col < cols; col++)
            {
                var cost = left[row - 1] == right[col - 1] ? 0 : 1;
                curr[col] = Math.Min(Math.Min(curr[col - 1] + 1, prev[col] + 1), prev[col - 1] + cost);
                if (curr[col] < min)
                    min = curr[col];
            }

            if (min > 1)
                return 2;
            (prev, curr) = (curr, prev);
        }

        return prev[cols - 1];
    }

    private static bool IsPause(string[] words)
    {
        if (words.Any(word => word.StartsWith("пауз", StringComparison.Ordinal)))
            return true;
        var stop = words.Any(word => word.StartsWith("останов", StringComparison.Ordinal) || word is "стоп");
        var playback = words.Any(word =>
            word.StartsWith("воспроизв", StringComparison.Ordinal)
            || word.StartsWith("просмотр", StringComparison.Ordinal)
            || word.StartsWith("плеер", StringComparison.Ordinal)
            || word.StartsWith("сериал", StringComparison.Ordinal)
            || word.StartsWith("фильм", StringComparison.Ordinal)
            || word.StartsWith("кино", StringComparison.Ordinal)
            || word.StartsWith("видео", StringComparison.Ordinal)
            || word is "серию" or "серия" or "серии");
        return stop && playback;
    }

    private static bool IsEpisodeWord(string word) =>
        word is "серию" or "серия" or "серии" or "серий";

    private static bool IsNextSeason(string[] words) =>
        words.Any(IsPlayVerb)
        && words.Any(word => word.StartsWith("следующ", StringComparison.Ordinal))
        && words.Any(word => word.StartsWith("сезон", StringComparison.Ordinal));

    private static bool IsNextEpisode(string[] words) =>
        words.Any(IsPlayVerb)
        && words.Any(word => word.StartsWith("следующ", StringComparison.Ordinal))
        && words.Any(IsEpisodeWord)
        && !words.Any(word => word.StartsWith("сезон", StringComparison.Ordinal));

    private static bool IsPreviousEpisode(string[] words) =>
        words.Any(IsPlayVerb)
        && words.Any(word => word.StartsWith("предыдущ", StringComparison.Ordinal) || word.StartsWith("прошл", StringComparison.Ordinal))
        && words.Any(IsEpisodeWord)
        && !words.Any(word => word.StartsWith("сезон", StringComparison.Ordinal));

    private static bool IsClose(string[] words)
    {
        if (words.Length == 0)
            return false;

        var close = words.Any(IsCloseWord);
        var player = words.Any(word =>
            word.StartsWith("плеер", StringComparison.Ordinal)
            || word.StartsWith("фильм", StringComparison.Ordinal)
            || word.StartsWith("сериал", StringComparison.Ordinal)
            || word.StartsWith("кино", StringComparison.Ordinal)
            || word.StartsWith("видео", StringComparison.Ordinal)
            || word.StartsWith("воспроизв", StringComparison.Ordinal)
            || word.StartsWith("просмотр", StringComparison.Ordinal));
        return close && player;
    }

    private static bool IsContinue(string word) =>
        word.StartsWith("продолж", StringComparison.Ordinal) || word.StartsWith("возобнов", StringComparison.Ordinal);

    private static bool IsPlayVerb(string word) =>
        word.StartsWith("включ", StringComparison.Ordinal)
        || word.StartsWith("запуст", StringComparison.Ordinal)
        || word.StartsWith("запуск", StringComparison.Ordinal)
        || word.StartsWith("постав", StringComparison.Ordinal)
        || word.StartsWith("воспроизв", StringComparison.Ordinal)
        || word.StartsWith("посмотр", StringComparison.Ordinal)
        || word.StartsWith("крут", StringComparison.Ordinal);

    private static bool IsLast(string word) =>
        word.StartsWith("последн", StringComparison.Ordinal) || word.StartsWith("прошл", StringComparison.Ordinal);

    private static bool IsNoise(string word) =>
        word is "пожалуйста" or "алиса" or "алису" or "мне" or "нам" or "хочу" or "можно" or "ну" or "же"
            or "мой" or "моя" or "мою" or "мое" or "моего" or "этот" or "эту" or "это" or "на" or "с" or "от"
            or "из" or "про" or "там" or "тут" or "сейчас" or "просто" or "давай" or "какой" or "какая"
            or "какое" or "какие" or "вам" or "тебе" or "где" or "удобнее" or "все" or "всех";

    private static bool IsSynopsisWord(string word) =>
        word.StartsWith("рассказыва", StringComparison.Ordinal)
        || word.StartsWith("путешеств", StringComparison.Ordinal)
        || word.StartsWith("расследу", StringComparison.Ordinal)
        || word.StartsWith("паранорм", StringComparison.Ordinal)
        || word.StartsWith("легенд", StringComparison.Ordinal)
        || word.StartsWith("фолькл", StringComparison.Ordinal)
        || word.StartsWith("порожден", StringComparison.Ordinal)
        || word.StartsWith("винчест", StringComparison.Ordinal)
        || word.StartsWith("автомоб", StringComparison.Ordinal)
        || word.StartsWith("американск", StringComparison.Ordinal)
        || word.StartsWith("сверхъест", StringComparison.Ordinal)
        || word.StartsWith("сверхест", StringComparison.Ordinal)
        || word.StartsWith("впечатля", StringComparison.Ordinal)
        || word.StartsWith("спецэфф", StringComparison.Ordinal)
        || word.StartsWith("мультфил", StringComparison.Ordinal)
        || word is "демоны" or "демон" or "пр" or "и" or "дина" or "как" or "такими" or "основ" or "многие" or "которых";

    private static bool IsUiChatter(string word) =>
        word.StartsWith("результат", StringComparison.Ordinal)
        || word.StartsWith("поиск", StringComparison.Ordinal)
        || word.StartsWith("видео", StringComparison.Ordinal)
        || word.StartsWith("подряд", StringComparison.Ordinal)
        || word.StartsWith("открыва", StringComparison.Ordinal)
        || word.StartsWith("выбер", StringComparison.Ordinal)
        || word.StartsWith("удобн", StringComparison.Ordinal)
        || word is "серии" or "серия" or "серию" or "мульт" or "мультик";

    private static bool IsCancel(string[] words)
    {
        if (words.Contains("не") && words.Any(w => w is "надо" or "нужно") && words.Length <= 4)
            return true;
        return words.All(w => w is "отмена" or "отмени" or "отменить" or "забей" or "отстань" or "хватит" or "пожалуйста" or "алиса");
    }

    private static bool IsForeign(string[] words) =>
        words.Any(w =>
            w.StartsWith("погод", StringComparison.Ordinal)
            || w.StartsWith("будиль", StringComparison.Ordinal)
            || w.StartsWith("таймер", StringComparison.Ordinal)
            || w.StartsWith("громкост", StringComparison.Ordinal)
            || w.StartsWith("музык", StringComparison.Ordinal)
            || w.StartsWith("песн", StringComparison.Ordinal)
            || w.StartsWith("напомн", StringComparison.Ordinal)
            || w.StartsWith("который", StringComparison.Ordinal)
            || w is "свет" or "время" or "час" or "часов");

    private static string Norm(string phrase) =>
        phrase.ToLowerInvariant().Replace('ё', 'е');

    private static string[] Words(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return [];
        var text = Norm(phrase);
        return text.Split([' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';', '"', '«', '»', '—', '-'], StringSplitOptions.RemoveEmptyEntries);
    }
}
