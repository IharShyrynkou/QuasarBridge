using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using QuasarBridge.Options;

namespace QuasarBridge.Services.Media;

public sealed record PlayableMedia(string Title, string Detail, string Path, long PositionSeconds);

public sealed record SavedPlayback(string Title, string Detail, long PositionSeconds);

public enum EpisodeShift
{
    Next,
    Previous,
    NextSeason
}

public sealed class JellyfinLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _http;
    private readonly IOptions<JellyfinOptions> _options;
    private readonly ILogger<JellyfinLibrary> _logger;

    public JellyfinLibrary(IHttpClientFactory http, IOptions<JellyfinOptions> options, ILogger<JellyfinLibrary> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Value.BaseUrl) && !string.IsNullOrWhiteSpace(_options.Value.ApiKey);

    public async Task<PlayableMedia?> FindLastAsync(MediaKind kind, CancellationToken ct)
    {
        var type = kind == MediaKind.Series ? "Episode" : "Movie";
        PlayableMedia? best = null;
        DateTimeOffset bestPlayed = DateTimeOffset.MinValue;
        foreach (var user in await UsersAsync(ct))
        {
            var page = await GetAsync<JellyfinPage>(
                $"/Users/{user.Id}/Items?IncludeItemTypes={type}&Recursive=true&SortBy=DatePlayed&SortOrder=Descending&Limit=12&Fields=Path,UserData,OriginalTitle",
                ct);
            foreach (var item in page?.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Path) || item.UserData?.LastPlayedDate is not DateTimeOffset played)
                    continue;
                if (played < bestPlayed)
                    continue;
                bestPlayed = played;
                best = ToMedia(item, kind);
            }
        }

        if (best is not null)
            _logger.LogDebug("Jellyfin last {kind}: {title} {detail} at {sec}s", kind, best.Title, best.Detail, best.PositionSeconds);
        return best;
    }

    public async Task<PlayableMedia?> FindByTitleAsync(MediaKind kind, string title, CancellationToken ct)
    {
        var query = Norm(title);
        if (query.Length == 0)
            return null;

        PlayableMedia? best = null;
        var bestScore = 0;
        var bestPlayed = DateTimeOffset.MinValue;
        foreach (var user in await UsersAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(user.Id))
                continue;
            if (kind == MediaKind.Movie)
            {
                var page = await SearchItemsAsync(user.Id, "Movie", query, 15, ct);
                Consider(page, kind, query, ref best, ref bestScore, ref bestPlayed);
                continue;
            }

            var series = BestItem(await SearchItemsAsync(user.Id, "Series", query, 10, ct), query);
            if (series?.Id is null || Score(series, query) < 60)
                continue;

            var played = await GetAsync<JellyfinPage>(
                $"/Users/{user.Id}/Items?ParentId={series.Id}&Recursive=true&IncludeItemTypes=Episode&SortBy=DatePlayed&SortOrder=Descending&Limit=8&Fields=Path,UserData,SeriesName",
                ct);
            var episode = (played?.Items ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Path) && item.UserData?.LastPlayedDate is not null)
                .OrderByDescending(item => item.UserData!.LastPlayedDate)
                .FirstOrDefault();
            if (episode is null)
            {
                var all = await GetAsync<JellyfinPage>($"/Shows/{series.Id}/Episodes?userId={user.Id}&Fields=Path", ct);
                episode = (all?.Items ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                    .OrderBy(item => item.ParentIndexNumber ?? int.MaxValue)
                    .ThenBy(item => item.IndexNumber ?? int.MaxValue)
                    .FirstOrDefault();
            }

            if (episode?.Path is null)
                continue;
            var candidate = ToMedia(episode, MediaKind.Series, series.Name);
            var playedAt = episode.UserData?.LastPlayedDate ?? DateTimeOffset.MinValue;
            var score = Score(series, query);
            if (score > bestScore || (score == bestScore && playedAt > bestPlayed))
            {
                best = candidate;
                bestScore = score;
                bestPlayed = playedAt;
            }
        }

        if (best is not null)
            _logger.LogDebug("Jellyfin title {query} → {title} {detail} score={score}", title, best.Title, best.Detail, bestScore);
        return best;
    }

    private async Task<List<JellyfinItem>?> SearchItemsAsync(string userId, string type, string query, int limit, CancellationToken ct)
    {
        List<JellyfinItem>? found = null;
        foreach (var term in SearchTerms(query))
        {
            var page = await GetAsync<JellyfinPage>(
                $"/Users/{userId}/Items?IncludeItemTypes={type}&Recursive=true&SearchTerm={Uri.EscapeDataString(term)}&Limit={limit}&Fields=Path,UserData,OriginalTitle",
                ct);
            found = page?.Items;
            if (BestItem(found, query) is not null)
                return found;
        }

        return found;
    }

    /// <summary>
    /// Jellyfin treats the term as a phrase, so «рик морти» misses «Рик и Морти».
    /// Try the words joined by «и», then each word.
    /// </summary>
    private static IEnumerable<string> SearchTerms(string query)
    {
        yield return query;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (words.Length >= 2)
        {
            var joined = string.Join(" и ", words);
            if (!string.Equals(joined, query, StringComparison.Ordinal))
                yield return joined;
        }

        if (words.Length == 1 && !string.Equals(words[0], query, StringComparison.Ordinal))
            yield return words[0];
        else if (words.Length >= 2)
        {
            foreach (var word in words)
                yield return word;
        }
    }

    private static void Consider(
        List<JellyfinItem>? items,
        MediaKind kind,
        string query,
        ref PlayableMedia? best,
        ref int bestScore,
        ref DateTimeOffset bestPlayed)
    {
        var chosen = BestItem(items, query);
        if (chosen?.Path is null)
            return;
        var score = Score(chosen, query);
        if (score < 60)
            return;
        var played = chosen.UserData?.LastPlayedDate ?? DateTimeOffset.MinValue;
        if (score < bestScore || (score == bestScore && played <= bestPlayed))
            return;
        best = ToMedia(chosen, kind);
        bestScore = score;
        bestPlayed = played;
    }

    private static JellyfinItem? BestItem(List<JellyfinItem>? items, string query)
    {
        JellyfinItem? best = null;
        var bestScore = 0;
        foreach (var item in items ?? [])
        {
            var score = Score(item, query);
            if (score > bestScore)
            {
                best = item;
                bestScore = score;
            }
        }

        return bestScore >= 60 ? best : null;
    }

    private static int Score(JellyfinItem item, string query)
    {
        var score = Math.Max(ScoreName(item.Name, query), ScoreName(item.OriginalTitle, query));
        if (item.SeriesName is not null)
            score = Math.Max(score, ScoreName(item.SeriesName, query));
        return score;
    }

    private static int ScoreName(string? name, string query)
    {
        var normalized = Norm(name);
        if (normalized.Length == 0 || query.Length == 0)
            return 0;
        if (normalized == query)
            return 100;
        // Reject short prefixes like «сверх» → «сверхъестественное».
        if (normalized.StartsWith(query, StringComparison.Ordinal)
            && query.Length >= 4
            && query.Length * 2 >= normalized.Length)
            return 80;
        var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (queryTokens.Length > 0
            && queryTokens.All(token => token.Length >= 2 && normalized.Contains(token, StringComparison.Ordinal)))
            return queryTokens.Length >= 2 || query.Length * 2 >= normalized.Length ? 70 : 0;
        if (query.Length >= 4 && normalized.Contains(query, StringComparison.Ordinal) && query.Length * 2 >= normalized.Length)
            return 60;
        return 0;
    }

    public async Task<PlayableMedia> ShiftEpisodeAsync(string fileName, EpisodeShift shift, CancellationToken ct)
    {
        var parsed = ParsedEpisode.FromFileName(fileName)
            ?? throw new InvalidOperationException("Сейчас играет не серия.");

        foreach (var user in await UsersAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(user.Id))
                continue;
            var seriesPage = await GetAsync<JellyfinPage>(
                $"/Users/{user.Id}/Items?IncludeItemTypes=Series&Recursive=true&SearchTerm={Uri.EscapeDataString(parsed.Series)}&Limit=10&Fields=OriginalTitle",
                ct);
            foreach (var series in seriesPage?.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(series.Id))
                    continue;
                var current = await EpisodesAsync(user.Id, series.Id, parsed.Season, ct);
                var here = current.FirstOrDefault(item => SameFile(item.Path, fileName))
                    ?? current.FirstOrDefault(item => item.IndexNumber == parsed.Episode);
                if (here is null)
                    continue;

                var media = shift switch
                {
                    EpisodeShift.Next => await NextOf(user.Id, series, parsed, current, ct),
                    EpisodeShift.Previous => await PreviousOf(user.Id, series, parsed, current, ct),
                    _ => await FirstOf(user.Id, series, parsed.Season + 1, ct)
                };
                if (media is not null)
                    return media;
                throw Missing(shift);
            }
        }

        throw Missing(shift);
    }

    private async Task<PlayableMedia?> NextOf(
        string userId, JellyfinItem series, ParsedEpisode parsed, List<JellyfinItem> current, CancellationToken ct)
    {
        var next = current
            .Where(item => !string.IsNullOrWhiteSpace(item.Path) && (item.IndexNumber ?? 0) > parsed.Episode)
            .OrderBy(item => item.IndexNumber ?? int.MaxValue)
            .FirstOrDefault();
        if (next?.Path is not null)
            return ToEpisode(series, next, parsed.Season);
        return await FirstOf(userId, series, parsed.Season + 1, ct);
    }

    private async Task<PlayableMedia?> PreviousOf(
        string userId, JellyfinItem series, ParsedEpisode parsed, List<JellyfinItem> current, CancellationToken ct)
    {
        var previous = current
            .Where(item => !string.IsNullOrWhiteSpace(item.Path) && (item.IndexNumber ?? 0) < parsed.Episode)
            .OrderByDescending(item => item.IndexNumber ?? 0)
            .FirstOrDefault();
        if (previous?.Path is not null)
            return ToEpisode(series, previous, parsed.Season);
        if (parsed.Season <= 1)
            return null;
        var earlier = await EpisodesAsync(userId, series.Id!, parsed.Season - 1, ct);
        var last = earlier
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .OrderByDescending(item => item.IndexNumber ?? 0)
            .FirstOrDefault();
        return last?.Path is null ? null : ToEpisode(series, last, parsed.Season - 1);
    }

    private async Task<PlayableMedia?> FirstOf(string userId, JellyfinItem series, int season, CancellationToken ct)
    {
        if (season <= 0 || string.IsNullOrWhiteSpace(series.Id))
            return null;
        var episodes = await EpisodesAsync(userId, series.Id, season, ct);
        var first = episodes
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .OrderBy(item => item.IndexNumber ?? int.MaxValue)
            .FirstOrDefault();
        return first?.Path is null ? null : ToEpisode(series, first, season);
    }

    private async Task<List<JellyfinItem>> EpisodesAsync(string userId, string seriesId, int season, CancellationToken ct)
    {
        var page = await GetAsync<JellyfinPage>(
            $"/Shows/{seriesId}/Episodes?userId={userId}&season={season}&Fields=Path",
            ct);
        return page?.Items ?? [];
    }

    private static PlayableMedia ToEpisode(JellyfinItem series, JellyfinItem episode, int season) =>
        new(
            episode.SeriesName ?? series.Name ?? "сериал",
            $"{episode.ParentIndexNumber ?? season} сезон {episode.IndexNumber} серия",
            episode.Path ?? "",
            0);

    private static InvalidOperationException Missing(EpisodeShift shift) => new(shift switch
    {
        EpisodeShift.Previous => "Предыдущей серии нет.",
        EpisodeShift.NextSeason => "Следующего сезона нет.",
        _ => "Следующей серии нет."
    });

    public async Task<SavedPlayback> SavePositionAsync(string fileName, long positionSeconds, CancellationToken ct)
    {
        var item = ParsedEpisode.FromFileName(fileName) is not null
            ? await FindEpisodeByFileAsync(fileName, ct)
            : await FindMovieByFileAsync(fileName, ct);
        var ticks = Math.Max(0, positionSeconds) * 10_000_000L;
        var raw = await GetStringAsync($"/Users/{item.UserId}/Items/{item.ItemId}/UserData", ct);
        var data = JsonNode.Parse(raw)?.AsObject()
            ?? throw new InvalidOperationException("Jellyfin не отдал данные просмотра.");
        data["PlaybackPositionTicks"] = ticks;
        data["LastPlayedDate"] = DateTimeOffset.Now;
        await PostAsync($"/Users/{item.UserId}/Items/{item.ItemId}/UserData", data, ct);
        _logger.LogDebug(
            "Jellyfin save {title} {detail} at {sec}s",
            item.Title, item.Detail, positionSeconds);
        return new SavedPlayback(item.Title, item.Detail, positionSeconds);
    }

    private async Task<FoundItem> FindEpisodeByFileAsync(string fileName, CancellationToken ct)
    {
        var parsed = ParsedEpisode.FromFileName(fileName)
            ?? throw new InvalidOperationException($"В имени файла нет сезона и серии: {fileName}");
        FoundItem? chosen = null;
        foreach (var user in await UsersAsync(ct))
        {
            var seriesPage = await GetAsync<JellyfinPage>(
                $"/Users/{user.Id}/Items?IncludeItemTypes=Series&Recursive=true&SearchTerm={Uri.EscapeDataString(parsed.Series)}&Limit=10",
                ct);
            foreach (var series in seriesPage?.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(series.Id))
                    continue;
                var episodes = await GetAsync<JellyfinPage>(
                    $"/Shows/{series.Id}/Episodes?userId={user.Id}&season={parsed.Season}&Fields=Path,UserData",
                    ct);
                var items = episodes?.Items ?? [];
                var episode = items.FirstOrDefault(item => SameFile(item.Path, fileName))
                    ?? items.FirstOrDefault(item => item.IndexNumber == parsed.Episode);
                if (episode?.Id is null || string.IsNullOrWhiteSpace(episode.Path))
                    continue;

                var found = new FoundItem
                {
                    UserId = user.Id!,
                    ItemId = episode.Id,
                    Title = episode.SeriesName ?? series.Name ?? parsed.Series,
                    Detail = $"{episode.ParentIndexNumber ?? parsed.Season} сезон {episode.IndexNumber ?? parsed.Episode} серия",
                    LastPlayed = episode.UserData?.LastPlayedDate
                };
                if (chosen is null || (found.LastPlayed ?? DateTimeOffset.MinValue) > (chosen.LastPlayed ?? DateTimeOffset.MinValue))
                    chosen = found;
            }
        }

        return chosen ?? throw new InvalidOperationException($"В Jellyfin нет серии для файла {fileName}");
    }

    private async Task<FoundItem> FindMovieByFileAsync(string fileName, CancellationToken ct)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var term = Regex.Replace(stem, @"[._]+", " ").Trim();
        if (term.Length > 80)
            term = term[..80];

        FoundItem? chosen = null;
        foreach (var user in await UsersAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(term))
                continue;
            var page = await GetAsync<JellyfinPage>(
                $"/Users/{user.Id}/Items?IncludeItemTypes=Movie&Recursive=true&SearchTerm={Uri.EscapeDataString(term)}&Limit=20&Fields=Path,UserData",
                ct);
            var items = page?.Items ?? [];
            var movie = items.FirstOrDefault(item => SameFile(item.Path, fileName))
                ?? items.FirstOrDefault(item => string.Equals(Norm(item.Name), Norm(term), StringComparison.Ordinal));
            if (movie?.Id is null || string.IsNullOrWhiteSpace(movie.Path))
                continue;

            var found = new FoundItem
            {
                UserId = user.Id!,
                ItemId = movie.Id,
                Title = movie.Name ?? stem,
                Detail = "",
                LastPlayed = movie.UserData?.LastPlayedDate
            };
            if (chosen is null || (found.LastPlayed ?? DateTimeOffset.MinValue) > (chosen.LastPlayed ?? DateTimeOffset.MinValue))
                chosen = found;
        }

        return chosen ?? throw new InvalidOperationException($"В Jellyfin нет фильма для файла {fileName}");
    }

    private static PlayableMedia ToMedia(JellyfinItem item, MediaKind kind, string? seriesName = null)
    {
        var title = kind == MediaKind.Series
            ? item.SeriesName ?? seriesName ?? item.Name ?? "сериал"
            : item.Name ?? "фильм";
        var detail = kind == MediaKind.Series && item.ParentIndexNumber is int season && item.IndexNumber is int episode
            ? $"{season} сезон {episode} серия"
            : "";
        var ticks = item.UserData?.PlaybackPositionTicks ?? 0;
        return new PlayableMedia(title, detail, item.Path ?? "", Math.Max(0, ticks / 10_000_000));
    }

    private async Task<IReadOnlyList<JellyfinUser>> UsersAsync(CancellationToken ct)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Jellyfin не настроен.");

        var users = await GetAsync<List<JellyfinUser>>("/Users", ct) ?? [];
        if (!string.IsNullOrWhiteSpace(options.UserId))
            users = users.Where(user => string.Equals(user.Id, options.UserId, StringComparison.OrdinalIgnoreCase)).ToList();
        return users.Where(user => !string.IsNullOrWhiteSpace(user.Id)).ToList();
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, path);
        using var response = await _http.CreateClient("jellyfin").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Jellyfin вернул {(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    private async Task<string> GetStringAsync(string path, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, path);
        using var response = await _http.CreateClient("jellyfin").SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Jellyfin вернул {(int)response.StatusCode}");
        return body;
    }

    private async Task PostAsync(string path, JsonNode body, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, path);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.CreateClient("jellyfin").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Jellyfin вернул {(int)response.StatusCode}");
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var options = _options.Value;
        var request = new HttpRequestMessage(method, options.BaseUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", $"Token=\"{options.ApiKey}\"");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static bool SameFile(string? path, string fileName) =>
        string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileNameWithoutExtension(path), Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var lower = value.ToLowerInvariant().Replace('ё', 'е');
        var chars = lower.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class JellyfinUser
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class JellyfinPage
    {
        public List<JellyfinItem>? Items { get; set; }
    }

    private sealed class JellyfinItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? OriginalTitle { get; set; }
        public string? SeriesName { get; set; }
        public string? Path { get; set; }
        public int? IndexNumber { get; set; }
        public int? ParentIndexNumber { get; set; }
        public JellyfinUserData? UserData { get; set; }
    }

    private sealed class JellyfinUserData
    {
        public long PlaybackPositionTicks { get; set; }
        public DateTimeOffset? LastPlayedDate { get; set; }
    }

    private sealed class FoundItem
    {
        public string UserId { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public DateTimeOffset? LastPlayed { get; set; }
    }

    private sealed class ParsedEpisode
    {
        private static readonly Regex Pattern = new(@"^(?<series>.+?) - S(?<season>\d+)E(?<episode>\d+)", RegexOptions.IgnoreCase);

        public string Series { get; private init; } = "";
        public int Season { get; private init; }
        public int Episode { get; private init; }

        public static ParsedEpisode? FromFileName(string fileName)
        {
            var match = Pattern.Match(Path.GetFileNameWithoutExtension(fileName));
            if (!match.Success
                || !int.TryParse(match.Groups["season"].Value, out var season)
                || !int.TryParse(match.Groups["episode"].Value, out var episode))
                return null;

            return new ParsedEpisode
            {
                Series = match.Groups["series"].Value.Trim(),
                Season = season,
                Episode = episode
            };
        }
    }
}
