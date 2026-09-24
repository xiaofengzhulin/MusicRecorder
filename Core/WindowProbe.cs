using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicRecorder.Core;

/// <summary>
/// Win32 兜底探测：当播放器没有提供 SMTC 媒体会话时，从播放器窗口标题里解析
/// "歌曲名 - 歌手"（QQ音乐 / 网易云音乐 / 酷狗音乐 等的窗口标题格式）。
/// </summary>
public static class WindowProbe
{
    private static readonly string[] PlayerProcesses =
    {
        "QQMusic", "QQMusicLite", "cloudmusic", "KuGou", "KuGouMusic", "KwMusic", "kwo",
        "MiguMusic", "Spotify", "foobar2000", "AIMP", "MusicBee", "PotPlayerMini64", "vlc"
    };

    private static readonly string[] AppTokens =
    {
        "QQ音乐", "QQMusic", "网易云音乐", "NetEase Cloud Music", "酷狗音乐", "酷狗",
        "酷我音乐", "咪咕音乐", "Spotify", "foobar2000", "AIMP", "MusicBee", "VLC", "PotPlayer"
    };

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public readonly record struct WindowTrack(string Title, string Artist, string AppName, string ProcessName);

    /// <summary>找出所有可见的播放器窗口。</summary>
    private static List<(IntPtr Hwnd, string Process, string Title)> EnumPlayerWindows()
    {
        var result = new List<(IntPtr, string, string)>();
        var self = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;
                var len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;

                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0 || pid == self) return true;

                string proc;
                try { proc = Process.GetProcessById((int)pid).ProcessName; }
                catch { return true; }

                if (!PlayerProcesses.Any(p => proc.Equals(p, StringComparison.OrdinalIgnoreCase))) return true;

                var sb = new StringBuilder(len + 2);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString().Trim();
                if (title.Length == 0) return true;

                result.Add((hWnd, proc, title));
            }
            catch { /* 忽略单个窗口的异常 */ }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>诊断用：列出所有播放器窗口（进程 / 窗口类名 / 标题），用于判断能否从标题拿到歌曲名或进度。</summary>
    public static string DumpPlayerWindows()
    {
        var sb = new StringBuilder();
        var windows = EnumPlayerWindows();
        sb.AppendLine($"播放器窗口数量：{windows.Count}");
        foreach (var (hwnd, proc, title) in windows)
        {
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            sb.AppendLine($"  {proc} | 类名={cls} | 标题='{title}'");
        }
        return sb.ToString();
    }

    /// <summary>是否检测到播放器进程（用于界面提示）。</summary>
    public static string? FindPlayerProcess()
    {
        var wins = EnumPlayerWindows();
        if (wins.Count == 0) return null;
        return FriendlyName(wins[0].Process);
    }

    /// <summary>从播放器窗口标题解析当前歌曲。</summary>
    public static WindowTrack? TryGetNowPlayingFromWindowTitle()
    {
        var wins = EnumPlayerWindows();
        if (wins.Count == 0) return null;

        var fg = GetForegroundWindow();
        var ordered = wins.OrderByDescending(w => w.Hwnd == fg).ToList();

        foreach (var (_, proc, rawTitle) in ordered)
        {
            var appName = FriendlyName(proc);
            var title = rawTitle;

            // 去掉结尾 / 中间的播放器名
            foreach (var token in AppTokens)
            {
                title = Regex.Replace(title, $@"\s*[-–—|·]\s*{Regex.Escape(token)}\s*$", "", RegexOptions.IgnoreCase);
                title = Regex.Replace(title, $@"^{Regex.Escape(token)}\s*[-–—|·]\s*", "", RegexOptions.IgnoreCase);
            }

            title = title.Trim(' ', '-', '–', '—', '|', '·');
            if (title.Length == 0) continue;

            // 常见静默状态标题
            if (title.Equals(appName, StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(title, @"^(QQ音乐|网易云音乐|酷狗音乐|酷我音乐|咪咕音乐)$")) continue;

            var parts = title.Split(new[] { " - ", " – ", " — " }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(p => p.Trim())
                             .Where(p => p.Length > 0)
                             .ToArray();

            var song = parts.Length > 0 ? parts[0] : title;
            var artist = parts.Length > 1 ? parts[1] : "";

            if (song.Length == 0) continue;
            return new WindowTrack(song, artist, appName, proc);
        }

        return null;
    }

    private static string FriendlyName(string process) => process.ToLowerInvariant() switch
    {
        "qqmusic" or "qqmusiclite" => "QQ音乐",
        "cloudmusic" => "网易云音乐",
        "kugou" or "kugoumusic" => "酷狗音乐",
        "kwmusic" or "kwo" => "酷我音乐",
        "migumusic" => "咪咕音乐",
        "spotify" => "Spotify",
        "foobar2000" => "foobar2000",
        "aimp" => "AIMP",
        "musicbee" => "MusicBee",
        "potplayermini64" => "PotPlayer",
        "vlc" => "VLC",
        _ => process
    };
}
