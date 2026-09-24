using System.IO;

namespace MusicRecorder.Core;

public enum RecorderState { Idle, Preparing, Recording, Finishing }

public enum StopReason { Manual, TrackChanged, PlaybackStopped, ReachedEnd, Timeout, CaptureError }

public sealed class RecorderOptions
{
    public string OutputFolder { get; set; } = AppSettings.DefaultOutputFolder();
    public int Bitrate { get; set; } = 320;
    public int DeviceIndex { get; set; } = -1;
    public bool RestartFromStart { get; set; } = true;

    /// <summary>指定要录制的播放器（媒体会话标识）；为空则自动选择。</summary>
    public string PreferredAppId { get; set; } = "";

    /// <summary>录制结束后自动暂停播放（避免播放器继续播下一首）。</summary>
    public bool PausePlaybackWhenDone { get; set; } = true;
}

public sealed record StartResult(bool Success, string? Error, string? FilePath, NowPlayingInfo? Info, string? Warning)
{
    public static StartResult Fail(string error) => new(false, error, null, null, null);
}

public sealed class RecordResult
{
    public string FilePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string SourceApp { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public StopReason Reason { get; set; }

    /// <summary>录制结束后是否成功暂停了播放器。</summary>
    public bool PlaybackPaused { get; set; }

    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 录制流程编排：
/// 检测播放器会话 -> 确保从头播放 -> 开始内录 -> 轮询歌曲是否结束 -> 自动停止并导出 MP3。
/// </summary>
public sealed class RecorderEngine : IDisposable
{
    private static readonly TimeSpan MaxRecordDuration = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan EndTailGrace = TimeSpan.FromMilliseconds(700);

    private readonly MediaSessionService _media = new();
    private readonly AudioRecorder _recorder = new();
    private readonly List<string> _warnings = new();

    private DateTime _startedAt;
    private string _baselineKey = "";
    private bool _hasTrackBaseline;
    private int _notPlayingTicks;
    private int _noInfoTicks;
    private int _tickBusy;
    private TimeSpan _maxPosition;
    private bool _pauseWhenDone = true;
    private string _currentTitle = "";
    private string _currentArtist = "";
    private string _currentApp = "";

    public RecorderEngine()
    {
        _recorder.LevelChanged += l => LevelChanged?.Invoke(l);
    }

    public RecorderState State { get; private set; } = RecorderState.Idle;
    public MediaSessionService Media => _media;
    public AudioRecorder Recorder => _recorder;
    public NowPlayingInfo? LastInfo { get; private set; }
    public string CurrentFilePath { get; private set; } = "";
    public bool IsRecording => State == RecorderState.Recording;
    public TimeSpan Elapsed => State == RecorderState.Recording ? DateTime.Now - _startedAt : TimeSpan.Zero;

    public event Action<string>? StatusChanged;
    public event Action? StateChanged;
    public event Action<NowPlayingInfo?>? InfoUpdated;
    public event Action<float>? LevelChanged;

    public async Task InitializeAsync()
    {
        await _media.InitializeAsync();
    }

    private void Status(string message)
    {
        Log.Info($"[状态] {message}");
        StatusChanged?.Invoke(message);
    }

    private void SetState(RecorderState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------ 开始录制

    public async Task<StartResult> StartAsync(RecorderOptions options)
    {
        if (State is RecorderState.Recording or RecorderState.Preparing)
            return StartResult.Fail("当前正在录制中。");

        SetState(RecorderState.Preparing);
        _warnings.Clear();
        _notPlayingTicks = 0;
        _noInfoTicks = 0;

        try
        {
            _media.PreferredAppId = options.PreferredAppId ?? "";
            _pauseWhenDone = options.PausePlaybackWhenDone;

            if (!_media.IsAvailable)
            {
                Status("正在连接 Windows 媒体会话…");
                await _media.InitializeAsync();
            }

            if (!_media.IsAvailable)
            {
                SetState(RecorderState.Idle);
                return StartResult.Fail(_media.LastError ?? "无法访问 Windows 系统媒体会话，请确认系统版本为 Windows 10 2004 或更高。");
            }

            Status("正在检测正在播放的歌曲…");
            var info = await _media.RefreshAsync(forceSessionRescan: true);
            if (info is null)
            {
                SetState(RecorderState.Idle);
                return StartResult.Fail("没有检测到任何正在运行的播放器会话。\r\n请先打开 QQ音乐 / 网易云音乐 / 酷狗音乐 任意一款并播放歌曲，再点击“开始录制”。");
            }

            if (!info.HasTrack)
            {
                if (info.PlaybackStatus != "Playing" && info.PlaybackStatus != "Paused")
                {
                    SetState(RecorderState.Idle);
                    return StartResult.Fail("检测到播放器，但当前没有歌曲在播放。\r\n请在播放器中播放歌曲后再点击“开始录制”。");
                }

                // 播放器没有提供歌曲名（少数播放器/网页播放器会这样）：仍然录制，用时间戳命名
                const string noTitle = "未能从播放器读取歌曲名（该播放器未开放媒体信息），将以录制时间命名文件。";
                _warnings.Add(noTitle);
                Log.Warn(noTitle);
            }

            Status(info.HasTrack
                ? $"检测到：{info.DisplayName}（{info.SourceAppName}）"
                : $"检测到播放器：{info.SourceAppName}（未提供歌曲名）");
            await _media.EnsurePlayingAsync();

            string? restartWarning = null;
            if (options.RestartFromStart)
            {
                Status("正在把歌曲倒回开头…");
                var (ok, message) = await RestartFromStartAsync(info);
                Status(message);
                if (!ok)
                {
                    restartWarning = message;
                    _warnings.Add(message);
                }

                var after = await _media.RefreshAsync();
                if (after is not null && after.HasTrack) info = after;
            }

            var title = string.IsNullOrWhiteSpace(info.Title)
                ? $"录音_{DateTime.Now:yyyyMMdd_HHmmss}"
                : info.Title.Trim();
            string path;
            try
            {
                path = BuildOutputPath(options.OutputFolder, title);
            }
            catch (Exception ex)
            {
                SetState(RecorderState.Idle);
                return StartResult.Fail($"导出目录不可用：{ex.Message}");
            }

            var tags = new TrackTags { Title = title, Artist = info.Artist, Album = info.Album };

            Status("正在启动系统内录…");
            try
            {
                _recorder.Start(options.DeviceIndex, path, options.Bitrate, tags);
            }
            catch (Exception ex)
            {
                Log.Error("启动录音失败", ex);
                SetState(RecorderState.Idle);
                return StartResult.Fail($"启动录音失败：{ex.Message}");
            }

            _startedAt = DateTime.Now;
            _baselineKey = info.HasTrack ? info.TrackKey : "";
            _hasTrackBaseline = info.HasTrack;
            _maxPosition = TimeSpan.Zero;
            _currentTitle = title;
            _currentArtist = info.Artist;
            _currentApp = info.SourceAppName;
            CurrentFilePath = path;
            SetState(RecorderState.Recording);

            Status(info.HasTrack ? $"正在录制：{info.DisplayName}" : "正在录制（该播放器未提供歌曲名）");
            return new StartResult(true, null, path, info, restartWarning);        }
        catch (Exception ex)
        {
            Log.Error("开始录制异常", ex);
            try { _recorder.Stop(); } catch { }
            SetState(RecorderState.Idle);
            return StartResult.Fail($"开始录制失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把当前歌曲倒回开头并开始播放。
    /// 关键点：
    ///  · 有些播放器（QQ音乐、网易云音乐）的 SMTC 时间轴恒为 0，此时 Position 完全不可信，
    ///    不能拿它判断"是否已经在开头"，也不能拿它验证是否倒回成功——否则会出现"以为在开头、其实没倒回"。
    ///  · 这类播放器改用「下一曲 → 上一曲」：切走再切回，歌曲必然从 0:00 开始，
    ///    并用歌名变化来验证确实切回了同一首歌。
    /// </summary>
    private async Task<(bool Ok, string Message)> RestartFromStartAsync(NowPlayingInfo info)
    {
        var timelineReliable = info.TimelineReliable;

        // 0) 进度可信且已经在开头：无需处理
        if (timelineReliable && info.Position <= TimeSpan.FromSeconds(1.5) && info.IsPlaying)
            return (true, "歌曲已经在开头，直接开始录制。");

        // 1) 播放器声明支持定位时才用定位（QQ音乐声明 IsPlaybackPositionEnabled=False，
        //    但它对 TryChangePlaybackPositionAsync 返回 True，属于"假装成功"，必须靠能力声明过滤）
        if (info.SupportsSeek)
        {
            if (await _media.TrySeekToStartAsync())
            {
                await Task.Delay(600);
                var afterSeek = await _media.RefreshAsync();
                var seekVerified = afterSeek is not null && (!timelineReliable || afterSeek.Position <= TimeSpan.FromSeconds(2.5));
                if (seekVerified)
                {
                    await _media.EnsurePlayingAsync();
                    return (true, "已通过系统媒体控制定位到开头。");
                }
                Log.Warn("定位指令返回成功但进度未变化，改用切歌方案");
            }
        }
        else
        {
            Log.Info("该播放器未声明支持定位（IsPlaybackPositionEnabled=False），使用切歌方案");
        }

        // 2) 下一曲 + 上一曲：切走再切回，当前歌曲会从 0:00 重新开始
        if (info.SupportsSkipNext && info.SupportsSkipPrevious)
        {
            var key0 = info.TrackKey;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                await _media.TrySkipNextAsync();
                await Task.Delay(1100);
                await _media.TrySkipPreviousAsync();
                await Task.Delay(1400);

                var after = await _media.RefreshAsync();
                if (after is not null && after.HasTrack && after.TrackKey == key0)
                {
                    await _media.EnsurePlayingAsync();
                    return (true, $"已通过「下一曲 + 上一曲」让歌曲从 0:00 重新开始（第 {attempt} 次尝试）。");
                }
                Log.Warn($"第 {attempt} 次切歌未回到原歌曲：{after?.TrackKey}");
            }

            await _media.EnsurePlayingAsync();
            return (false, "尝试「下一曲 + 上一曲」后没有回到原歌曲，已按当前播放内容录制。若不对请手动切回该歌曲后重试。");
        }

        // 3) 只有"上一曲"可用：多数播放器在播放数秒后按上一曲会重播当前歌曲
        var beforeKey = info.TrackKey;
        await _media.TrySkipPreviousAsync();
        await Task.Delay(1100);
        var check = await _media.RefreshAsync();
        if (check is not null && check.HasTrack && check.TrackKey != beforeKey)
        {
            await _media.TrySkipNextAsync(); // 真的跳到了上一首，切回来（从 0 开始）
            await Task.Delay(1100);
        }
        check = await _media.RefreshAsync();
        if (check is not null && check.HasTrack && check.TrackKey == beforeKey)
        {
            await _media.EnsurePlayingAsync();
            return (true, "已通过「上一曲」让歌曲从头开始。");
        }

        // 4) 系统媒体键兜底
        MediaKeySender.SendPreviousTrack();
        await Task.Delay(1200);
        check = await _media.RefreshAsync();
        if (check is not null && check.HasTrack && check.TrackKey == beforeKey)
        {
            await _media.EnsurePlayingAsync();
            return (true, "已通过系统媒体键让歌曲从头开始。");
        }

        await _media.EnsurePlayingAsync();
        return (false, timelineReliable
            ? "无法自动把进度倒回开头（该播放器未开放定位或切歌控制），已从当前位置开始录制。建议手动把进度条拖到 0:00 后重试。"
            : "该播放器（如 QQ音乐）既不能定位进度、也不能切歌返回，无法自动回到开头，已从当前位置开始录制。建议先在播放器里手动切到该歌曲开头再点录制。");
    }

    // ------------------------------------------------------------------ 轮询/结尾检测

    public async Task<RecordResult?> TickAsync()
    {
        if (Interlocked.Exchange(ref _tickBusy, 1) == 1) return null;
        try
        {
            var info = await _media.RefreshAsync();
            LastInfo = info;
            InfoUpdated?.Invoke(info);

            if (State != RecorderState.Recording) return null;

            var elapsed = DateTime.Now - _startedAt;

            if (_recorder.CaptureAborted)
            {
                _warnings.Add("音频采集流意外中断。");
                return await StopAsync(StopReason.CaptureError);
            }

            if (info is null || !info.HasTrack)
            {
                _noInfoTicks++;
                // 连续 8 秒读不到会话信息：认为播放器已关闭
                if (_noInfoTicks * 0.25 >= 8) return await StopAsync(StopReason.PlaybackStopped);
                return null;
            }
            _noInfoTicks = 0;

            // 1) 已经切到下一首歌 => 本首结束（仅在开始时拿到了歌曲名的情况下判断）
            if (_hasTrackBaseline && elapsed.TotalSeconds > 3 && info.TrackKey != _baselineKey)
            {
                Log.Info($"检测到换歌：{_baselineKey} -> {info.TrackKey}");
                return await StopAsync(StopReason.TrackChanged);
            }

            // 2) 播放状态离开 Playing（播放器自动暂停/停止，或用户手动暂停）
            if (!info.IsPlaying)
            {
                _notPlayingTicks++;
                if (_notPlayingTicks >= 3) return await StopAsync(StopReason.PlaybackStopped);
            }
            else
            {
                _notPlayingTicks = 0;
            }

            // 3) 进度已到曲末
            if (info.HasDuration && elapsed.TotalSeconds > 3 &&
                info.Position >= info.Duration - TimeSpan.FromMilliseconds(400))
            {
                return await StopAsync(StopReason.ReachedEnd);
            }

            // 4) 播放器不提供总时长时的兜底：进度明显回退（重新从头播放 = 上一首已放完）
            if (info.Position > _maxPosition) _maxPosition = info.Position;
            if (info.IsPlaying && !info.HasDuration &&
                elapsed.TotalSeconds > 35 &&
                _maxPosition > TimeSpan.FromSeconds(30) &&
                info.Position < TimeSpan.FromSeconds(3))
            {
                Log.Info($"检测到进度回退（{_maxPosition:mm\\:ss} -> {info.Position:mm\\:ss}），判定歌曲已重新开始");
                return await StopAsync(StopReason.ReachedEnd);
            }

            // 4) 超长保护
            if (elapsed > MaxRecordDuration)
            {
                _warnings.Add($"录制超过 {MaxRecordDuration.TotalMinutes:0} 分钟，已自动停止。");
                return await StopAsync(StopReason.Timeout);
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Error("轮询录制状态异常", ex);
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _tickBusy, 0);
        }
    }

    // ------------------------------------------------------------------ 停止

    public async Task<RecordResult?> StopAsync(StopReason reason)
    {
        if (State is not RecorderState.Recording) return null;
        SetState(RecorderState.Finishing);

        Status(reason switch
        {
            StopReason.TrackChanged => "歌曲已结束，正在结束录制…",
            StopReason.PlaybackStopped => "播放已停止，正在结束录制…",
            StopReason.ReachedEnd => "已播放到结尾，正在结束录制…",
            StopReason.Timeout => "录制超时，正在结束录制…",
            StopReason.CaptureError => "采集中断，正在保存…",
            _ => "正在停止录制…",
        });

        // 换歌时播放器已经在放下一首了：先立刻暂停，避免下一首继续播放（也会被录进去）
        var playbackPaused = false;
        if (_pauseWhenDone && reason == StopReason.TrackChanged)
        {
            playbackPaused = await PausePlaybackAsync();
        }

        // 留一点尾部余量，把淡出/尾音录进去
        switch (reason)
        {
            case StopReason.TrackChanged:
                await Task.Delay(150);
                break;
            case StopReason.ReachedEnd:
            case StopReason.PlaybackStopped:
                await Task.Delay((int)EndTailGrace.TotalMilliseconds);
                break;
        }

        var recorded = _recorder.Recorded;
        string? path = null;
        try
        {
            path = await Task.Run(() => _recorder.Stop());
        }
        catch (Exception ex)
        {
            Log.Error("停止录制失败", ex);
            _warnings.Add($"停止录制时出错：{ex.Message}");
        }

        var result = new RecordResult
        {
            FilePath = path ?? "",
            Title = _currentTitle,
            Artist = _currentArtist,
            SourceApp = _currentApp,
            Duration = recorded,
            Reason = reason,
        };
        result.Warnings.AddRange(_warnings);

        // 录制结束后暂停播放（除了换歌那一刻已经暂停过的情况）
        if (_pauseWhenDone && !playbackPaused)
        {
            playbackPaused = await PausePlaybackAsync();
        }
        result.PlaybackPaused = playbackPaused;
        if (_pauseWhenDone && !playbackPaused)
        {
            result.Warnings.Add("未能自动暂停播放器，请手动暂停。");
        }

        if (string.IsNullOrEmpty(path))
        {
            result.Warnings.Add("没有生成录音文件，请检查磁盘空间与导出目录权限。");
        }
        else if (recorded < TimeSpan.FromSeconds(3))
        {
            result.Warnings.Add("录制时长过短，可能是歌曲刚开始就结束了，请确认播放器状态。");
        }

        CurrentFilePath = path ?? "";
        SetState(RecorderState.Idle);

        var saved = string.IsNullOrEmpty(path) ? "录制结束，但没有生成文件。" : $"已保存：{Path.GetFileName(path)}";
        if (playbackPaused) saved += "，播放已暂停。";
        Status(saved);
        return result;
    }

    /// <summary>暂停目标播放器；成功返回 true。</summary>
    private async Task<bool> PausePlaybackAsync()
    {
        try
        {
            var ok = await _media.PauseCurrentAsync();
            Log.Info($"录制结束暂停播放：{ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn($"暂停播放失败：{ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ 工具

    public static string BuildOutputPath(string folder, string title)
    {
        if (string.IsNullOrWhiteSpace(folder)) folder = AppSettings.DefaultOutputFolder();
        Directory.CreateDirectory(folder);

        var safe = SanitizeFileName(title);
        if (safe.Length == 0) safe = $"录音_{DateTime.Now:yyyyMMdd_HHmmss}";

        var path = Path.Combine(folder, safe + ".mp3");
        var index = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{safe} ({index}).mp3");
            index++;
            if (index > 999) break;
        }
        return path;
    }

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim(' ', '.');
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");
        if (result.Length > 120) result = result[..120];
        return result;
    }

    public void Dispose() => _recorder.Dispose();
}
