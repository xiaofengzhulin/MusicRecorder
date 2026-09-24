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

        PlayerCombo.DisplayMemberPath = nameof(PlayerOption.Label);
        PlayerCombo.SelectedValuePath = nameof(PlayerOption.AppId);
        PlayerCombo.ItemsSource = new List<PlayerOption> { new("", "自动选择（推荐）") };
        PlayerCombo.SelectedIndex = 0;

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
        PlayerCombo.ItemsSource = items;
        PlayerCombo.SelectedItem = items.FirstOrDefault(i => i.AppId == previous && i.AppId.Length > 0) ?? items[0];
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

        SaveSettingsFromUi();
        var options = new RecorderOptions
        {
            OutputFolder = _settings.OutputFolder,
            Bitrate = _settings.Bitrate,
            DeviceIndex = _settings.CaptureDeviceNumber,
            RestartFromStart = RestartCheck.IsChecked == true,
            PreferredAppId = _settings.PreferredPlayerAppId,
            PausePlaybackWhenDone = PauseCheck.IsChecked == true,
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
            SetStatus(start.Error ?? "开始录制失败。");
            MessageBox.Show(this, start.Error ?? "开始录制失败。", "MusicRecorder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateUiState();
            return;
        }

        if (!string.IsNullOrWhiteSpace(start.Warning)) SetStatus("⚠ " + start.Warning);
        LastFileText.Text = $"正在录制到：{start.FilePath}";
        UpdateUiState();
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

        if (!string.IsNullOrEmpty(result.FilePath) && File.Exists(result.FilePath))
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
        PauseCheck.IsEnabled = !recording && !busy;
        BrowseButton.IsEnabled = !recording && !busy;

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
        _settings.PreferredPlayerAppId = (PlayerCombo.SelectedItem as PlayerOption)?.AppId ?? "";
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
