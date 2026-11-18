using Microsoft.Extensions.Options;

namespace LocalAgent;

public sealed class ReadyAnnouncer : IHostedService
{
    private readonly SpeechBox _speech;
    private readonly StationSpeaker _station;
    private readonly IOptions<StationOptions> _options;
    private readonly IHostEnvironment _env;
    private readonly IHostApplicationLifetime _life;
    private readonly ILogger<ReadyAnnouncer> _logger;

    public ReadyAnnouncer(
        SpeechBox speech,
        StationSpeaker station,
        IOptions<StationOptions> options,
        IHostEnvironment env,
        IHostApplicationLifetime life,
        ILogger<ReadyAnnouncer> logger)
    {
        _speech = speech;
        _station = station;
        _options = options;
        _env = env;
        _life = life;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => Announce(_life.ApplicationStopping));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task Announce(CancellationToken ct)
    {
        var marker = Path.Combine(_env.ContentRootPath, "runtime", "speech-announced");
        if (File.Exists(marker))
            return;

        var told = false;
        var failures = 0;
        while (!ct.IsCancellationRequested && !File.Exists(marker) && failures < 3)
        {
            if (!_speech.PiperReady)
            {
                if (!told)
                {
                    _logger.LogInformation("жду синтез речи, потом станция скажет фразу");
                    told = true;
                }

                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                var phrase = string.IsNullOrWhiteSpace(_options.Value.Phrase)
                    ? "Хозяин, всё готово."
                    : _options.Value.Phrase.Trim();
                var wav = await _speech.SpeakAsync(phrase, ct);
                await _station.PlayAsync(wav, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                await File.WriteAllTextAsync(marker, phrase, ct);
                _logger.LogInformation("станция: «{phrase}»", phrase);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failures++;
                _logger.LogWarning("станция не сказала фразу: {msg}", ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
