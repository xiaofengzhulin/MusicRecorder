using System.IO;
using System.Text.Json;

namespace MusicRecorder.Core;

/// <summary>用户配置持久化（导出目录、码率、录音设备、开关项）。</summary>
public sealed class AppSettings
{
    public string OutputFolder { get; set; } = DefaultOutputFolder();
    public int Bitrate { get; set; } = 320;
    /// <summary>-1 表示使用系统默认播放设备（环回录制）。</summary>
    public int CaptureDeviceNumber { get; set; } = -1;
    public bool RestartFromStart { get; set; } = true;
    public bool OpenFolderWhenDone { get; set; } = true;

    /// <summary>录制结束后自动暂停播放器。</summary>
    public bool PausePlaybackWhenDone { get; set; } = true;

    /// <summary>
    /// 「自动录制」：勾选后，只要在播放器里开始播放（或勾选时正在播放），
    /// 就自动倒回开头并开始录制，无需手动点「开始录制」。
    /// （字段名沿用旧名以兼容已有 settings.json。）
    /// </summary>
    public bool AutoStartRecordingWhenSongDetected { get; set; } = false;

    /// <summary>
    /// 「自动录制整张歌单」：点播放开始录制，当前歌曲放完后自动接着录下一首，每首单独导出。
    /// 勾选后连录期间不会在每首结束时暂停播放器（否则无法连录）。
    /// </summary>
    public bool AutoRecordPlaylistMode { get; set; } = false;

    /// <summary>指定要录制的播放器（媒体会话标识），空 = 自动选择。</summary>
    public string PreferredPlayerAppId { get; set; } = "";

    public static string DefaultOutputFolder()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrWhiteSpace(music)) music = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(music, "MusicRecorder");
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicRecorder", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.OutputFolder)) loaded.OutputFolder = DefaultOutputFolder();
                    if (loaded.Bitrate <= 0) loaded.Bitrate = 320;
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取配置失败，使用默认值：{ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"保存配置失败：{ex.Message}");
        }
    }
}
