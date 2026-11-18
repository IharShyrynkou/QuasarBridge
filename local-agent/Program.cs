using LocalAgent;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<StationOptions>(builder.Configuration.GetSection(StationOptions.SectionName));
builder.Services.AddHttpClient("ollama", client => client.Timeout = TimeSpan.FromMinutes(3));
builder.Services.AddHttpClient("search", client =>
{
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
});
builder.Services.AddSingleton<OllamaChat>();
builder.Services.AddSingleton<ScriptBox>();
builder.Services.AddSingleton<WebSearch>();
builder.Services.AddSingleton<SpeechBox>();
builder.Services.AddSingleton<StationSpeaker>();
builder.Services.AddSingleton<AgentBrain>();
builder.Services.AddHostedService<ModelWarmup>();
builder.Services.AddHostedService<ReadyAnnouncer>();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);

var listen = builder.Configuration["Agent:Listen"] ?? "http://127.0.0.1:8787";
if (!listen.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
    && !listen.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Сервис слушает только localhost");
builder.WebHost.UseUrls(listen);

var app = builder.Build();

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error is ArgumentException ? StatusCodes.Status400BadRequest : StatusCodes.Status503ServiceUnavailable;
    await context.Response.WriteAsJsonAsync(new { error = error?.Message ?? "ошибка" });
}));

app.MapGet("/health", async (AgentBrain brain, CancellationToken ct) => await brain.HealthAsync(ct));

app.MapPost("/v1/command", async (CommandRequest request, AgentBrain brain, CancellationToken ct) =>
{
    var tokens = request.Tokens?
        .Where(token => !string.IsNullOrWhiteSpace(token))
        .Select(token => token.Trim())
        .ToArray() ?? [];
    if (tokens.Length == 0 && !string.IsNullOrWhiteSpace(request.Text))
    {
        tokens = request.Text.Split(['\n', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    var result = await brain.CommandAsync(tokens, ct);
    return Results.Ok(result);
});

app.MapPost("/v1/ask", async (AskRequest request, AgentBrain brain, CancellationToken ct) =>
{
    var result = await brain.AskAsync(request.Text ?? "", request.Search, ct);
    return Results.Ok(result);
});

app.MapPost("/v1/speak", async (SpeakRequest request, AgentBrain brain, CancellationToken ct) =>
{
    var wav = await brain.SpeakAsync(request.Text ?? "", ct);
    return Results.File(wav, "audio/wav");
});

app.MapGet("/v1/scripts", (ScriptBox scripts) => scripts.List());

app.MapPost("/v1/scripts/run", (RunRequest request, ScriptBox scripts) =>
{
    try
    {
        return Results.Ok(scripts.Run(request.Name, request.Args));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Logger.LogInformation("локальный агент на {url}, модель {model}", listen, app.Services.GetRequiredService<IOptions<AgentOptions>>().Value.Model);
app.Run();

public sealed class CommandRequest
{
    public string[]? Tokens { get; set; }
    public string? Text { get; set; }
}

public sealed class AskRequest
{
    public string? Text { get; set; }
    public bool Search { get; set; }
}

public sealed class SpeakRequest
{
    public string? Text { get; set; }
}

public sealed class RunRequest
{
    public string? Name { get; set; }
    public string[]? Args { get; set; }
}
