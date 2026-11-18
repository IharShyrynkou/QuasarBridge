using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LocalAgent;

public sealed class AgentBrain
{
    private const string CommandRules = """
        Из токенов речи собери одну команду. Мусор выбрасывай: «подключите яндекс станцию к экрану», «чтобы смотреть видеоролики», «фильмы», «сериалы».
        Верни только JSON:
        {"act":"play_title","kind":"series","title":"рик и морти","seconds":0,"script":null,"args":[],"say":"Включаю Рик и Морти"}
        act: play_title, play_last, close, pause, seek, next_episode, previous_episode, next_season, script, say, none.
        kind: series или movie.
        title: название без слов «включи», «сериал», «фильм». Если названия нет — null.
        seconds: для seek, вперёд плюс, назад минус. Иначе 0.
        script: имя файла из списка или null. args: аргументы этого скрипта.
        say: короткая фраза, которую можно произнести.
        play_title — в токенах есть название. play_last — включить без названия. close — выключить или закрыть. pause — пауза.
        seek — перемотка. next_episode, previous_episode, next_season — серия или сезон.
        script — только если человек просит запустить скрипт из списка. say — обычный вопрос без команды плеера. none — не разобрать.
        Не выдумывай название и не выдумывай скрипт.
        """;

    private readonly OllamaChat _ollama;
    private readonly ScriptBox _scripts;
    private readonly WebSearch _search;
    private readonly SpeechBox _speech;
    private readonly ILogger<AgentBrain> _logger;

    public AgentBrain(
        OllamaChat ollama,
        ScriptBox scripts,
        WebSearch search,
        SpeechBox speech,
        ILogger<AgentBrain> logger)
    {
        _ollama = ollama;
        _scripts = scripts;
        _search = search;
        _speech = speech;
        _logger = logger;
    }

    public async Task<HealthReport> HealthAsync(CancellationToken ct) => new(
        await _ollama.ModelReadyAsync(ct),
        _ollama.Model,
        _speech.PiperReady,
        _scripts.List());

    public async Task<long> WarmAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await _ollama.CompleteAsync(
            [new ChatTurn("user", "Верни JSON {\"ok\":true}")],
            32,
            TimeSpan.FromSeconds(180),
            ct);
        return watch.ElapsedMilliseconds;
    }

    public async Task<CommandResult> CommandAsync(IReadOnlyList<string> tokens, CancellationToken ct)
    {
        var clean = tokens
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .Take(40)
            .Select(token => token.Length <= 200 ? token : token[..200])
            .ToArray();
        if (clean.Length == 0)
            throw new ArgumentException("Нужны токены");

        var system = CommandRules + "\nСкрипты: " + string.Join(", ", _scripts.List());
        var watch = Stopwatch.StartNew();
        var raw = await _ollama.CompleteAsync(
            [new ChatTurn("system", system), new ChatTurn("user", string.Join('\n', clean))],
            180,
            TimeSpan.FromSeconds(15),
            ct);
        var parsed = ParseCommand(raw);
        string? output = null;
        int? exitCode = null;
        string? error = parsed.Error;
        if (parsed.Act == "script" && error is null)
        {
            try
            {
                var run = _scripts.Run(parsed.Script, parsed.Args);
                output = run.Output;
                exitCode = run.ExitCode;
                if (string.IsNullOrWhiteSpace(parsed.Say))
                    parsed = parsed with { Say = run.ExitCode == 0 ? "Готово" : "Скрипт не запустился" };
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
            }
        }

        watch.Stop();
        _logger.LogInformation(
            "команда за {ms} мс: {act} {detail}",
            watch.ElapsedMilliseconds,
            parsed.Act,
            parsed.Title ?? parsed.Script ?? parsed.Say);
        return parsed with { Output = output, ExitCode = exitCode, Ms = watch.ElapsedMilliseconds, Error = error };
    }

    public async Task<AskResult> AskAsync(string text, bool search, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0)
            throw new ArgumentException("Нужен текст");
        if (text.Length > 2000)
            text = text[..2000];

        var scripts = string.Join(", ", _scripts.List());
        var system = """
            Ты локальный помощник. Отвечай только JSON.
            Если нужен поиск и в сообщении сказано, что поиск разрешён: {"tool":"web_search","query":"короткий запрос"}
            Если нужно запустить скрипт из списка: {"tool":"run_script","script":"имя.ps1","args":[]}
            Иначе: {"tool":null,"say":"короткая фраза по-русски, которую можно произнести"}
            Не выдумывай скрипты. Не вызывай поиск, если он запрещён. После результата инструмента верни JSON с tool null и say.
            Цифры бери только из результата поиска. Если цифры там нет, скажи, что точное значение не попалось, и не придумывай его.
            """ + "\nСкрипты: " + (scripts.Length == 0 ? "нет" : scripts);

        var messages = new List<ChatTurn>
        {
            new("system", system),
            new("user", (search ? "Поиск разрешён." : "Поиск запрещён.") + "\n" + text)
        };
        var used = new List<string>();
        var say = "";
        string? error = null;
        var watch = Stopwatch.StartNew();

        for (var round = 0; round < 3; round++)
        {
            var raw = await _ollama.CompleteAsync(messages, 220, TimeSpan.FromSeconds(25), ct);
            var call = ParseAsk(raw);
            if (call.Tool is null)
            {
                say = call.Say;
                break;
            }

            messages.Add(new ChatTurn("assistant", raw));
            if (call.Tool == "web_search")
            {
                if (!search)
                {
                    used.Add("web_search:выключен");
                    messages.Add(new ChatTurn("user", "Поиск выключен. Верни JSON {\"tool\":null,\"say\":\"...\"} без поиска."));
                    continue;
                }

                var found = await _search.SearchAsync(call.Query, ct);
                used.Add("web_search");
                _logger.LogInformation("поиск: {query} → {found}", call.Query, found.Length <= 400 ? found : found[..400]);
                messages.Add(new ChatTurn("user", "Результат поиска:\n" + found + "\nВерни JSON {\"tool\":null,\"say\":\"короткий ответ\"}."));
                continue;
            }

            if (call.Tool == "run_script")
            {
                try
                {
                    var run = _scripts.Run(call.Script, call.Args);
                    used.Add("run_script:" + run.Name);
                    messages.Add(new ChatTurn(
                        "user",
                        $"Код {run.ExitCode}. Вывод:\n{run.Output}\nВерни JSON {{\"tool\":null,\"say\":\"коротко что вышло\"}}."));
                }
                catch (ArgumentException ex)
                {
                    used.Add("run_script:ошибка");
                    messages.Add(new ChatTurn("user", ex.Message + " Верни JSON {\"tool\":null,\"say\":\"...\"}."));
                }

                continue;
            }

            messages.Add(new ChatTurn("user", "Такого инструмента нет. Верни JSON {\"tool\":null,\"say\":\"...\"}."));
        }

        if (string.IsNullOrWhiteSpace(say))
            error = "модель не дала фразу";
        watch.Stop();
        _logger.LogInformation("ответ за {ms} мс: {say}", watch.ElapsedMilliseconds, say);
        return new AskResult(say, used.ToArray(), watch.ElapsedMilliseconds, error);
    }

    public Task<byte[]> SpeakAsync(string text, CancellationToken ct) => _speech.SpeakAsync(text, ct);

    internal static CommandResult ParseCommand(string content)
    {
        JsonElement root;
        try
        {
            root = ReadObject(content);
        }
        catch (JsonException)
        {
            return new CommandResult { Act = "none", Error = "модель не вернула json" };
        }

        var act = StringOf(root, "act")?.Trim().ToLowerInvariant();
        if (act is not (
            "play_title" or "play_last" or "close" or "pause" or "seek"
            or "next_episode" or "previous_episode" or "next_season"
            or "script" or "say" or "none"))
            act = "none";

        var kind = string.Equals(StringOf(root, "kind"), "movie", StringComparison.OrdinalIgnoreCase) ? "movie" : "series";
        var title = CleanTitle(StringOf(root, "title"));
        var seconds = 0;
        if (root.TryGetProperty("seconds", out var secondsValue) && secondsValue.TryGetInt32(out var parsedSeconds))
            seconds = Math.Clamp(parsedSeconds, -86_400, 86_400);
        var script = FileName(StringOf(root, "script"));
        var args = Strings(root, "args");
        var say = StringOf(root, "say")?.Trim() ?? "";
        if (say.Length > 400)
            say = say[..400];
        if (act == "play_title" && title is null)
            act = "play_last";
        if (act == "none")
            say = "";
        if (act == "script" && script is null)
            return new CommandResult { Act = "none", Kind = kind, Say = "", Error = "скрипт не назван" };

        return new CommandResult
        {
            Act = act,
            Kind = kind,
            Title = title,
            Seconds = seconds,
            Script = script,
            Args = args,
            Say = say
        };
    }

    private static AskCall ParseAsk(string content)
    {
        try
        {
            var root = ReadObject(content);
            var tool = StringOf(root, "tool")?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(tool) || tool is "null" or "none")
                tool = null;
            var say = StringOf(root, "say")?.Trim() ?? "";
            if (say.Length > 400)
                say = say[..400];
            return new AskCall(tool, say, StringOf(root, "query"), FileName(StringOf(root, "script")), Strings(root, "args"));
        }
        catch (JsonException)
        {
            var say = content.Trim();
            if (say.Length > 400)
                say = say[..400];
            return new AskCall(null, say, null, null, []);
        }
    }

    private static JsonElement ReadObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new JsonException("нет объекта");
        using var document = JsonDocument.Parse(content[start..(end + 1)]);
        return document.RootElement.Clone();
    }

    private static string? StringOf(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string[] Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .Take(8)
            .ToArray();
    }

    private static string? FileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        name = name.Trim();
        return name == Path.GetFileName(name) ? name : null;
    }

    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Equals("null", StringComparison.OrdinalIgnoreCase))
            return null;
        var words = title.ToLowerInvariant().Replace('ё', 'е')
            .Split([' ', '\t', '.', ',', '!', '?', '«', '»', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word is not ("включи" or "включите" or "включить" or "запусти" or "запустите"
                or "сериал" or "сериала" or "фильм" or "фильма" or "кино" or "пожалуйста" or "алиса"))
            .ToArray();
        var text = string.Join(' ', words);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

public sealed record CommandResult
{
    public string Act { get; init; } = "none";
    public string Kind { get; init; } = "series";
    public string? Title { get; init; }
    public int Seconds { get; init; }
    public string? Script { get; init; }
    public string[] Args { get; init; } = [];
    public string Say { get; init; } = "";
    public string? Output { get; init; }
    public int? ExitCode { get; init; }
    public long Ms { get; init; }
    public string? Error { get; init; }
}

public sealed record AskResult(string Say, string[] Tools, long Ms, string? Error);

public sealed record HealthReport(bool ModelReady, string Model, bool SpeechReady, IReadOnlyList<string> Scripts);

public sealed record AskCall(string? Tool, string Say, string? Query, string? Script, string[] Args);
