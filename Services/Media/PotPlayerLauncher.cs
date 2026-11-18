using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;
using QuasarBridge.Options;

namespace QuasarBridge.Services.Media;

public sealed record PlayerPosition(string FileName, long PositionSeconds);

public sealed record SeekMove(long PositionSeconds, int DeltaSeconds, bool AtStart, bool AtEnd);

public sealed class PotPlayerLauncher
{
    private const int WmUser = 0x0400;
    private const int GetTotalTime = 0x5002;
    private const int GetCurrentTime = 0x5004;
    private const int SetCurrentTime = 0x5005;
    private const int SetPlayStatus = 0x5007;
    private const int StatusPaused = 1;
    private const int SendVirtualKey = 0x5010;
    private const int VkReturn = 0x0D;
    private const int WmClose = 0x0010;

    private readonly IOptions<PotPlayerOptions> _options;
    private readonly ILogger<PotPlayerLauncher> _logger;

    public PotPlayerLauncher(IOptions<PotPlayerOptions> options, ILogger<PotPlayerLauncher> logger)
    {
        _options = options;
        _logger = logger;
    }

    public void Play(PlayableMedia media)
    {
        if (string.IsNullOrWhiteSpace(media.Path))
            throw new InvalidOperationException("У записи нет пути к файлу.");
        if (!File.Exists(media.Path))
            throw new FileNotFoundException("Файл не найден на этом компьютере.", media.Path);

        var seconds = Math.Max(0, media.PositionSeconds);
        var player = FindPlayer();
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = player,
            Arguments = $"\"{media.Path}\" /seek={seconds} /current",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("PotPlayer не запустился.");

        var fileName = Path.GetFileName(media.Path);
        _ = Task.Run(() =>
        {
            try
            {
                var window = PlayerWindow.WaitForTitle(fileName, TimeSpan.FromSeconds(6));
                if (window != IntPtr.Zero && !PlayerWindow.CoversScreen(window))
                    PlayerWindow.Send(window, SendVirtualKey, VkReturn);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось развернуть PotPlayer");
            }
        });

        _logger.LogDebug("PotPlayer {title} {detail} seek={sec}s", media.Title, media.Detail, seconds);
    }

    public PlayerPosition ReadCurrent()
    {
        var window = PlayerWindow.Find();
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("PotPlayer не запущен.");

        var milliseconds = PlayerWindow.Send(window, GetCurrentTime, 0);
        if (milliseconds < 0)
            throw new InvalidOperationException("PotPlayer не отдал позицию воспроизведения.");

        var title = PlayerWindow.Title(window);
        const string suffix = " - PotPlayer";
        var fileName = title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? title[..^suffix.Length]
            : title;
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("У PotPlayer нет текущего файла.");

        return new PlayerPosition(fileName.Trim(), milliseconds / 1000);
    }

    public void Pause()
    {
        var window = PlayerWindow.Find();
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("PotPlayer не запущен.");
        PlayerWindow.Send(window, SetPlayStatus, StatusPaused);
        _logger.LogDebug("PotPlayer поставлен на паузу");
    }

    public SeekMove SeekBy(TimeSpan delta)
    {
        var window = PlayerWindow.Find();
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("PotPlayer не запущен.");

        var currentMs = PlayerWindow.Send(window, GetCurrentTime, 0);
        if (currentMs < 0)
            throw new InvalidOperationException("PotPlayer не отдал позицию воспроизведения.");

        var totalMs = PlayerWindow.Send(window, GetTotalTime, 0);
        var target = currentMs + (long)delta.TotalMilliseconds;
        if (target < 0)
            target = 0;

        var knownLength = totalMs > currentMs;
        var atEnd = false;
        if (knownLength && target >= totalMs)
        {
            target = totalMs;
            atEnd = delta > TimeSpan.Zero;
        }

        PlayerWindow.Send(window, SetCurrentTime, (int)Math.Min(target, int.MaxValue));
        _logger.LogDebug("PotPlayer seek {delta}s -> {target}ms", (long)delta.TotalSeconds, target);
        return new SeekMove(target / 1000, (int)delta.TotalSeconds, target == 0 && delta < TimeSpan.Zero, atEnd);
    }

    public void Close()
    {
        var closed = PlayerWindow.CloseAll();
        if (closed == 0)
            throw new InvalidOperationException("PotPlayer не запущен.");
        _logger.LogDebug("PotPlayer закрыт, окон {count}", closed);
    }

    private string FindPlayer()
    {
        var configured = _options.Value.Path;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PotPlayer", "PotPlayerMini64.exe");
        if (File.Exists(standard))
            return standard;
        throw new InvalidOperationException("PotPlayer не найден. Укажите PotPlayer:Path.");
    }

    private static class PlayerWindow
    {
        public static IntPtr WaitForTitle(string namePart, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                var hwnd = Find();
                if (hwnd != IntPtr.Zero && Title(hwnd).Contains(namePart, StringComparison.OrdinalIgnoreCase))
                    return hwnd;
                Thread.Sleep(200);
            }

            return IntPtr.Zero;
        }

        public static bool CoversScreen(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
                return false;
            return rect.Right - rect.Left >= GetSystemMetrics(0)
                && rect.Bottom - rect.Top >= GetSystemMetrics(1);
        }

        public static long Send(IntPtr hwnd, int command, int parameter) =>
            SendMessage(hwnd, WmUser, (IntPtr)command, (IntPtr)parameter).ToInt64();

        public static IntPtr Find()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                if (!IsPlayer(hwnd))
                    return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static int CloseAll()
        {
            var closed = 0;
            EnumWindows((hwnd, _) =>
            {
                if (IsPlayer(hwnd))
                {
                    _ = PostClose(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                    closed++;
                }

                return true;
            }, IntPtr.Zero);
            return closed;
        }

        private static bool IsPlayer(IntPtr hwnd)
        {
            var className = new StringBuilder(64);
            _ = GetClassName(hwnd, className, className.Capacity);
            return className.ToString() is "PotPlayer64" or "PotPlayer";
        }

        public static string Title(IntPtr hwnd)
        {
            var buffer = new StringBuilder(512);
            _ = GetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern IntPtr PostClose(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
