using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace LocalAgent;

/// <summary>
/// Кладёт wav на колонку станции. Колонки этого ПК не трогает.
/// </summary>
public sealed class StationSpeaker
{
    private readonly StationOptions _options;
    private readonly ILogger<StationSpeaker> _logger;

    public StationSpeaker(IOptions<StationOptions> options, ILogger<StationSpeaker> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task PlayAsync(byte[] wav, CancellationToken ct)
    {
        if (!File.Exists(_options.KeyPath))
            throw new InvalidOperationException("Нет SSH-ключа станции");

        var local = Path.Combine(Path.GetTempPath(), "alice-ready-" + Guid.NewGuid().ToString("n") + ".wav");
        try
        {
            await File.WriteAllBytesAsync(local, WavPcm.ToMono48K(wav), ct);
            var remote = "/data/local/tmp/alice-ready.wav";
            await CopyAsync(local, remote, ct);

            var played = await RunAsync("ssh",
            [
                "-i", _options.KeyPath,
                "-o", "IdentitiesOnly=yes",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", "ConnectTimeout=8",
                "-o", "BatchMode=yes",
                $"{_options.User}@{_options.Host}",
                $"tinyplay2 {remote} -D {_options.Card} -d {_options.Device}"
            ], ct);
            if (played.Code != 0)
                throw new InvalidOperationException("Станция не воспроизвела речь: " + Trim(played.Text));

            _logger.LogInformation("станция сказала фразу");
        }
        finally
        {
            try { File.Delete(local); } catch { }
        }
    }

    private async Task CopyAsync(string local, string remote, CancellationToken ct)
    {
        var psi = Ssh();
        psi.RedirectStandardInput = true;
        psi.ArgumentList.Add($"cat > {remote}");
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        await using (var file = File.OpenRead(local))
            await file.CopyToAsync(process.StandardInput.BaseStream, ct);
        process.StandardInput.Close();
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Не удалось отправить речь на станцию: " + Trim(await stderr + await stdout));
    }

    private ProcessStartInfo Ssh()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(_options.KeyPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("IdentitiesOnly=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ConnectTimeout=8");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add($"{_options.User}@{_options.Host}");
        return psi;
    }

    private static async Task<(int Code, string Text)> RunAsync(string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var text = string.Join('\n', new[] { await stdout, await stderr }.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
        return (process.ExitCode, text);
    }

    private static string Trim(string value) => value.Length <= 300 ? value : value[..300];
}
