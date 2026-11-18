using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using QuasarBridge.Options;

namespace QuasarBridge.Services.Media;

/// <summary>
/// Asks FreeLLM (Gemini) to turn a messy token list into one player command.
/// </summary>
public sealed class FreeLlmCommandDecoder
{
    private const string SystemPrompt = """
        Из токенов распознавания речи собери одну команду для локального плеера.
        Токены — куски одной фразы человека и иногда мусор ответа Алисы.
        Мусор выбрасывай: «чтобы смотреть видеоролики», «фильмы», «сериалы», «нужно подключить яндекс станцию к экрану», «подключите станцию к экрану».
        Верни только JSON без пояснений:
        {"act":"play_title","kind":"series","title":"рик и морти"}
        act — ровно одно из: play_title, play_last, close, none.
        kind — series или movie.
        title — название как его сказал человек, без слов «включи», «сериал», «фильм». Если названия нет, null.
        play_title — в токенах есть название сериала или фильма.
        play_last — включить или продолжить без названия: «включи сериал», «включи фильм», «последний».
        close — выключить, закрыть или остановить плеер, сериал или фильм.
        none — это не команда про фильм или сериал.
        Не выдумывай название, если его нет среди токенов.
        Пример токенов: включи сериал / рик / морти / чтобы / смотреть / видеоролики / подключить / яндекс / станцию / экрану
        Ответ на пример: {"act":"play_title","kind":"series","title":"рик и морти"}
        """;

    private static readonly string[] TitleNoise =
    [
        "включи", "включите", "включить", "запусти", "запустите",
        "сериал", "сериала", "фильм", "фильма", "кино",
        "пожалуйста", "алиса", "последний", "последняя", "последнее", "последнюю"
    ];

    private readonly IHttpClientFactory _http;
    private readonly ILogger<FreeLlmCommandDecoder> _logger;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;

    public FreeLlmCommandDecoder(
        IOptions<FreeLlmOptions> options,
        IHttpClientFactory http,
        ILogger<FreeLlmCommandDecoder> logger)
    {
        _http = http;
        _logger = logger;
        _endpoint = string.IsNullOrWhiteSpace(options.Value.BaseUrl)
            ? "http://127.0.0.1:3001/v1/chat/completions"
            : options.Value.BaseUrl;
        _apiKey = options.Value.ApiKey;
        _model = string.IsNullOrWhiteSpace(options.Value.Model) ? "gemini-3.5-flash-lite" : options.Value.Model;
    }

    public async Task<MediaIntent?> DecodeAsync(IReadOnlyList<string> tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("Нет FreeLlm:ApiKey");

        var user = string.Join('\n', tokens);
        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = user }
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var client = _http.CreateClient("freellm");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"FreeLLM вернул {(int)response.StatusCode}: {Trim(body)}");

        using var document = JsonDocument.Parse(body);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content))
            throw new JsonException("Пустой ответ модели");

        var intent = Read(content);
        _logger.LogDebug("FreeLLM raw {content}", Trim(content));
        return intent;
    }

    public static MediaIntent? Read(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new JsonException("В ответе модели нет JSON");

        using var document = JsonDocument.Parse(content[start..(end + 1)]);
        var root = document.RootElement;
        var act = root.TryGetProperty("act", out var actValue) ? actValue.GetString() : null;
        var kindText = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
        string? title = null;
        if (root.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String)
            title = titleValue.GetString();

        var kind = string.Equals(kindText, "movie", StringComparison.OrdinalIgnoreCase)
            ? MediaKind.Movie
            : MediaKind.Series;
        title = CleanTitle(title);

        return act switch
        {
            "play_title" when title is not null => new MediaIntent(MediaAct.PlayTitle, kind, title),
            "play_title" => new MediaIntent(MediaAct.ContinueLast, kind, null),
            "play_last" => new MediaIntent(MediaAct.ContinueLast, kind, null),
            "close" => new MediaIntent(MediaAct.Close, kind, null),
            "none" => null,
            _ => throw new JsonException($"неизвестный act: {act}")
        };
    }

    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var words = title.ToLowerInvariant().Replace('ё', 'е')
            .Split([' ', '\t', '.', ',', '!', '?', '«', '»', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !TitleNoise.Contains(word)
                && !word.StartsWith("включ", StringComparison.Ordinal)
                && !word.StartsWith("запуст", StringComparison.Ordinal)
                && !word.StartsWith("сериал", StringComparison.Ordinal)
                && !word.StartsWith("фильм", StringComparison.Ordinal)
                && !word.StartsWith("кино", StringComparison.Ordinal))
            .ToArray();
        var text = string.Join(' ', words);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string Trim(string value) => value.Length <= 300 ? value : value[..300];
}
