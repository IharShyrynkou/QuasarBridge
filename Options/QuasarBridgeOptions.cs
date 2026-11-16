namespace QuasarBridge.Options;

public class QuasarBridgeOptions
{
    public const string SectionName = "QuasarBridge";

    public string ListenUrl { get; set; } = "http://127.0.0.1:8443";
    public string UpstreamUrl { get; set; } = "wss://uniproxy.alice.yandex.net/uni.ws";
    public bool SwaggerEnabled { get; set; } = true;
    public bool OpenSwaggerOnStart { get; set; } = false;
    public string LogFilePath { get; set; } = "logs/quasar-bridge.log";

    /// <summary>Relative or absolute path to SQLite database file.</summary>
    public string DatabasePath { get; set; } = "App_Data/quasar-bridge.db";

    /// <summary>observe = log+forward; hybrid = apply DB routing rules for local handling.</summary>
    public string Mode { get; set; } = "hybrid";

    public bool EnableTls { get; set; } = true;
    public string TlsCertPath { get; set; } = @"C:\Users\Admin\Pictures\certs\fullchain.pem";
    public string TlsKeyPath { get; set; } = @"C:\Users\Admin\Pictures\certs\quasar-all.key";
    public string TlsCertPassword { get; set; } = "";
    /// <summary>Optional thumbprint in CurrentUser\My. Empty = auto-detect CN=quasar.yandex.net.</summary>
    public string TlsStoreThumbprint { get; set; } = "1B607464F66256BD73B6A9BB6B7FFCD33A345C68";
    public int TlsPort { get; set; } = 443;
    public int HttpPort { get; set; } = 8443;

    /// <summary>Bind HTTPS only on this address (e.g. 192.168.0.176). Empty = any.</summary>
    public string? TlsBindAddress { get; set; } = "192.168.0.176";

    public bool ResolveUpstreamViaDoh { get; set; } = true;
    public string[] AllowedClientIps { get; set; } = Array.Empty<string>();
}
