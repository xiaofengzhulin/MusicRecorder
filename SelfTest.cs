using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MusicRecorder.Core;

namespace MusicRecorder;

/// <summary>
/// 命令行自检（不显示界面）：
///   MusicRecorder.exe --selftest [--seconds=6] [--tone]
///     检查 SMTC 会话检测、播放设备枚举、WASAPI 环回录制并编码 MP3（--tone 会同时播放测试音以验证确实录到了声音）。
///   MusicRecorder.exe --e2e [--seconds=60]
///     端到端测试：走完整录制引擎流程（依赖系统中存在正在播放的歌曲），验证"开始录制 -> 自动检测结束 -> 自动停止并导出 MP3"。
/// 结果写入 %TEMP%\MusicRecorder-selftest.txt，并尽量输出到父控制台。
/// </summary>
public static class SelfTest
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    public static int Run(string[] args)
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { /* 无父控制台时忽略 */ }

        var log = new StringBuilder();
        void Line(string text)
        {
            log.AppendLine(text);
            try { Console.WriteLine(text); } catch { /* ignore */ }
        }

        var mode = args.Any(a => a.Equals("--e2e", StringComparison.OrdinalIgnoreCase)) ? "e2e" : "selftest";
        var seconds = GetInt(args, "--seconds=", 6);
        var tone = args.Any(a => a.Equals("--tone", StringComparison.OrdinalIgnoreCase));

        // 开发自检工具模式
        if (args.Any(a => a.Equals("--gentest", StringComparison.OrdinalIgnoreCase)))
            return GenerateTestSong(args, seconds);
        if (args.Any(a => a.Equals("--fakeplayer", StringComparison.OrdinalIgnoreCase)))
            return RunFakePlayer(args);
        if (args.Any(a => a.Equals("--pause", StringComparison.OrdinalIgnoreCase)))
            return PauseCurrent(args);
        if (args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
            return RunDiagnostics(args);
        if (args.Any(a => a.Equals("--compare", StringComparison.OrdinalIgnoreCase)))
            return CompareRecordings(args);

        var exit = 0;
        try
        {
            Line($"=== MusicRecorder 自检（{mode}）{DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            Line($"系统: {Environment.OSVersion}｜.NET: {Environment.Version}｜进程: {(Environment.Is64BitProcess ? "x64" : "x86")}");
            Line("");

            exit |= CheckMediaSession(Line);
            exit |= CheckDevices(Line);

            exit |= mode == "e2e"
                ? RunEndToEnd(Line, seconds)
                : RunCaptureTest(Line, seconds, tone);
        }
        catch (Exception ex)
        {
            Line("自检异常: " + ex);
            exit = 1;
        }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "MusicRecorder-selftest.txt");
            File.WriteAllText(path, log.ToString(), Encoding.UTF8);
            Line("");
            Line($"结果已写入: {path}");
        }
        catch { /* ignore */ }

        return exit;
    }

    private static int CheckMediaSession(Action<string> line)
    {
        var media = new MediaSessionService();
        var available = media.InitializeAsync().GetAwaiter().GetResult();
        line($"[1] Windows 系统媒体会话（SMTC）: {(available ? "可用" : "不可用 - " + media.LastError)}");

        var info = media.RefreshAsync(forceSessionRescan: true).GetAwaiter().GetResult();
        line("    会话列表: " + (media.Sessions.Count == 0
            ? "（无，说明当前没有播放器在向系统注册媒体会话）"
            : string.Join(" | ", media.Sessions.Select(s => $"{s.AppName}[{s.AppId}]{(s.IsPlaying ? " 播放中" : "")}"))));
        line("    当前歌曲: " + (info is null
            ? "未检测到"
            : $"{info.Title} / {info.Artist} / {info.SourceAppName} / {info.PlaybackStatus} / {info.Position:mm\\:ss} of {(info.HasDuration ? info.Duration.ToString(@"mm\:ss") : "未知")}"));

        var win = WindowProbe.TryGetNowPlayingFromWindowTitle();
        line("    窗口标题兜底探测: " + (win is null
            ? "未检测到播放器窗口"
            : $"{win.Value.Title} - {win.Value.Artist}（{win.Value.AppName}）"));

        line("");
        return available ? 0 : 1;
    }

    private static int CheckDevices(Action<string> line)
    {
        var devices = AudioRecorder.GetRenderDevices();
        line($"[2] 播放设备（环回录制来源）: {devices.Count} 个");
        foreach (var d in devices) line($"    #{d.Index} {d.Name}{(d.IsDefault ? "  ← 默认" : "")}");

        try
        {
            using var en = new MMDeviceEnumerator();
            var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = def.AudioEndpointVolume;
            line($"    默认设备音量: {volume.MasterVolumeLevelScalar * 100:0}%{(volume.Mute ? "（静音！）" : "")}");
            if (volume.Mute || volume.MasterVolumeLevelScalar <= 0.01f)
                line("    ⚠ 默认设备静音或音量为 0，环回录制会得到静音文件。");
        }
        catch (Exception ex)
        {
            line($"    读取音量失败: {ex.Message}");
        }

        line("");
        return devices.Count == 0 ? 1 : 0;
    }

    private static int RunCaptureTest(Action<string> line, int seconds, bool tone)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "MusicRecorder", "selftest");
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir)) { try { File.Delete(f); } catch { } }
        var target = Path.Combine(outDir, "selftest.mp3");

        line($"[3] 环回录制测试: 录制 {seconds} 秒 -> {target}{(tone ? "（同时播放 440Hz 测试音）" : "")}");

        var recorder = new AudioRecorder();
        float peak = 0;
        recorder.LevelChanged += level => { if (level > peak) peak = level; };

        WaveOutEvent? player = null;
        try
        {
            recorder.Start(-1, target, 320, new TrackTags { Title = "自检录音", Artist = "MusicRecorder", Album = "selftest" });
            line($"    源格式: {recorder.SourceFormat}");
            line($"    编码格式: {recorder.EncodedFormat}");
            line($"    编码器: {recorder.EncoderName}");

            if (tone)
            {
                var generator = new SignalGenerator(44100, 2) { Frequency = 440, Gain = 0.3, Type = SignalGeneratorType.Sin };
                player = new WaveOutEvent();
                player.Init(generator);
                player.Play();
            }

            Thread.Sleep(seconds * 1000);
            var path = recorder.Stop();
            line($"    录制时长: {recorder.Recorded:mm\\:ss\\.ff}｜峰值电平: {peak * 100:0.0}%");
            line($"    输出文件: {path}");

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                line("    ✗ 没有生成文件");
                return 1;
            }

            var size = new FileInfo(path).Length;
            line($"    文件大小: {size / 1024.0:0.0} KB");

            if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new Mp3FileReader(path);
                var decoded = ReadPeak(reader);
                line($"    解码校验: 时长 {reader.TotalTime:mm\\:ss\\.ff}，声道 {reader.WaveFormat.Channels}，采样率 {reader.WaveFormat.SampleRate}，解码峰值 {decoded * 100:0.0}%");
                if (size < 4096) { line("    ✗ MP3 文件过小，编码可能失败"); return 1; }
                if (tone && decoded < 0.01f) line("    ⚠ 未在录音中检测到声音（请检查系统音量/静音状态，或该设备不支持环回）");
            }

            line("    ✓ 录制与编码流程正常");
            line("");
            return 0;
        }
        catch (Exception ex)
        {
            line("    ✗ 录制测试失败: " + ex);
            return 1;
        }
        finally
        {
            try { player?.Stop(); player?.Dispose(); } catch { }
            try { recorder.Dispose(); } catch { }
        }
    }

    /// <summary>端到端：完整走录制引擎（需要系统中正在播放的歌曲）。</summary>
    private static int RunEndToEnd(Action<string> line, int maxSeconds)
    {
        line($"[3] 端到端测试：使用录制引擎录制当前歌曲，最长等待 {maxSeconds} 秒");
        var outDir = Path.Combine(Path.GetTempPath(), "MusicRecorder", "e2e");
        Directory.CreateDirectory(outDir);

        var engine = new RecorderEngine();
        engine.StatusChanged += s => line("    · " + s);

        try
        {
            var start = engine.StartAsync(new RecorderOptions
            {
                OutputFolder = outDir,
                Bitrate = 320,
                DeviceIndex = -1,
                RestartFromStart = true,
                PausePlaybackWhenDone = true,
            }).GetAwaiter().GetResult();

            if (!start.Success)
            {
                line("    ✗ 无法开始录制: " + start.Error);
                return 1;
            }

            line($"    开始录制: {start.FilePath}");
            if (!string.IsNullOrWhiteSpace(start.Warning)) line("    ⚠ " + start.Warning);

            var deadline = DateTime.Now.AddSeconds(maxSeconds);
            RecordResult? result = null;
            while (DateTime.Now < deadline)
            {
                var r = engine.TickAsync().GetAwaiter().GetResult();
                if (r is not null) { result = r; break; }
                Thread.Sleep(250);
            }

            if (result is null)
            {
                line("    ⚠ 到达等待上限仍未检测到歌曲结束，手动停止");
                result = engine.StopAsync(StopReason.Timeout).GetAwaiter().GetResult();
            }

            if (result is null || string.IsNullOrEmpty(result.FilePath) || !File.Exists(result.FilePath))
            {
                line("    ✗ 没有生成文件");
                return 1;
            }

            using var reader = new Mp3FileReader(result.FilePath);
            line($"    停止原因: {result.Reason}");
            line($"    结束后已暂停播放: {(result.PlaybackPaused ? "是" : "否")}");
            line($"    输出文件: {result.FilePath}（{new FileInfo(result.FilePath).Length / 1024.0:0.0} KB）");
            line($"    解码时长: {reader.TotalTime:mm\\:ss\\.ff}");
            foreach (var w in result.Warnings) line("    ⚠ " + w);
            line("    ✓ 端到端流程完成");
            line("");
            return reader.TotalTime.TotalSeconds > 5 ? 0 : 1;
        }
        catch (Exception ex)
        {
            line("    ✗ 端到端测试失败: " + ex);
            return 1;
        }
        finally
        {
            engine.Dispose();
        }
    }

    private static float ReadPeak(WaveStream reader)
    {
        var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
        var peak = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i + 1 < read; i += 2)
            {
                var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                var abs = sample == short.MinValue ? 32767 : Math.Abs((int)sample);
                if (abs > peak) peak = abs;
            }
        }
        return peak / 32768f;
    }

    // ================================================================== 开发自检工具

    /// <summary>生成一首带 ID3 标签的测试歌曲（正弦音），用于端到端验证。</summary>
    private static int GenerateTestSong(string[] args, int seconds)
    {
        var path = GetString(args, "--out=") ?? Path.Combine(Path.GetTempPath(), "MusicRecorder", "test-song.mp3");
        var title = GetString(args, "--title=") ?? "测试歌曲";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        seconds = Math.Clamp(seconds, 5, 300);

        try
        {
            using var writer = new LameMP3FileWriter(path, new WaveFormat(44100, 16, 2), 192,
                new ID3TagData { Title = title, Artist = "MusicRecorder 自检", Album = "自检专辑" });

            // 每 2 秒切换一次音调，便于人工听出"确实从头开始播"
            var tones = new[] { 440.0, 554.0, 659.0, 880.0 };
            var chunkSeconds = 2;
            for (var t = 0; t < seconds; t += chunkSeconds)
            {
                var take = Math.Min(chunkSeconds, seconds - t);
                var generator = new SignalGenerator(44100, 2)
                {
                    Frequency = tones[(t / chunkSeconds) % tones.Length],
                    Gain = 0.28,
                    Type = SignalGeneratorType.Sin,
                };
                var provider = new SampleToWaveProvider16(generator);
                var buffer = new byte[provider.WaveFormat.AverageBytesPerSecond];
                var remaining = (long)(take * provider.WaveFormat.AverageBytesPerSecond);
                while (remaining > 0)
                {
                    var read = provider.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    writer.Write(buffer, 0, read);
                    remaining -= read;
                }
            }

            Console.WriteLine($"已生成测试歌曲：{path}（{seconds} 秒，标题「{title}」）");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "MusicRecorder-selftest.txt"),
                $"已生成测试歌曲：{path}\r\n标题：{title}\r\n时长：{seconds} 秒\r\n", Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("生成测试歌曲失败：" + ex);
            return 1;
        }
    }

    /// <summary>模拟一个"会向系统注册 SMTC 媒体会话"的播放器，用于端到端自检。</summary>
    private static int RunFakePlayer(string[] args)
    {
        var file = GetString(args, "--file=") ?? Path.Combine(Path.GetTempPath(), "MusicRecorder", "test-song.mp3");
        var holdSeconds = GetInt(args, "--hold=", 15);

        if (!File.Exists(file))
        {
            Console.WriteLine("找不到音频文件：" + file);
            return 1;
        }

        try
        {
            var player = new Windows.Media.Playback.MediaPlayer
            {
                Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(file)),
                AutoPlay = true,
            };

            var controls = player.SystemMediaTransportControls;
            controls.IsEnabled = true;
            controls.IsPlayEnabled = true;
            controls.IsPauseEnabled = true;
            controls.IsStopEnabled = true;
            controls.IsNextEnabled = true;
            controls.IsPreviousEnabled = true;

            // 像真实音乐软件那样声明歌曲信息（QQ音乐/网易云/酷狗都会这么填）
            var title = GetString(args, "--title=") ?? Path.GetFileNameWithoutExtension(file);
            var updater = controls.DisplayUpdater;
            updater.Type = Windows.Media.MediaPlaybackType.Music;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = "MusicRecorder 自检";
            updater.MusicProperties.AlbumTitle = "自检专辑";
            updater.Update();

            // 响应系统媒体控制请求（模拟真实播放器：上一曲=重播当前歌曲）
            controls.ButtonPressed += (_, e) =>
            {
                try
                {
                    switch (e.Button)
                    {
                        case Windows.Media.SystemMediaTransportControlsButton.Play:
                            player.Play();
                            break;
                        case Windows.Media.SystemMediaTransportControlsButton.Pause:
                            player.Pause();
                            break;
                        case Windows.Media.SystemMediaTransportControlsButton.Previous:
                            if (player.PlaybackSession.Position > TimeSpan.FromSeconds(3))
                                player.PlaybackSession.Position = TimeSpan.Zero;
                            player.Play();
                            break;
                        case Windows.Media.SystemMediaTransportControlsButton.Next:
                            player.PlaybackSession.Position = TimeSpan.Zero;
                            break;
                    }
                }
                catch { }
            };
            controls.PlaybackPositionChangeRequested += (_, e) =>
            {
                try { player.PlaybackSession.Position = e.RequestedPlaybackPosition; } catch { }
            };

            var ended = new ManualResetEventSlim(false);
            player.MediaEnded += (_, _) => ended.Set();
            player.MediaFailed += (_, e) => { Console.WriteLine("播放失败：" + e.ErrorMessage); ended.Set(); };

            player.Play();
            Console.WriteLine($"模拟播放器已启动：{file}");
            Console.WriteLine("（正在向系统注册媒体会话，标题来自文件 ID3 标签）");

            var deadline = DateTime.Now.AddSeconds(holdSeconds + 300);
            var endAt = DateTime.Now.AddSeconds(holdSeconds);
            while (!ended.IsSet && DateTime.Now < endAt && DateTime.Now < deadline) Thread.Sleep(200);

            Console.WriteLine("媒体播放结束，保持会话 " + holdSeconds + " 秒以便观察…");
            Thread.Sleep(holdSeconds * 1000);
            player.Pause();
            player.Dispose();
            Console.WriteLine("模拟播放器退出。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("模拟播放器失败：" + ex);
            return 1;
        }
    }

    /// <summary>暂停当前播放器的播放（自检/维护用）。</summary>
    /// <summary>诊断：导出媒体会话的原始能力信息、播放器窗口标题，并可实测定位/切歌是否生效。</summary>
    /// <summary>
    /// 对比两段录音，验证"是否真的都从歌曲 0:00 开始"：
    /// 对 10ms RMS 包络做互相关扫描，若最佳匹配出现在偏移 0 且相关度很高，说明两段录音起点相同。
    /// 用法：--compare --a=&lt;mp3&gt; --b=&lt;mp3&gt; [--seconds=10]
    /// </summary>
    private static int CompareRecordings(string[] args)
    {
        var a = GetString(args, "--a=");
        var b = GetString(args, "--b=");
        var seconds = GetInt(args, "--seconds=", 10);
        var buffer = new StringBuilder();
        void Line(string text)
        {
            buffer.AppendLine(text);
            try { Console.WriteLine(text); } catch { }
        }

        try
        {
            if (a is null || b is null || !File.Exists(a) || !File.Exists(b))
            {
                Line("用法：--compare --a=<mp3路径> --b=<mp3路径> [--seconds=10]");
                return 1;
            }

            var ea = Envelope(a, seconds);
            var eb = Envelope(b, seconds);
            Line($"A: {Path.GetFileName(a)}（{ea.Length} 个包络点，每点 10ms）");
            Line($"B: {Path.GetFileName(b)}（{eb.Length} 个包络点）");
            Line("");

            var matches = new List<(int Offset, double Score)>();
            for (var offset = 0; offset + 100 <= eb.Length; offset += 5) // 每 50ms 扫一格
            {
                var score = Correlate(ea, eb, offset);
                if (!double.IsNaN(score)) matches.Add((offset, score));
            }
            if (matches.Count == 0)
            {
                Line("数据不足，无法比对。");
                return 1;
            }

            var best = matches.OrderByDescending(m => m.Score).Take(3).ToList();
            Line("最佳匹配（偏移 = B 比 A 晚开始的时间）：");
            foreach (var m in best) Line($"    偏移 {m.Offset * 10,5} ms → 相关度 {m.Score:0.000}");

            var top = best[0];
            Line("");
            Line(top.Offset * 10 <= 300 && top.Score >= 0.8
                ? $"结论：两段录音起点一致（偏移 {top.Offset * 10} ms，相关度 {top.Score:0.000}）→ 都是从歌曲开头开始录制 ✓"
                : $"结论：两段录音起点不同（最佳偏移 {top.Offset * 10} ms，相关度 {top.Score:0.000}）→ B 并非从歌曲开头开始 ✗");

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "MusicRecorder-selftest.txt"), buffer.ToString(), Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            Line("对比失败: " + ex);
            return 1;
        }
    }

    /// <summary>把音频解码成 10ms 一个点的 RMS 包络。</summary>
    private static double[] Envelope(string path, int seconds)
    {
        using var reader = new Mp3FileReader(path);
        var format = reader.WaveFormat;
        var blockSamples = Math.Max(1, format.SampleRate / 100);
        var wanted = (long)seconds * format.SampleRate;
        var values = new List<double>();
        var buffer = new byte[format.AverageBytesPerSecond];
        long done = 0;
        double sum = 0;
        var count = 0;
        int read;
        while (done < wanted && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i + 1 < read; i += 2 * format.Channels)
            {
                var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                sum += (double)sample * sample;
                count++;
                if (count < blockSamples) continue;
                values.Add(Math.Sqrt(sum / count));
                sum = 0;
                count = 0;
                done += blockSamples;
            }
        }
        return values.ToArray();
    }

    private static double Correlate(double[] a, double[] b, int offsetB)
    {
        var n = Math.Min(a.Length, b.Length - offsetB);
        if (n < 20) return double.NaN;
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        for (var i = 0; i < n; i++)
        {
            double x = a[i], y = b[i + offsetB];
            sa += x; sb += y; saa += x * x; sbb += y * y; sab += x * y;
        }
        var cov = sab / n - sa / n * (sb / n);
        var va = saa / n - sa / n * (sa / n);
        var vb = sbb / n - sb / n * (sb / n);
        if (va <= 0 || vb <= 0) return double.NaN;
        return cov / Math.Sqrt(va * vb);
    }

    private static int RunDiagnostics(string[] args)
    {
        var seconds = GetInt(args, "--seconds=", 6);
        var appId = GetString(args, "--player=");
        var play = args.Any(a => a.Equals("--play", StringComparison.OrdinalIgnoreCase));

        var buffer = new StringBuilder();
        void Line(string text)
        {
            buffer.AppendLine(text);
            try { Console.WriteLine(text); } catch { }
        }

        try
        {
            var media = new MediaSessionService();
            if (!string.IsNullOrEmpty(appId)) media.PreferredAppId = appId;
            media.InitializeAsync().GetAwaiter().GetResult();

            Line($"=== 媒体会话诊断 {DateTime.Now:HH:mm:ss} ===");
            Line("");
            Line("【1】会话原始信息");
            Line(media.DumpRawSessionsAsync().GetAwaiter().GetResult());

            Line("【2】播放器窗口");
            Line(WindowProbe.DumpPlayerWindows());

            if (play)
            {
                Line($"【3】确保播放，然后每秒采样时间轴 {seconds} 次");
                var playing = media.EnsurePlayingAsync().GetAwaiter().GetResult();
                Line($"    发送播放指令: {playing}");
                Line(media.SampleTimelineAsync(appId, seconds).GetAwaiter().GetResult());

                Line("【4】实测控制指令");
                Line(media.TestControlsAsync(appId).GetAwaiter().GetResult());

                Line("【5】恢复暂停");
                var paused = media.PauseCurrentAsync().GetAwaiter().GetResult();
                Line($"    暂停结果: {paused}");
            }

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "MusicRecorder-selftest.txt"), buffer.ToString(), Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            Line("诊断失败: " + ex);
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "MusicRecorder-selftest.txt"), buffer.ToString(), Encoding.UTF8); } catch { }
            return 1;
        }
    }

    private static int PauseCurrent(string[] args)
    {
        var media = new MediaSessionService();
        media.InitializeAsync().GetAwaiter().GetResult();
        var info = media.RefreshAsync(true).GetAwaiter().GetResult();
        Console.WriteLine("当前会话：" + (info?.ToString() ?? "未检测到"));
        var ok = media.PauseCurrentAsync().GetAwaiter().GetResult();
        Console.WriteLine("暂停结果：" + ok);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "MusicRecorder-selftest.txt"),
            $"暂停结果：{ok}｜会话：{info}\r\n", Encoding.UTF8);
        return ok ? 0 : 1;
    }

    private static string? GetString(string[] args, string prefix)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a[prefix.Length..].Trim('"');
        }
        return null;
    }

    private static int GetInt(string[] args, string prefix, int fallback)    {
        foreach (var a in args)
        {
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(a[prefix.Length..], out var value))
                return Math.Clamp(value, 2, 600);
        }
        return fallback;
    }
}
