using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace LocalAgent;

/// <summary>
/// Piper пишет WAV в ответ сервиса. Файл не воспроизводится на колонках этого ПК.
/// </summary>
public sealed class SpeechBox
{
    private readonly IHostEnvironment _env;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<SpeechBox> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SpeechBox(IOptions<AgentOptions> options, IHostEnvironment env, ILogger<SpeechBox> logger)
    {
        _options = options;
        _env = env;
        _logger = logger;
    }

    public bool PiperReady => FindPiper() is not null && FindVoice() is not null;

    public async Task<byte[]> SpeakAsync(string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0)
            throw new ArgumentException("Пустой текст");
        if (text.Length > 400)
            text = text[..400];

        var piper = FindPiper() ?? throw new InvalidOperationException("Piper не найден");
        var voice = FindVoice() ?? throw new InvalidOperationException("Голос ru_RU-irina не найден");
        var wavPath = Path.Combine(Path.GetTempPath(), "local-agent-" + Guid.NewGuid().ToString("n") + ".wav");
        var textPath = wavPath + ".txt";

        await _gate.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(textPath, text + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c \"\"{piper}\" -m \"{voice}\" -f \"{wavPath}\" -q < \"{textPath}\"\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(piper) ?? _env.ContentRootPath
            };

            using var process = new Process { StartInfo = psi };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            process.Start();
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("Piper не успел собрать речь");
            }

            var error = await errorTask;
            if (process.ExitCode != 0 || !File.Exists(wavPath))
                throw new InvalidOperationException("Piper не собрал речь: " + Trim(error));

            var bytes = await File.ReadAllBytesAsync(wavPath, ct);
            var duration = WavDuration(bytes);
            _logger.LogInformation("речь {chars} знаков, {ms} мс, {bytes} байт", text.Length, duration, bytes.Length);
            return bytes;
        }
        finally
        {
            _gate.Release();
            try { File.Delete(wavPath); } catch { }
            try { File.Delete(textPath); } catch { }
        }
    }

    private string? FindPiper()
    {
        var configured = Resolve(_options.Value.PiperPath);
        if (File.Exists(configured))
            return configured;
        var runtime = Path.Combine(_env.ContentRootPath, "runtime");
        if (!Directory.Exists(runtime))
            return null;
        return Directory.EnumerateFiles(runtime, "piper.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    private string? FindVoice()
    {
        var configured = Resolve(_options.Value.VoicePath);
        if (File.Exists(configured))
            return configured;
        var runtime = Path.Combine(_env.ContentRootPath, "runtime");
        if (!Directory.Exists(runtime))
            return null;
        return Directory.EnumerateFiles(runtime, "*.onnx", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Length > 1_000_000 && File.Exists(file.FullName + ".json"))
            .OrderByDescending(file => file.Length)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(_env.ContentRootPath, path);

    private static int WavDuration(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 44 || wav[0] != (byte)'R' || wav[1] != (byte)'I')
            return 0;
        var sampleRate = BitConverter.ToInt32(wav.Slice(24, 4));
        var byteRate = BitConverter.ToInt32(wav.Slice(28, 4));
        if (sampleRate <= 0 || byteRate <= 0)
            return 0;
        return (int)((wav.Length - 44) * 1000L / byteRate);
    }

    private static string Trim(string value) => value.Length <= 300 ? value : value[..300];
}
