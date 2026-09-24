using System.IO;
using Windows.Media.Control;

namespace MusicRecorder.Core;

/// <summary>当前播放歌曲的快照信息。</summary>
public sealed class NowPlayingInfo
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string SourceAppId { get; set; } = "";
    public string SourceAppName { get; set; } = "";
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public bool HasDuration { get; set; }
    public string PlaybackStatus { get; set; } = "Unknown";
    public bool FromWindowTitle { get; set; }

    // 播放器自身声明的能力（SMTC PlaybackControls）
    public bool SupportsSeek { get; set; }
    public bool SupportsSkipNext { get; set; }
    public bool SupportsSkipPrevious { get; set; }
    public bool SupportsPlay { get; set; }
    public bool SupportsPause { get; set; }

    /// <summary>
    /// 播放器是否提供可信的进度信息。部分播放器（QQ音乐、网易云音乐）的时间轴恒为 0，
    /// 此时进度、总时长、以及"是否已经在开头"的判断都不可信。
    /// </summary>
    public bool TimelineReliable => HasDuration && Duration > TimeSpan.FromSeconds(5);

    public bool IsPlaying => PlaybackStatus == "Playing";
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);

    /// <summary>用于判断"是否换歌"的稳定标识。</summary>
    public string TrackKey => Normalize(Title) + "\u0001" + Normalize(Artist);
    public string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} - {Artist}";

    private static string Normalize(string s) => (s ?? "").Trim().ToLowerInvariant();

    public override string ToString() =>
        $"{DisplayName} | {SourceAppName} | {PlaybackStatus} | {Position:mm\\:ss}/{Duration:mm\\:ss}";
}

/// <summary>
/// 通过 Windows SMTC（GlobalSystemMediaTransportControlsSession，WinRT）读取/控制
/// QQ音乐、网易云音乐、酷狗音乐等播放器的当前会话：歌曲名、歌手、播放进度、播放/暂停、定位。
/// </summary>
public sealed class MediaSessionService
{
    private static readonly (string Key, string Name)[] KnownPlayers =
    {
        ("qqmusic", "QQ音乐"),
        ("tencent.qqmusic", "QQ音乐"),
        ("cloudmusic", "网易云音乐"),
        ("netease", "网易云音乐"),
        ("kugou", "酷狗音乐"),
        ("kugoumusic", "酷狗音乐"),
        ("kwo", "酷我音乐"),
        ("kwmusic", "酷我音乐"),
        ("migu", "咪咕音乐"),
        ("spotify", "Spotify"),
        ("aimp", "AIMP"),
        ("foobar", "foobar2000"),
        ("musicbee", "MusicBee"),
        ("potplayer", "PotPlayer"),
        ("vlc", "VLC"),
        ("zunemusic", "媒体播放器"),
        ("music.ui", "媒体播放器"),
        ("qqmusiclite", "QQ音乐"),
    };

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _sessionAppId = "";
    private DateTime _lastSessionRefresh = DateTime.MinValue;

    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>用户指定的目标播放器（媒体会话标识）。为空表示自动挑选。</summary>
    public string PreferredAppId { get; set; } = "";

    /// <summary>已发现的播放器会话（来源应用标识 -> 友好名称）。</summary>
    public List<(string AppId, string AppName, bool IsPlaying)> Sessions { get; } = new();

    public static string FriendlyAppName(string appUserModelId)
    {
        var id = (appUserModelId ?? "").ToLowerInvariant();
        foreach (var (key, name) in KnownPlayers)
        {
            if (id.Contains(key, StringComparison.OrdinalIgnoreCase)) return name;
        }
        try
        {
            var file = Path.GetFileName(id);
            if (!string.IsNullOrWhiteSpace(file)) return file;
        }
        catch { /* ignore */ }
        return string.IsNullOrWhiteSpace(appUserModelId) ? "未知来源" : appUserModelId;
    }

    public static bool IsKnownPlayer(string appUserModelId)
    {
        var id = (appUserModelId ?? "").ToLowerInvariant();
        return KnownPlayers.Any(p => id.Contains(p.Key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>初始化 SMTC 会话管理器。</summary>
    public async Task<bool> InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            IsAvailable = _manager is not null;
            LastError = IsAvailable ? null : "系统媒体会话管理器不可用";
            Log.Info($"SMTC 初始化：{(IsAvailable ? "成功" : "失败")}");
            return IsAvailable;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            LastError = $"初始化系统媒体会话失败：{ex.Message}";
            Log.Error("SMTC 初始化异常", ex);
            return false;
        }
    }

    /// <summary>刷新会话列表并挑选最合适的目标播放器会话。</summary>
    public async Task<NowPlayingInfo?> RefreshAsync(bool forceSessionRescan = false)
    {
        if (!IsAvailable || _manager is null) return null;

        try
        {
            if (forceSessionRescan || (DateTime.UtcNow - _lastSessionRefresh).TotalSeconds >= 4 || _session is null)
            {
                _lastSessionRefresh = DateTime.UtcNow;
                Sessions.Clear();
                foreach (var s in _manager.GetSessions())
                {
                    bool playing;
                    try { playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
                    catch { playing = false; }
                    Sessions.Add((s.SourceAppUserModelId ?? "", FriendlyAppName(s.SourceAppUserModelId ?? ""), playing));
                }
                _session = PickBestSession();
                _sessionAppId = _session?.SourceAppUserModelId ?? "";
            }

            if (_session is null)
            {
                var info = TryWindowFallback(playing: true);
                return info;
            }

            var props = await _session.TryGetMediaPropertiesAsync();
            var playback = _session.GetPlaybackInfo();
            var timeline = _session.GetTimelineProperties();

            var result = new NowPlayingInfo
            {
                SourceAppId = _session.SourceAppUserModelId ?? "",
                SourceAppName = FriendlyAppName(_session.SourceAppUserModelId ?? ""),
                PlaybackStatus = playback.PlaybackStatus.ToString(),
                Title = props?.Title?.Trim() ?? "",
                Artist = props?.Artist?.Trim() ?? "",
                Album = props?.AlbumTitle?.Trim() ?? "",
                SupportsSeek = SafeControl(playback, c => c.IsPlaybackPositionEnabled),
                SupportsSkipNext = SafeControl(playback, c => c.IsNextEnabled),
                SupportsSkipPrevious = SafeControl(playback, c => c.IsPreviousEnabled),
                SupportsPlay = SafeControl(playback, c => c.IsPlayEnabled),
                SupportsPause = SafeControl(playback, c => c.IsPauseEnabled),
            };

            if (string.IsNullOrWhiteSpace(result.Title))
            {
                var fb = TryWindowFallback(playing: false);
                if (fb is not null)
                {
                    result.Title = fb.Title;
                    result.Artist = fb.Artist;
                    result.FromWindowTitle = true;
                }
            }

            // 时间轴：部分播放器给出 EndTime = 歌曲总长
            var end = timeline.EndTime;
            var start = timeline.StartTime;
            var dur = end - start;
            if (dur > TimeSpan.FromSeconds(5) && dur < TimeSpan.FromHours(3))
            {
                result.Duration = dur;
                result.HasDuration = true;
            }

            var pos = timeline.Position - start;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

            // 估算"现在"的进度：LastUpdatedTime 之后按播放状态外推
            if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                var delta = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (delta > TimeSpan.Zero && delta < TimeSpan.FromSeconds(5)) pos += delta;
            }
            if (result.HasDuration && pos > result.Duration) pos = result.Duration;
            result.Position = pos;

            if (string.IsNullOrWhiteSpace(result.SourceAppName) || result.SourceAppName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var proc = WindowProbe.FindPlayerProcess();
                if (proc is not null) result.SourceAppName = proc;
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取媒体会话失败：{ex.Message}");
            _session = null;
            var fb = TryWindowFallback(playing: false);
            return fb;
        }
    }

    private GlobalSystemMediaTransportControlsSession? PickBestSession()
    {
        if (_manager is null) return null;
        var list = _manager.GetSessions().ToList();
        if (list.Count == 0) return null;

        bool Playing(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }

        // 0) 用户指定的播放器优先
        if (!string.IsNullOrWhiteSpace(PreferredAppId))
        {
            var preferred = list.FirstOrDefault(s =>
                string.Equals(s.SourceAppUserModelId, PreferredAppId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null) return preferred;
        }

        // 1) 已知播放器且正在播放
        var pick = list.FirstOrDefault(s => IsKnownPlayer(s.SourceAppUserModelId ?? "") && Playing(s));
        // 2) 已知播放器（暂停中也算，用户可能刚暂停）
        pick ??= list.FirstOrDefault(s => IsKnownPlayer(s.SourceAppUserModelId ?? ""));
        // 3) 任何正在播放的会话
        pick ??= list.FirstOrDefault(Playing);
        // 4) 系统当前会话
        try { pick ??= _manager.GetCurrentSession(); } catch { /* ignore */ }
        pick ??= list[0];
        return pick;
    }

    /// <summary>当前会话（供控制播放使用）。</summary>
    public GlobalSystemMediaTransportControlsSession? CurrentSession => _session;

    private NowPlayingInfo? TryWindowFallback(bool playing)
    {
        try
        {
            var w = WindowProbe.TryGetNowPlayingFromWindowTitle();
            if (w is null) return null;
            return new NowPlayingInfo
            {
                Title = w.Value.Title,
                Artist = w.Value.Artist,
                SourceAppName = w.Value.AppName,
                SourceAppId = w.Value.ProcessName,
                PlaybackStatus = playing ? "Playing" : "Unknown",
                FromWindowTitle = true,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"窗口标题兜底失败：{ex.Message}");
            return null;
        }
    }

    private static bool SafeControl(GlobalSystemMediaTransportControlsSessionPlaybackInfo info, Func<GlobalSystemMediaTransportControlsSessionPlaybackControls, bool> selector)
    {
        try { return selector(info.Controls); }
        catch { return false; }
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    /// <summary>确保歌曲正在播放。</summary>
    public async Task<bool> EnsurePlayingAsync()
    {
        var s = _session;
        if (s is null) return false;
        try
        {
            if (IsPlaying(s)) return true;

            await s.TryPlayAsync();
            await Task.Delay(350);
            if (IsPlaying(s)) return true;

            // 部分播放器（如 QQ音乐）的播放指令会失败，用系统媒体键兜底
            MediaKeySender.SendPlayPause();
            await Task.Delay(450);
            var playing = IsPlaying(s);
            Log.Info($"确保播放：TryPlay 后仍为暂停，媒体键兜底结果={playing}");
            return playing;
        }
        catch (Exception ex)
        {
            Log.Warn($"播放控制失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>诊断用：把系统里所有媒体会话的原始信息（媒体属性 / 播放控制能力 / 时间轴）完整导出来。</summary>
    public async Task<string> DumpRawSessionsAsync()
    {
        var sb = new System.Text.StringBuilder();
        if (_manager is null)
        {
            sb.AppendLine("SMTC 未初始化");
            return sb.ToString();
        }

        var sessions = _manager.GetSessions();
        sb.AppendLine($"会话数量：{sessions.Count}");
        foreach (var s in sessions)
        {
            sb.AppendLine(new string('-', 70));
            sb.AppendLine($"来源标识: {s.SourceAppUserModelId}  → {FriendlyAppName(s.SourceAppUserModelId ?? "")}");

            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                if (props is null) sb.AppendLine("  媒体属性: (null)");
                else
                {
                    sb.AppendLine($"  媒体属性: 标题='{props.Title}' 歌手='{props.Artist}' 专辑='{props.AlbumTitle}' 专辑歌手='{props.AlbumArtist}' 类型={props.PlaybackType} 曲目号={props.TrackNumber}");
                }
            }
            catch (Exception ex) { sb.AppendLine("  媒体属性读取失败: " + ex.Message); }

            try
            {
                var info = s.GetPlaybackInfo();
                var c = info.Controls;
                sb.AppendLine($"  播放状态: {info.PlaybackStatus}｜播放类型: {info.PlaybackType}｜循环: {info.AutoRepeatMode}｜随机: {info.IsShuffleActive}｜倍速: {info.PlaybackRate}");
                sb.AppendLine($"  可控能力: 播放={c.IsPlayEnabled} 暂停={c.IsPauseEnabled} 停止={c.IsStopEnabled} " +
                              $"上一曲={c.IsPreviousEnabled} 下一曲={c.IsNextEnabled} 快进={c.IsFastForwardEnabled} 快退={c.IsRewindEnabled} " +
                              $"定位进度={c.IsPlaybackPositionEnabled} 倍速={c.IsPlaybackRateEnabled} 随机={c.IsShuffleEnabled} 循环={c.IsRepeatEnabled}");
            }
            catch (Exception ex) { sb.AppendLine("  播放信息读取失败: " + ex.Message); }

            try
            {
                AppendTimeline(sb, s);
            }
            catch (Exception ex) { sb.AppendLine("  时间轴读取失败: " + ex.Message); }
        }
        return sb.ToString();
    }

    private static void AppendTimeline(System.Text.StringBuilder sb, GlobalSystemMediaTransportControlsSession s)
    {
        var t = s.GetTimelineProperties();
        sb.AppendLine($"  时间轴: 位置={t.Position:mm\\:ss\\.fff} 起点={t.StartTime:mm\\:ss\\.fff} 终点={t.EndTime:mm\\:ss\\.fff} " +
                      $"最小定位={t.MinSeekTime:mm\\:ss\\.fff} 最大定位={t.MaxSeekTime:mm\\:ss\\.fff} 更新时间={t.LastUpdatedTime:HH:mm:ss\\.fff}");
    }

    /// <summary>诊断用：对指定会话做一次时间轴采样（判断进度是否会推进）。</summary>
    public async Task<string> SampleTimelineAsync(string? appId, int seconds)
    {
        var sb = new System.Text.StringBuilder();
        if (_manager is null) return "SMTC 未初始化";

        var session = _manager.GetSessions().FirstOrDefault(s =>
            string.IsNullOrEmpty(appId) || string.Equals(s.SourceAppUserModelId, appId, StringComparison.OrdinalIgnoreCase));
        if (session is null) return "未找到指定会话";

        sb.AppendLine($"目标会话: {session.SourceAppUserModelId}");
        for (var i = 0; i < seconds; i++)
        {
            var info = session.GetPlaybackInfo();
            AppendTimeline(sb, session);
            sb.AppendLine($"    [第{i + 1}秒] 状态={info.PlaybackStatus}");
            await Task.Delay(1000);
        }
        return sb.ToString();
    }

    /// <summary>诊断用：测试定位到开头 / 上一曲 / 下一曲是否真的生效。</summary>
    public async Task<string> TestControlsAsync(string? appId)
    {
        var sb = new System.Text.StringBuilder();
        if (_manager is null) return "SMTC 未初始化";

        var session = _manager.GetSessions().FirstOrDefault(s =>
            string.IsNullOrEmpty(appId) || string.Equals(s.SourceAppUserModelId, appId, StringComparison.OrdinalIgnoreCase));
        if (session is null) return "未找到指定会话";

        async Task<string> SnapshotAsync(string label)
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var t = session.GetTimelineProperties();
            var st = session.GetPlaybackInfo().PlaybackStatus;
            return $"{label}: 标题='{props?.Title}' 位置={t.Position:mm\\:ss\\.fff} 终点={t.EndTime:mm\\:ss\\.fff} 状态={st}";
        }

        sb.AppendLine(await SnapshotAsync("初始"));

        var seekOk = await session.TryChangePlaybackPositionAsync(0);
        await Task.Delay(900);
        sb.AppendLine($"TryChangePlaybackPositionAsync(0) 返回 {seekOk}");
        sb.AppendLine(await SnapshotAsync("定位后"));

        var prevOk = await session.TrySkipPreviousAsync();
        await Task.Delay(1200);
        sb.AppendLine($"TrySkipPreviousAsync 返回 {prevOk}");
        sb.AppendLine(await SnapshotAsync("上一曲后"));

        var nextOk = await session.TrySkipNextAsync();
        await Task.Delay(1200);
        sb.AppendLine($"TrySkipNextAsync 返回 {nextOk}");
        sb.AppendLine(await SnapshotAsync("下一曲后"));

        return sb.ToString();
    }

    /// <summary>暂停当前会话（录制结束后自动暂停、自检/维护用）。已暂停时返回 true。</summary>
    public async Task<bool> PauseCurrentAsync()
    {
        try
        {
            var session = _session;
            if (session is null)
            {
                await RefreshAsync(forceSessionRescan: true);
                session = _session;
                if (session is null) return false;
            }

            if (session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                return true; // 已经不在播放

            var ok = await session.TryPauseAsync();
            if (!ok)
            {
                // 再确认一次：有些播放器虽然返回 false，其实已经暂停了
                await Task.Delay(200);
                if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    MediaKeySender.SendPlayPause(); // 系统媒体键兜底（暂停/播放切换）
                    await Task.Delay(350);
                }
            }
            else
            {
                await Task.Delay(200);
            }

            var paused = session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Log.Info($"暂停播放：TryPause={ok}，结果={(paused ? "已暂停" : "仍在播放")}");
            return paused;
        }
        catch (Exception ex)
        {
            Log.Warn($"暂停失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>尝试把进度定位到歌曲开头。</summary>
    public async Task<bool> TrySeekToStartAsync()
    {
        var s = _session;
        if (s is null) return false;
        try
        {
            var ok = await s.TryChangePlaybackPositionAsync(0);
            Log.Info($"SMTC 定位到开头：{ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn($"定位失败：{ex.Message}");
            return false;
        }
    }

    public async Task<bool> TrySkipPreviousAsync()
    {
        try { return _session is not null && await _session.TrySkipPreviousAsync(); }
        catch (Exception ex) { Log.Warn($"上一曲失败：{ex.Message}"); return false; }
    }

    public async Task<bool> TrySkipNextAsync()
    {
        try { return _session is not null && await _session.TrySkipNextAsync(); }
        catch (Exception ex) { Log.Warn($"下一曲失败：{ex.Message}"); return false; }
    }
}
