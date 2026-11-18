namespace LocalAgent;

public sealed class ModelWarmup : IHostedService
{
    private readonly AgentBrain _brain;
    private readonly IHostApplicationLifetime _life;
    private readonly ILogger<ModelWarmup> _logger;

    public ModelWarmup(AgentBrain brain, IHostApplicationLifetime life, ILogger<ModelWarmup> logger)
    {
        _brain = brain;
        _life = life;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => Loop(_life.ApplicationStopping));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task Loop(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 40 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                var ms = await _brain.WarmAsync(ct);
                _logger.LogInformation("модель прогрета за {ms} мс", ms);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("модель ещё не готова: {msg}", ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
