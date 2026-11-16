using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuasarBridge.Data;
using QuasarBridge.Options;
using QuasarBridge.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<QuasarBridgeOptions>(builder.Configuration.GetSection(QuasarBridgeOptions.SectionName));
builder.Services.AddSingleton<ProxyState>();
builder.Services.AddSingleton<UpstreamDnsResolver>();
builder.Services.AddSingleton<MessageRouter>();
builder.Services.AddSingleton<LocalCommandHandler>();
builder.Services.AddSingleton<UniProxyService>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient("doh", c =>
{
    c.Timeout = TimeSpan.FromSeconds(5);
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "QuasarBridge/1.0");
});

var dbPathConfig = builder.Configuration["QuasarBridge:DatabasePath"] ?? "App_Data/quasar-bridge.db";
var dbPath = Path.IsPathRooted(dbPathConfig)
    ? dbPathConfig
    : Path.Combine(builder.Environment.ContentRootPath, dbPathConfig);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<QuasarBridgeDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath};Cache=Shared"));


builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
var earlyLogPath = builder.Configuration["QuasarBridge:LogFilePath"]
                   ?? Environment.GetEnvironmentVariable("QUASAR_LOG_FILE")
                   ?? "logs/quasar-bridge.log";
if (!Path.IsPathRooted(earlyLogPath))
    earlyLogPath = Path.Combine(builder.Environment.ContentRootPath, earlyLogPath);
builder.Logging.AddProvider(new SimpleFileLoggerProvider(earlyLogPath));

builder.WebHost.UseUrls("");

var configSection = builder.Configuration.GetSection(QuasarBridgeOptions.SectionName);
var tlsEnabled = configSection.GetValue("EnableTls", true);
var tlsCertPath = configSection.GetValue<string>("TlsCertPath") ?? "";
var tlsKeyPath = configSection.GetValue<string>("TlsKeyPath");
var tlsCertPassword = configSection.GetValue<string>("TlsCertPassword");
var tlsStoreThumbprint = configSection.GetValue<string>("TlsStoreThumbprint");
var tlsPort = configSection.GetValue("TlsPort", 443);
var httpPort = configSection.GetValue("HttpPort", 8443);
var tlsBind = configSection.GetValue<string>("TlsBindAddress");

X509Certificate2? tlsLeaf = null;
X509Certificate2Collection? tlsIntermediates = null;

if (tlsEnabled && !string.IsNullOrWhiteSpace(tlsCertPath))
{
    var contentRoot = builder.Environment.ContentRootPath;
    var fullCertPath = Path.IsPathRooted(tlsCertPath) ? tlsCertPath : Path.Combine(contentRoot, tlsCertPath);
    var fullKeyPath = string.IsNullOrWhiteSpace(tlsKeyPath)
        ? null
        : (Path.IsPathRooted(tlsKeyPath) ? tlsKeyPath : Path.Combine(contentRoot, tlsKeyPath));

    using var bootLog = LoggerFactory.Create(l => l.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
    var logger = bootLog.CreateLogger("QuasarBridge");
    logger.LogInformation("Loading TLS from cert={cert} key={key}", fullCertPath, fullKeyPath ?? "(pfx)");

    (tlsLeaf, tlsIntermediates) = TlsCertificateLoader.Load(fullCertPath, fullKeyPath, tlsCertPassword, tlsStoreThumbprint);
    logger.LogInformation(
        "TLS ready Subject={subject} NotAfter={notAfter:u} HasKey={hasKey} Intermediates={count}",
        tlsLeaf.Subject, tlsLeaf.NotAfter.ToUniversalTime(), tlsLeaf.HasPrivateKey, tlsIntermediates.Count);
}

builder.WebHost.ConfigureKestrel((_, options) =>
{
    if (tlsEnabled && tlsLeaf is not null)
    {
        var httpsOpts = new HttpsConnectionAdapterOptions
        {
            ServerCertificate = tlsLeaf,
            ServerCertificateChain = tlsIntermediates,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CheckCertificateRevocation = false
        };

        Action<ListenOptions> configureHttps = listenOptions =>
        {
            listenOptions.UseHttps(httpsOpts);
            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
        };

        IPAddress? bindIp = null;
        var hasBind = !string.IsNullOrWhiteSpace(tlsBind) && IPAddress.TryParse(tlsBind, out bindIp);

        if (hasBind)
            options.Listen(bindIp!, tlsPort, configureHttps);
        else
            options.ListenAnyIP(tlsPort, configureHttps);

        var altTlsPort = httpPort;
        if (altTlsPort != tlsPort)
        {
            if (hasBind)
                options.Listen(bindIp!, altTlsPort, configureHttps);
            else
                options.ListenAnyIP(altTlsPort, configureHttps);
        }

        options.Listen(IPAddress.Loopback, httpPort, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http1;
        });
    }
    else
    {
        options.ListenAnyIP(httpPort, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http1;
        });
    }
});

var app = builder.Build();
var opts = app.Services.GetRequiredService<IOptions<QuasarBridgeOptions>>().Value;

opts.ListenUrl = Environment.GetEnvironmentVariable("QUASAR_LISTEN") ?? opts.ListenUrl;
opts.UpstreamUrl = Environment.GetEnvironmentVariable("QUASAR_UPSTREAM") ?? opts.UpstreamUrl;
opts.Mode = Environment.GetEnvironmentVariable("QUASAR_MODE") ?? opts.Mode;
opts.DatabasePath = dbPath;
if (bool.TryParse(Environment.GetEnvironmentVariable("QUASAR_SWAGGER"), out var sw)) opts.SwaggerEnabled = sw;
if (bool.TryParse(Environment.GetEnvironmentVariable("QUASAR_OPEN_SWAGGER"), out var osw)) opts.OpenSwaggerOnStart = osw;

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuasarBridgeDbContext>();
    await DbInitializer.InitializeAsync(db, dbPath, app.Logger);
}

var router = app.Services.GetRequiredService<MessageRouter>();
await router.ReloadAsync();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

if (opts.SwaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation(
        "QuasarBridge mode={mode} http=:{http} https={https}:{tls} path=/uni.ws upstream={upstream} db={db} routing={rev}/{rules}",
        opts.Mode, httpPort, tlsBind ?? "0.0.0.0", tlsPort, opts.UpstreamUrl, dbPath, router.Revision, router.RuleCount);

    if (opts.SwaggerEnabled)
    {
        var swaggerUrl = $"http://127.0.0.1:{httpPort}/swagger";
        app.Logger.LogInformation("Swagger UI: {url}", swaggerUrl);
        if (opts.OpenSwaggerOnStart)
        {
            try { Process.Start(new ProcessStartInfo { FileName = swaggerUrl, UseShellExecute = true }); }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Failed to open Swagger"); }
        }
    }
});

app.Run();
