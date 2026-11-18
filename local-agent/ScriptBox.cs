using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace LocalAgent;

public sealed class ScriptBox
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".cmd", ".bat", ".py"
    };

    private readonly string _root;
    private readonly ILogger<ScriptBox> _logger;

    public ScriptBox(IOptions<AgentOptions> options, IHostEnvironment env, ILogger<ScriptBox> logger)
    {
        _logger = logger;
        var path = options.Value.ScriptsPath;
        _root = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path));
        Directory.CreateDirectory(_root);
    }

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_root))
            return [];
        return Directory.EnumerateFiles(_root)
            .Select(Path.GetFileName)
            .Where(name => name is not null && Extensions.Contains(Path.GetExtension(name)))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ScriptResult Run(string? name, IReadOnlyList<string>? args)
    {
        var full = Resolve(name);
        var ext = Path.GetExtension(full);
        var safeArgs = (args ?? [])
            .Where(arg => !string.IsNullOrWhiteSpace(arg))
            .Select(arg => arg.Trim())
            .Take(8)
            .ToArray();
        foreach (var arg in safeArgs)
        {
            if (arg.Length > 200)
                throw new ArgumentException("Аргумент скрипта длиннее 200 знаков");
            if (arg.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("В аргументе скрипта есть перевод строки");
        }

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _root,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "powershell.exe";
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(PowerShellCommand(full, safeArgs));
        }
        else if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "py";
            psi.ArgumentList.Add("-3");
            psi.ArgumentList.Add(full);
            foreach (var arg in safeArgs)
                psi.ArgumentList.Add(arg);
        }
        else
        {
            foreach (var arg in safeArgs)
            {
                if (arg.IndexOfAny(['"', '&', '|', '>', '<', '%', '^', '!']) >= 0)
                    throw new ArgumentException("В аргументе cmd есть служебный символ");
            }

            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(BuildCmd(full, safeArgs));
        }

        _logger.LogInformation("скрипт: {name}", Path.GetFileName(full));
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new ScriptResult(Path.GetFileName(full)!, -1, "скрипт превысил 20 секунд");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        var output = string.Join('\n', new[] { stdout, stderr }.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
        if (output.Length > 4000)
            output = output[..4000];
        return new ScriptResult(Path.GetFileName(full)!, process.ExitCode, output);
    }

    private string Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name))
            throw new ArgumentException("Имя скрипта должно быть файлом в папке scripts, без пути");
        var ext = Path.GetExtension(name);
        if (!Extensions.Contains(ext))
            throw new ArgumentException("Можно запускать только ps1, cmd, bat и py");

        var full = Path.GetFullPath(Path.Combine(_root, name));
        var root = Path.GetFullPath(_root);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Скрипт вне папки scripts");
        if (!File.Exists(full))
            throw new ArgumentException($"Скрипта нет: {name}");
        return full;
    }

    private static string PowerShellCommand(string full, IReadOnlyList<string> args)
    {
        var call = "& " + PsQuote(full);
        if (args.Count > 0)
            call += " " + string.Join(' ', args.Select(PsQuote));
        return "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false; $OutputEncoding = [Console]::OutputEncoding; " + call;
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string BuildCmd(string full, IReadOnlyList<string> args)
    {
        var parts = new List<string> { Quote(full) };
        parts.AddRange(args.Select(Quote));
        return string.Join(' ', parts);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}

public sealed record ScriptResult(string Name, int ExitCode, string Output);
