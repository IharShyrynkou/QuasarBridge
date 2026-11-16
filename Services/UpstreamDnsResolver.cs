using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace QuasarBridge.Services;

/// <summary>
/// Resolves the real upstream host via DoH so the bridge does not loop back
/// when AdGuard/OpenWrt rewrites uniproxy.alice.yandex.net to this PC.
/// </summary>
public sealed class UpstreamDnsResolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UpstreamDnsResolver> _logger;
    private readonly object _lock = new();
    private IPAddress[] _cached = Array.Empty<IPAddress>();
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(5);

    public UpstreamDnsResolver(IHttpClientFactory httpClientFactory, ILogger<UpstreamDnsResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_cached.Length > 0 && DateTimeOffset.UtcNow < _expiresAt)
                return _cached;
        }

        var addresses = await ResolveViaDohAsync(host, ct);
        if (addresses.Length == 0)
        {
            // Last resort: system DNS (may already be rewritten — only useful before AdGuard rule).
            addresses = (await Dns.GetHostAddressesAsync(host, ct))
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .ToArray();
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException($"Cannot resolve upstream host {host}");

        // Drop loopback / private answers — those mean rewrite already hit us.
        var publicOnes = addresses.Where(a => !IsPrivateOrLoopback(a)).ToArray();
        if (publicOnes.Length == 0)
            throw new InvalidOperationException($"Upstream {host} resolved only to private/loopback addresses ({string.Join(',', addresses)}). DoH failed and DNS rewrite is active.");

        lock (_lock)
        {
            _cached = publicOnes;
            _expiresAt = DateTimeOffset.UtcNow + _ttl;
        }

        _logger.LogInformation("Upstream {host} resolved via DoH to {ips}", host, string.Join(',', publicOnes.Select(a => a.ToString())));
        return publicOnes;
    }

    private async Task<IPAddress[]> ResolveViaDohAsync(string host, CancellationToken ct)
    {
        var endpoints = new[]
        {
            $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A",
            $"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=A"
        };

        var client = _httpClientFactory.CreateClient("doh");
        foreach (var url in endpoints)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var payload = await resp.Content.ReadFromJsonAsync<DohResponse>(cancellationToken: ct);
                if (payload?.Answer is null) continue;
                var ips = payload.Answer
                    .Where(a => a.Type == 1 && !string.IsNullOrWhiteSpace(a.Data))
                    .Select(a => IPAddress.TryParse(a.Data, out var ip) ? ip : null)
                    .Where(ip => ip is not null)
                    .Cast<IPAddress>()
                    .ToArray();
                if (ips.Length > 0) return ips;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DoH resolve failed for {url}", url);
            }
        }

        return Array.Empty<IPAddress>();
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168)
               || (bytes[0] == 169 && bytes[1] == 254);
    }

    private sealed class DohResponse
    {
        [JsonPropertyName("Answer")]
        public List<DohAnswer>? Answer { get; set; }
    }

    private sealed class DohAnswer
    {
        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }
}
