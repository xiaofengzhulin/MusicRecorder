using System.IO;
using System.Text;

namespace MusicRecorder.Core;

/// <summary>极简日志：写入 %LOCALAPPDATA%\MusicRecorder\logs\app-yyyyMMdd.log，便于排查问题。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicRecorder", "logs");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                _file ??= Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.AppendAllText(_file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}
