using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LocalAgent;

public sealed class OllamaChat
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<OllamaChat> _logger;
    private readonly string _endpoint;
    private readonly string _tags;
    private readonly string _model;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OllamaChat(IOptions<AgentOptions> options, IHttpClientFactory http, ILogger<OllamaChat> logger)
    {
        _http = http;
        _logger = logger;
        var root = options.Value.OllamaUrl.TrimEnd('/');
        _endpoint = root + "/api/chat";
        _tags = root + "/api/tags";
        _model = string.IsNullOrWhiteSpace(options.Value.Model) ? "qwen3.5:4b" : options.Value.Model;
    }

    public string Model => _model;

    public async Task<bool> ModelReadyAsync(CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient("ollama");
            using var response = await client.GetAsync(_tags, ct);
            if (!response.IsSuccessStatusCode)
                return false;
            var body = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("models", out var models))
                return false;
            foreach (var model in models.EnumerateArray())
            {
                var name = model.TryGetProperty("name", out var value) ? value.GetString() : null;
                if (name is not null && name.StartsWith(_model, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<string> CompleteAsync(
        IReadOnlyList<ChatTurn> messages,
        int numPredict,
        TimeSpan budget,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(budget);
            var payload = JsonSerializer.Serialize(new
            {
                model = _model,
                stream = false,
                think = false,
                keep_alive = "24h",
                format = "json",
                options = new { temperature = 0, num_predict = numPredict, num_ctx = 4096 },
                messages = messages.Select(m => new { role = m.Role, content = m.Content })
            });

            var client = _http.CreateClient("ollama");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Ollama вернула {(int)response.StatusCode}: {Trim(body)}");

            using var document = JsonDocument.Parse(body);
            var message = document.RootElement.GetProperty("message");
            var content = message.TryGetProperty("content", out var contentValue) ? contentValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                var thinking = message.TryGetProperty("thinking", out var thinkingValue) ? thinkingValue.GetString() : null;
                if (!string.IsNullOrWhiteSpace(thinking))
                    _logger.LogDebug("модель вернула размышление без ответа, {chars} знаков", thinking.Length);
                throw new InvalidOperationException("Пустой ответ модели");
            }

            return content;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Trim(string value) => value.Length <= 300 ? value : value[..300];
}

public sealed record ChatTurn(string Role, string Content);
