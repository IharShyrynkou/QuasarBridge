using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAgent;

public sealed partial class WebSearch
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<WebSearch> _logger;

    public WebSearch(IHttpClientFactory http, ILogger<WebSearch> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<string> SearchAsync(string? query, CancellationToken ct)
    {
        query = query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return "Пустой запрос.";
        if (query.Length > 200)
            query = query[..200];

        try
        {
            var parts = new List<string>();
            var official = await OfficialRatesAsync(query, ct);
            if (!string.IsNullOrWhiteSpace(official))
                parts.Add(official);
            var instant = await InstantAsync(query, ct);
            if (!string.IsNullOrWhiteSpace(instant))
                parts.Add(instant);
            var pages = await HtmlAsync(query, ct);
            if (!string.IsNullOrWhiteSpace(pages))
                parts.Add(pages);
            return parts.Count == 0 ? "Поиск ничего не вернул." : Join(parts);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "Поиск не ответил.";
        }
    }

    private async Task<string> InstantAsync(string query, CancellationToken ct)
    {
        var url = "https://api.duckduckgo.com/?format=json&no_html=1&skip_disambig=1&q=" + Uri.EscapeDataString(query);
        var client = _http.CreateClient("search");
        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return "";
        var body = await ReadTextAsync(response.Content, ct);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var lines = new List<string>();
        Add(lines, Text(root, "Answer"));
        Add(lines, Text(root, "AbstractText"));
        if (root.TryGetProperty("RelatedTopics", out var topics))
            Walk(topics, lines);
        return Join(lines);
    }

    private async Task<string> OfficialRatesAsync(string query, CancellationToken ct)
    {
        var codes = RateCodes(query);
        if (codes.Length == 0)
            return "";
        var client = _http.CreateClient("search");
        using var response = await client.GetAsync("https://www.cbr.ru/scripts/XML_daily.asp", ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("цб ответил {code}", (int)response.StatusCode);
            return "";
        }
        var xml = await ReadTextAsync(response.Content, ct);
        _logger.LogInformation("цб {chars} знаков", xml.Length);
        var date = DatePattern().Match(xml);
        var lines = new List<string>();
        foreach (var code in codes)
        {
            var value = Regex.Match(xml, $"<CharCode>{code}</CharCode>.*?<Value>([^<]+)</Value>", RegexOptions.Singleline);
            if (!value.Success)
                continue;
            var when = date.Success ? date.Groups[1].Value : "сегодня";
            lines.Add($"ЦБ на {when}: 1 {code} = {value.Groups[1].Value} руб.");
        }

        return lines.Count == 0 ? "" : string.Join("\n", lines);
    }

    private async Task<string> HtmlAsync(string query, CancellationToken ct)
    {
        var client = _http.CreateClient("search");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = query })
        };
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return "";
        var html = await ReadTextAsync(response.Content, ct);
        var lines = new List<string>();
        string? firstUrl = null;
        foreach (Match link in ResultLinkPattern().Matches(html))
        {
            var url = link.Groups[1].Value;
            var title = Clean(link.Groups[2].Value);
            if (firstUrl is null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
                firstUrl = WebUtility.HtmlDecode(url);
            Add(lines, title);
            if (lines.Count >= 4)
                break;
        }

        if (firstUrl is not null)
        {
            var extract = await PageExtractAsync(firstUrl, query, ct);
            if (!string.IsNullOrWhiteSpace(extract))
                lines.Insert(0, extract);
        }

        return Join(lines);
    }

    private async Task<string> PageExtractAsync(string url, string query, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var client = _http.CreateClient("search");
            using var response = await client.GetAsync(url, budget.Token);
            if (!response.IsSuccessStatusCode)
                return "";
            var html = await ReadTextAsync(response.Content, budget.Token);
            if (html.Length > 250_000)
                html = html[..250_000];
            html = ScriptPattern().Replace(html, " ");
            var text = Clean(html);
            var needle = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(word => word.Length >= 4) ?? query;
            var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                at = 0;
            var start = Math.Max(0, at - 180);
            var length = Math.Min(700, text.Length - start);
            return length <= 0 ? "" : text.Substring(start, length);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "";
        }
    }

    private static async Task<string> ReadTextAsync(HttpContent content, CancellationToken ct)
    {
        var bytes = await content.ReadAsByteArrayAsync(ct);
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 240));
        if (head.Contains("windows-1251", StringComparison.OrdinalIgnoreCase))
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string[] RateCodes(string query)
    {
        var text = query.ToLowerInvariant().Replace('ё', 'е');
        var codes = new List<string>();
        if (text.Contains("доллар", StringComparison.Ordinal))
            codes.Add("USD");
        if (text.Contains("евро", StringComparison.Ordinal))
            codes.Add("EUR");
        if (text.Contains("юан", StringComparison.Ordinal))
            codes.Add("CNY");
        if (text.Contains("фунт", StringComparison.Ordinal))
            codes.Add("GBP");
        if (codes.Count == 0 && text.Contains("валют", StringComparison.Ordinal))
            codes.AddRange(["USD", "EUR"]);
        return codes.ToArray();
    }

    private static void Walk(JsonElement element, List<string> lines)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                Walk(child, lines);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;
        Add(lines, Text(element, "Text"));
        if (element.TryGetProperty("Topics", out var topics))
            Walk(topics, lines);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void Add(List<string> lines, string? line)
    {
        line = line?.Trim();
        if (string.IsNullOrWhiteSpace(line) || lines.Count >= 4 || lines.Contains(line))
            return;
        lines.Add(line);
    }

    private static string Join(List<string> lines)
    {
        if (lines.Count == 0)
            return "";
        var text = string.Join("\n", lines.Select((line, index) => $"{index + 1}. {line}"));
        return text.Length <= 2500 ? text : text[..2500];
    }

    private static string Clean(string html)
    {
        var text = TagPattern().Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        return SpacePattern().Replace(text, " ").Trim();
    }

    [GeneratedRegex("class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ResultLinkPattern();

    [GeneratedRegex("Date=\"([^\"]+)\"")]
    private static partial Regex DatePattern();

    [GeneratedRegex("<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptPattern();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();

    [GeneratedRegex("\\s+")]
    private static partial Regex SpacePattern();
}
