using System.Diagnostics;

namespace QuasarBridge.Services;

/// <summary>Opens Alice's hands-free voice search so the bridge can feed a question.</summary>
public static class StationAsk
{
    public static bool Start(out string detail)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(@"C:\Users\Admin\Documents\ssh\id_rsa");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("IdentitiesOnly=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ConnectTimeout=5");
        psi.ArgumentList.Add("root@192.168.0.195");
        psi.ArgumentList.Add("/system/bin/am start -a android.speech.action.VOICE_SEARCH_HANDS_FREE -n com.yandex.tv.alice/.app.AliceGatewayActivity");

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }

        if (!process.WaitForExit(8000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            detail = "ssh не вернулся";
            return false;
        }

        var text = (process.StandardOutput.ReadToEnd() + " " + process.StandardError.ReadToEnd()).Trim();
        detail = string.IsNullOrWhiteSpace(text) ? "пустой ответ" : text;
        return process.ExitCode == 0;
    }
}
