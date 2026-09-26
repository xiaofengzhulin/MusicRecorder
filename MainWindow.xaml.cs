using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicRecorder.Core;

namespace MusicRecorder;

public partial class MainWindow : Window
{
    private sealed record BitrateOption(int Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record PlayerOption(string AppId, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x7D, 0xF6));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x3B, 0x3B));

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly RecorderEngine _engine = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private bool _forceClose;
    private string _lastStatus = "";
    private string _playerSignature = "";

    /// <summary>「自动录制」是否已武装：检测到"开始播放"后置 false，播放停止/暂停后重新武装。</summary>
    private bool _autoRecordArmed = true;
    private bool _autoStarting;

    /// <summary>「自动录制整张歌单」最近一次自动开录的歌曲标识，用于判定"换歌了，该录下一首"。</summary>
    private string _lastAutoTrackKey = "";

    /// <summary>正在弹「是否导出」对话框：模态期间定时器仍在跑，此时不要自动开录。</summary>
    private bool _promptOpen;

    /// <summary>刷新「目标播放器」下拉框时会触发 SelectionChanged，期间不要覆盖用户的选择。</summary>
    private bool _suppressPlayerComboEvent;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();

        _engine.StatusChanged += OnEngineStatus;
        _engine.StateChanged += OnEngineStateChanged;
        _engine.LevelChanged += OnEngineLevel;

        BitrateCombo.ItemsSource = new[]
        {
            new BitrateOption(128, "128 kbps"),
            new BitrateOption(192, "192 kbps"),
            new BitrateOption(256, "256 kbps"),
            new BitrateOption(320, "320 kbps（推荐）"),
        };
        BitrateCombo.DisplayMemberPath = nameof(BitrateOption.Label);
        BitrateCombo.SelectedValuePath = nameof(BitrateOption.Value);
        BitrateCombo.SelectedValue = _settings.Bitrate;
        if (BitrateCombo.SelectedIndex < 0) BitrateCombo.SelectedValue = 320;

        OutputFolderBox.Text = _settings.OutputFolder;
        RestartCheck.IsChecked = _settings.RestartFromStart;
        PauseCheck.IsChecked = _settings.PausePlaybackWhenDone;
        OpenFolderCheck.IsChecked = _settings.OpenFolderWhenDone;
        AutoRecordCheck.IsChecked = _settings.AutoStartRecordingWhenSongDetected;
        PlaylistCheck.IsChecked = _settings.AutoRecordPlaylistMode;
        // 自动录制开启时立即武装：若启动时播放器正在播放，会在下一轮轮询直接开录（与"勾选瞬间正在播放"行为一致）
        _autoRecordArmed = true;
        UpdatePlaylistUiState();

        PlayerCombo.DisplayMemberPath = nameof(PlayerOption.Label);
        PlayerCombo.SelectedValuePath = nameof(PlayerOption.AppId);
        _suppressPlayerComboEvent = true;
        PlayerCombo.ItemsSource = new List<PlayerOption> { new("", "自动选择（推荐）") };
        PlayerCombo.SelectedIndex = 0;
        _suppressPlayerComboEvent = false;
        // 让「界面监控 / 自动录制」也按用户上次选定的播放器来判断
        _engine.Media.PreferredAppId = _settings.PreferredPlayerAppId;

        RefreshDeviceList();

        SetStatus("正在初始化系统媒体会话…");
        await _engine.InitializeAsync();
        SetStatus(_engine.Media.IsAvailable
            ? "就绪：在播放器中播放任意歌曲，然后点击下方“开始录制”。"
            : (_engine.Media.LastError ?? "系统媒体会话不可用。"));

        UpdateUiState();
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    /// <summary>保证窗口完整落在屏幕可用区域内并居中（高 DPI / 小屏时必须）。</summary>
    private void FitToWorkArea()
    {
        try
        {
            var area = SystemParameters.WorkArea;
            if (area.Height <= 0 || area.Width <= 0) return;

            if (Height > area.Height - 24) Height = Math.Max(MinHeight, area.Height - 24);
            if (Width > area.Width - 24) Width = Math.Max(MinWidth, area.Width - 24);

            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
            Log.Info($"窗口适配：可用区 {area.Width:0}x{area.Height:0}，窗口 {Width:0}x{Height:0} @ ({Left:0},{Top:0})");
        }
        catch (Exception ex)
        {
            Log.Warn($"适配屏幕尺寸失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 定时轮询

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        RecordResult? finished = null;
        try
        {
            finished = await _engine.TickAsync();
        }
        catch (Exception ex)
        {
            Log.Error("界面轮询异常", ex);
        }

        UpdateNowPlayingUi(_engine.LastInfo);

        if (_engine.IsRecording)
        {
            RecordedTimeText.Text = FormatTime(_engine.Elapsed);
        }

        if (finished is not null) HandleFinished(finished);

        await MaybeAutoStartAsync();
    }

    /// <summary>
    /// 自动录制判定。两种模式（可同时勾选，「整张歌单」是「单首」的超集）：
    ///  · 自动录制（单首）：检测到播放器由「未播放」变为「播放中」时录一首；
    ///  · 自动录制整张歌单：在上一条件之外，还跟踪换歌——上一首录完后自动接着录下一首，每首单独导出。
    /// 用「上升沿 + 换歌」判定，且一次播放/一首歌只触发一次；播放停止后重新武装，
    /// 避免录制结束时（我们主动暂停播放器）被误判成新的播放而反复自动开录。
    /// </summary>
    private async Task MaybeAutoStartAsync()
    {
        var autoSingle = AutoRecordCheck.IsChecked == true;
        var playlist = PlaylistCheck.IsChecked == true;
        if (!autoSingle && !playlist) { _autoRecordArmed = true; return; }
        if (_promptOpen || _autoStarting || _engine.State != RecorderState.Idle) return;

        var info = _engine.LastInfo;
        if (info is null || !info.IsPlaying)
        {
            _autoRecordArmed = true;   // 播放已停止/暂停：重新武装，等下一次播放
            return;
        }

        var risingEdge = _autoRecordArmed;   // 刚刚开始播放
        // 歌单连录：换到别的歌了（只在能读到歌曲名时判定，否则无法区分"换歌"和"同一首"）
        var trackChanged = playlist && info.HasTrack && info.TrackKey != _lastAutoTrackKey;
        if (!risingEdge && !trackChanged) return;

        _autoRecordArmed = false;
        if (info.HasTrack) _lastAutoTrackKey = info.TrackKey;

        _autoStarting = true;
        try
        {
            if (trackChanged && !risingEdge)
            {
                Log.Info($"歌单连录：检测到换歌 → {info.DisplayName}");
                SetStatus($"歌单连录：检测到换歌，正在把「{info.DisplayName}」倒回开头并开始录制…");
            }
            else
            {
                Log.Info(playlist ? "歌单连录：检测到播放开始" : "自动录制：检测到播放开始");
                SetStatus(playlist
                    ? "歌单连录：检测到播放开始，正在从头录制；放完会自动接着录下一首…"
                    : "自动录制：检测到播放开始，正在把歌曲倒回开头并开始录制…");
            }
            // 换歌触发时强制回到 0:00：新歌此时已经播了一小段，靠"位置判据"会误判成"已在开头"
            await StartRecordingAsync(auto: true, forceRestart: trackChanged);
        }
        finally
        {
            _autoStarting = false;
        }
    }

    private void AutoRecordCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;   // XAML 初始化阶段不处理

        _settings.AutoStartRecordingWhenSongDetected = AutoRecordCheck.IsChecked == true;
        _settings.Save();
        _autoRecordArmed = true;   // 勾选瞬间若正在播放，下一轮轮询即开始录制

        if (AutoRecordCheck.IsChecked == true)
        {
            Log.Info("自动录制已开启");
            SetStatus("自动录制已开启：在播放器里点播放就会自动倒回开头并开始录制这一首（不追踪换歌）。");
        }
        else
        {
            Log.Info("自动录制已关闭");
            SetStatus(PlaylistCheck.IsChecked == true
                ? "自动录制已关闭（歌单连录仍开启）。"
                : "自动录制已关闭，改回手动点击「开始录制」。");
        }
    }

    private void PlaylistCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _settings.AutoRecordPlaylistMode = PlaylistCheck.IsChecked == true;
        _settings.Save();
        _autoRecordArmed = true;
        _lastAutoTrackKey = "";
        UpdatePlaylistUiState();

        if (PlaylistCheck.IsChecked == true)
        {
            Log.Info("歌单连录已开启");
            SetStatus("歌单连录已开启：点播放开始录制，每首放完自动接着录下一首（每首单独导出）。");
        }
        else
        {
            Log.Info("歌单连录已关闭");
            SetStatus("歌单连录已关闭。");
        }
        UpdateUiState();
    }

    /// <summary>歌单连录时必须连续播放，所以「结束后自动暂停播放」在该模式下不生效（置灰）。</summary>
    private void UpdatePlaylistUiState()
    {
        var playlist = PlaylistCheck.IsChecked == true;
        var busy = _engine.State is RecorderState.Preparing or RecorderState.Finishing;
        PauseCheck.IsEnabled = !playlist && !_engine.IsRecording && !busy;
        PauseCheck.Opacity = playlist ? 0.45 : 1.0;
        PauseCheck.ToolTip = playlist
            ? "歌单连录需要连续播放，每首录完不会暂停播放器，因此该项在当前模式下不生效。"
            : "录制结束后自动暂停播放器，避免继续播下一首（歌单连录时该项不生效）。";
    }

    private void UpdateNowPlayingUi(NowPlayingInfo? info)
    {
        RefreshPlayerList();
        var sessions = _engine.Media.Sessions;
        PlayersText.Text = sessions.Count == 0
            ? "检测到的播放器：—（请先打开播放器并播放歌曲）"
            : "检测到的播放器：" + string.Join("、", sessions.Select(s => s.AppName + (s.IsPlaying ? "（播放中）" : "")));

        if (info is null || !info.HasTrack)
        {
            SongTitleText.Text = "未检测到正在播放的歌曲";
            SongArtistText.Text = "打开 QQ音乐 / 网易云音乐 / 酷狗音乐 播放歌曲后，这里会显示歌曲信息";
            SourceText.Text = "来源：未检测";
            ProgressText.Text = "--:-- / --:--";
            SongProgress.Value = 0;
            return;
        }

        SongTitleText.Text = info.Title;
        var sub = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Artist)) sub.Add(info.Artist);
        if (!string.IsNullOrWhiteSpace(info.Album)) sub.Add($"《{info.Album}》");
        if (info.FromWindowTitle) sub.Add("（歌曲信息来自窗口标题）");
        SongArtistText.Text = sub.Count > 0 ? string.Join(" · ", sub) : "（未提供歌手信息）";

        SourceText.Text = $"来源：{info.SourceAppName} · {DescribeStatus(info.PlaybackStatus)}";

        if (info.HasDuration)
        {
            SongProgress.Maximum = Math.Max(1, info.Duration.TotalSeconds);
            SongProgress.Value = Math.Min(info.Position.TotalSeconds, SongProgress.Maximum);
            ProgressText.Text = $"{FormatTime(info.Position)} / {FormatTime(info.Duration)}";
        }
        else if (_engine.IsRecording)
        {
            // 播放器不提供进度（QQ音乐 / 网易云音乐）：录制总是从歌曲 0:00 开始，所以录制时长＝歌曲进度
            SongProgress.Maximum = 1;
            SongProgress.Value = 0;
            ProgressText.Text = $"已录制 {FormatTime(_engine.Elapsed)}（该播放器不提供总时长，将在换歌/停止播放时自动结束）";
        }
        else
        {
            SongProgress.Maximum = 1;
            SongProgress.Value = 0;
            ProgressText.Text = "该播放器不提供播放进度（如 QQ音乐 / 网易云音乐），录制时长即为歌曲进度";
        }
    }

    /// <summary>已检测到的播放器列表有变化时刷新下拉框（保留用户选择）。</summary>
    private void RefreshPlayerList()
    {
        var sessions = _engine.Media.Sessions;
        var signature = string.Join("|", sessions.Select(s => s.AppId));
        if (signature == _playerSignature) return;
        _playerSignature = signature;

        var items = new List<PlayerOption> { new("", "自动选择（推荐）") };
        items.AddRange(sessions.Select(s => new PlayerOption(s.AppId, s.AppName + (s.IsPlaying ? "（播放中）" : ""))));

        var previous = (PlayerCombo.SelectedItem as PlayerOption)?.AppId ?? _settings.PreferredPlayerAppId;
        _suppressPlayerComboEvent = true;
        try
        {
            PlayerCombo.ItemsSource = items;
            PlayerCombo.SelectedItem = items.FirstOrDefault(i => i.AppId == previous && i.AppId.Length > 0) ?? items[0];
        }
        finally
        {
            _suppressPlayerComboEvent = false;
        }
    }

    /// <summary>用户手动切换「目标播放器」：立即生效并持久化（监控与自动录制都按它来判断）。</summary>
    private void PlayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPlayerComboEvent || !IsLoaded) return;

        var appId = (PlayerCombo.SelectedItem as PlayerOption)?.AppId ?? "";
        _settings.PreferredPlayerAppId = appId;
        _engine.Media.PreferredAppId = appId;
        _settings.Save();
        Log.Info($"目标播放器已切换为：{(string.IsNullOrEmpty(appId) ? "自动选择" : appId)}");
    }

    private static string DescribeStatus(string status) => status switch
    {
        "Playing" => "播放中",
        "Paused" => "已暂停",
        "Stopped" => "已停止",
        "Closed" => "已关闭",
        _ => status,
    };

    // ------------------------------------------------------------------ 按钮事件

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRecording)
        {
            RecordButton.IsEnabled = false;
            RecordResult? result = null;
            try { result = await _engine.StopAsync(StopReason.Manual); }
            catch (Exception ex) { Log.Error("手动停止失败", ex); }
            RecordButton.IsEnabled = true;
            if (result is not null) HandleFinished(result);
            UpdateUiState();
            return;
        }

        await StartRecordingAsync(auto: false);
    }

    /// <summary>
    /// 真正开始录制。手动点「开始录制」与两种自动录制模式共用这一条路径。
    /// auto=true 时不弹窗（用户可能只是按了播放，不该被对话框打断），失败只写状态栏与日志。
    /// forceRestart=true 用于歌单连录的换歌触发：强制把新歌倒回 0:00。
    /// </summary>
    private async Task<bool> StartRecordingAsync(bool auto, bool forceRestart = false)
    {
        if (_engine.State is RecorderState.Recording or RecorderState.Preparing) return false;

        SaveSettingsFromUi();
        var playlist = PlaylistCheck.IsChecked == true;
        var options = new RecorderOptions
        {
            OutputFolder = _settings.OutputFolder,
            Bitrate = _settings.Bitrate,
            DeviceIndex = _settings.CaptureDeviceNumber,
            RestartFromStart = RestartCheck.IsChecked == true,
            ForceRestart = forceRestart,
            PreferredAppId = _settings.PreferredPlayerAppId,
            // 歌单连录必须让播放器连续播放，否则每首录完就暂停、无法接着录下一首
            PausePlaybackWhenDone = PauseCheck.IsChecked == true && !playlist,
        };

        RecordButton.IsEnabled = false;
        StartResult start;
        try
        {
            start = await _engine.StartAsync(options);
        }
        catch (Exception ex)
        {
            Log.Error("开始录制异常", ex);
            start = StartResult.Fail($"开始录制失败：{ex.Message}");
        }
        RecordButton.IsEnabled = true;

        if (!start.Success)
        {
            var message = start.Error ?? "开始录制失败。";
            Log.Warn($"开始录制失败：{message}");
            SetStatus(auto ? $"自动录制未开始：{message}" : message);
            if (!auto)
            {
                MessageBox.Show(this, message, "MusicRecorder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            UpdateUiState();
            return false;
        }

        if (!string.IsNullOrWhiteSpace(start.Warning)) SetStatus("⚠ " + start.Warning);
        LastFileText.Text = $"正在录制到：{start.FilePath}";
        UpdateUiState();
        return true;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择导出目录",
                InitialDirectory = Directory.Exists(OutputFolderBox.Text)
                    ? OutputFolderBox.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            };
            if (dialog.ShowDialog(this) == true)
            {
                OutputFolderBox.Text = dialog.FolderName;
                SaveSettingsFromUi();
            }
        }
        catch (Exception ex)
        {
            Log.Error("选择目录失败", ex);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = OutputFolderBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开目录失败", ex);
            SetStatus($"无法打开目录：{ex.Message}");
        }
    }

    private void RefreshDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshDeviceList();
        SetStatus($"已刷新录音设备，共 {Math.Max(0, DeviceCombo.Items.Count - 1)} 个可用播放设备。");
    }

    // ------------------------------------------------------------------ 录制结果

    private void HandleFinished(RecordResult result)
    {
        foreach (var warning in result.Warnings) Log.Warn(warning);

        var hasFile = !string.IsNullOrEmpty(result.FilePath) && File.Exists(result.FilePath);

        // 时长过短 + 被手动暂停/打断：先问一句是否仍要导出，选「否」直接删掉录音文件
        if (hasFile && result.NeedsExportConfirmation && !ConfirmShortRecording(result))
        {
            DiscardRecording(result);
            UpdateUiState();
            return;
        }

        if (hasFile)
        {
            LastFileText.Text = $"上次导出：{result.FilePath}";
            var extra = result.Warnings.Count > 0 ? "（" + string.Join("；", result.Warnings) + "）" : "";
            SetStatus($"已导出：{Path.GetFileName(result.FilePath)}｜时长 {FormatTime(result.Duration)}{extra}");

            if (OpenFolderCheck.IsChecked == true)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.FilePath}\"") { UseShellExecute = true });
                }
                catch (Exception ex) { Log.Warn($"打开导出目录失败：{ex.Message}"); }
            }
        }
        else
        {
            var message = result.Warnings.Count > 0 ? string.Join("\r\n", result.Warnings) : "录制结束，但没有生成文件。";
            SetStatus(message);
            MessageBox.Show(this, message, "MusicRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateUiState();
    }

    /// <summary>询问用户是否导出这段「时长过短 + 被打断」的录音。返回 true = 导出。</summary>
    private bool ConfirmShortRecording(RecordResult result)
    {
        _promptOpen = true;   // 模态期间定时器仍在跑，别让自动录制在此期间开录
        try
        {
            var answer = MessageBox.Show(this,
                $"录制时长过短（{FormatTime(result.Duration)}），音频可能被手动暂停或打断。\r\n\r\n" +
                $"是否仍要导出这段音频？\r\n\r\n" +
                $"文件：{Path.GetFileName(result.FilePath)}\r\n" +
                $"目录：{Path.GetDirectoryName(result.FilePath)}",
                "MusicRecorder · 录制时长过短", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return false;
            Log.Info($"用户选择导出过短录音：{result.FilePath}");
            return true;
        }
        finally
        {
            _promptOpen = false;
        }
    }

    /// <summary>按用户要求丢弃录音：删除已经生成的文件，且不更新「上次导出」。</summary>
    private void DiscardRecording(RecordResult result)
    {
        var name = Path.GetFileName(result.FilePath);
        try
        {
            File.Delete(result.FilePath);
            result.Discarded = true;
            result.FilePath = "";
            Log.Info($"用户选择不导出，已删除录音文件：{name}（时长 {FormatTime(result.Duration)}）");
            SetStatus($"已按你的选择丢弃这段录音（时长 {FormatTime(result.Duration)}），未生成文件。");
        }
        catch (Exception ex)
        {
            Log.Error($"删除录音文件失败：{name}", ex);
            SetStatus($"录制时长过短，但删除文件失败：{ex.Message}");
            MessageBox.Show(this,
                $"未能删除录音文件：\r\n{ex.Message}\r\n\r\n文件仍在：{result.FilePath}",
                "MusicRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ 引擎事件（后台线程）

    private void OnEngineStatus(string message) => Dispatcher.BeginInvoke(() => SetStatus(message));

    private void OnEngineStateChanged() => Dispatcher.BeginInvoke(UpdateUiState);

    private void OnEngineLevel(float level) => Dispatcher.BeginInvoke(() =>
    {
        try { LevelBar.Value = Math.Clamp(level * 100f, 0f, 100f); }
        catch { /* ignore */ }
    });

    private void UpdateUiState()
    {
        var recording = _engine.IsRecording;
        var busy = _engine.State is RecorderState.Preparing or RecorderState.Finishing;

        RecordButton.Content = recording ? "■  停止并保存" : "●  开始录制";
        RecordButton.Background = recording ? DangerBrush : AccentBrush;
        RecordButton.IsEnabled = !busy;

        DeviceCombo.IsEnabled = !recording && !busy;
        PlayerCombo.IsEnabled = !recording && !busy;
        BitrateCombo.IsEnabled = !recording && !busy;
        OutputFolderBox.IsEnabled = !recording && !busy;
        RestartCheck.IsEnabled = !recording && !busy;
        BrowseButton.IsEnabled = !recording && !busy;
        UpdatePlaylistUiState();   // 内含 PauseCheck 的可用性（歌单连录时置灰）

        RecordStateText.Text = _engine.State switch
        {
            RecorderState.Recording => "● 录制中",
            RecorderState.Preparing => "准备中…",
            RecorderState.Finishing => "正在保存…",
            _ => "待机中",
        };
        RecordStateText.Foreground = recording ? DangerBrush : (Brush)FindResource("TextBrush");
        if (!recording) LevelBar.Value = 0;
    }

    private void SetStatus(string message)
    {
        _lastStatus = message;
        StatusText.Text = message;
    }

    // ------------------------------------------------------------------ 设置

    private void SaveSettingsFromUi()
    {
        _settings.OutputFolder = OutputFolderBox.Text?.Trim() ?? AppSettings.DefaultOutputFolder();
        _settings.Bitrate = BitrateCombo.SelectedValue is int bitrate ? bitrate : 320;
        _settings.CaptureDeviceNumber = DeviceCombo.SelectedItem is DeviceInfo device ? device.Index : -1;
        _settings.RestartFromStart = RestartCheck.IsChecked == true;
        _settings.PausePlaybackWhenDone = PauseCheck.IsChecked == true;
        _settings.OpenFolderWhenDone = OpenFolderCheck.IsChecked == true;
        _settings.AutoStartRecordingWhenSongDetected = AutoRecordCheck.IsChecked == true;
        _settings.AutoRecordPlaylistMode = PlaylistCheck.IsChecked == true;

        // 目标播放器：只在用户确实选中了某个播放器时更新。
        // 下拉框会随会话列表刷新而重建，若此时用户选的播放器没在运行，选中项会回落为「自动选择」，
        // 这里不能因此把用户的选择抹掉。
        var pickedAppId = (PlayerCombo.SelectedItem as PlayerOption)?.AppId;
        if (!string.IsNullOrEmpty(pickedAppId)) _settings.PreferredPlayerAppId = pickedAppId;
        _engine.Media.PreferredAppId = _settings.PreferredPlayerAppId;
        _settings.Save();
    }

    private void RefreshDeviceList()
    {
        var items = new List<DeviceInfo> { new(-1, "系统默认播放设备", true) };
        items.AddRange(AudioRecorder.GetRenderDevices());
        DeviceCombo.ItemsSource = items;

        var target = items.FirstOrDefault(d => d.Index == _settings.CaptureDeviceNumber);
        DeviceCombo.SelectedItem = target ?? items[0];
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        SaveSettingsFromUi();
        if (_forceClose || !_engine.IsRecording) return;

        e.Cancel = true;
        var answer = MessageBox.Show(this,
            "正在录制中。是否停止录制并保存文件后退出？", "MusicRecorder",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        RecordButton.IsEnabled = false;
        try
        {
            var result = await _engine.StopAsync(StopReason.Manual);
            if (result is not null) HandleFinished(result);
        }
        catch (Exception ex)
        {
            Log.Error("退出时停止录制失败", ex);
        }
        _forceClose = true;
        Close();
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }
}
