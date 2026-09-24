using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicRecorder.Core;

public sealed record DeviceInfo(int Index, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"（默认）{Name}" : Name;
}

public sealed class TrackTags
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
}

/// <summary>
/// WASAPI 环回（Loopback）内录：直接抓取系统播放的声音 -> 16bit PCM -> MP3。
/// 主编码器使用 LAME（自带 ID3），若 LAME 不可用则自动退化为
/// 先录 WAV 再用 Windows Media Foundation 转 MP3。
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private IWaveProvider? _provider;
    private IAudioSink? _sink;
    private Thread? _pump;
    private volatile bool _pumping;
    private long _bytesWritten;
    private int _avgBytesPerSecond;
    private long _lastLevelTick;
    private float _pendingPeak;

    public bool IsRecording { get; private set; }

    /// <summary>采集过程异常中断（例如设备被拔掉）。</summary>
    public bool CaptureAborted { get; private set; }

    public string? OutputPath { get; private set; }
    public WaveFormat? SourceFormat { get; private set; }
    public WaveFormat? EncodedFormat { get; private set; }
    public string EncoderName { get; private set; } = "";

    /// <summary>峰值电平 0..1（后台线程触发，界面需自行调度到 UI 线程）。</summary>
    public event Action<float>? LevelChanged;

    public TimeSpan Recorded
    {
        get
        {
            var bytesPerSecond = _avgBytesPerSecond;
            if (bytesPerSecond <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds((double)Interlocked.Read(ref _bytesWritten) / bytesPerSecond);
        }
    }

    public static List<DeviceInfo> GetRenderDevices()
    {
        var list = new List<DeviceInfo>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defaultId = null;
            try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.ID; }
            catch (Exception ex) { Log.Warn($"获取默认播放设备失败：{ex.Message}"); }

            int index = 0;
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var isDefault = !string.IsNullOrEmpty(defaultId) && d.ID == defaultId;
                list.Add(new DeviceInfo(index++, d.FriendlyName, isDefault));
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举播放设备失败", ex);
        }
        return list;
    }

    public void Start(int deviceIndex, string outputPath, int bitrate, TrackTags tags)
    {
        if (IsRecording) throw new InvalidOperationException("录音已在进行中");
        CaptureAborted = false;
        _bytesWritten = 0;
        OutputPath = outputPath;

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        MMDevice? device = null;
        if (deviceIndex >= 0)
        {
            using var en = new MMDeviceEnumerator();
            var devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            if (deviceIndex < devices.Count) device = devices[deviceIndex];
        }

        _capture = device is null ? new WasapiLoopbackCapture() : new WasapiLoopbackCapture(device);
        SourceFormat = _capture.WaveFormat;
        Log.Info($"开始采集：设备={(device?.FriendlyName ?? "默认")}，源格式={SourceFormat}");

        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(20),
            DiscardOnBufferOverflow = true,
            // 必须为 false：Read 在无数据时返回 0，由泵循环按真实时间补静音，
            // 否则 ReadFully=true 会以远快于实时的速度用静音填充，产出超长 MP3。
            ReadFully = false,
        };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        var samples = ToSampleProvider(_buffer);
        if (samples.WaveFormat.Channels > 2) samples = new DownmixToStereoProvider(samples);
        if (samples.WaveFormat.Channels < 1) throw new InvalidOperationException("音频设备声道数异常");

        var rate = samples.WaveFormat.SampleRate;
        if (rate > 48000) samples = new WdlResamplingSampleProvider(samples, 48000);

        _provider = new SampleToWaveProvider16(samples);
        EncodedFormat = _provider.WaveFormat;
        _avgBytesPerSecond = _provider.WaveFormat.AverageBytesPerSecond;

        _sink = CreateSink(outputPath, _provider.WaveFormat, bitrate, tags);

        _capture.StartRecording();
        _pumping = true;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "MusicRecorder-AudioPump", Priority = ThreadPriority.AboveNormal };
        _pump.Start();
        IsRecording = true;
    }

    /// <summary>停止录制并完成编码，返回最终文件（MP3；若编码失败则为 WAV）。</summary>
    public string? Stop()
    {
        if (_capture is null && _sink is null) return null;

        IsRecording = false;
        try { _capture?.StopRecording(); }
        catch (Exception ex) { Log.Warn($"停止采集异常：{ex.Message}"); }

        // 让缓冲区剩余音频（歌曲结尾淡出）排空
        Thread.Sleep(400);

        _pumping = false;
        try { _pump?.Join(5000); } catch { /* ignore */ }

        string? result = null;
        try { result = _sink?.Complete(); }
        catch (Exception ex) { Log.Error("完成编码失败", ex); }

        Cleanup();
        OutputPath = result;
        return result;
    }

    private void Cleanup()
    {
        try { if (_capture is not null) { _capture.DataAvailable -= OnDataAvailable; _capture.RecordingStopped -= OnRecordingStopped; _capture.Dispose(); } } catch { }
        try { _sink?.Dispose(); } catch { }
        _capture = null;
        _sink = null;
        _buffer = null;
        _provider = null;
        _pump = null;
    }

    public void Dispose() => Cleanup();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded > 0 && _buffer is not null) _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            Log.Warn($"缓冲音频失败：{ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Log.Error("采集流异常停止", e.Exception);
            CaptureAborted = true;
        }
    }

    private void PumpLoop()
    {
        var provider = _provider;
        var sink = _sink;
        if (provider is null || sink is null) return;

        var format = provider.WaveFormat;
        var bytesPerSecond = format.AverageBytesPerSecond;
        var buffer = new byte[Math.Max(4096, bytesPerSecond / 10)];
        var silence = new byte[buffer.Length];
        var stopwatch = Stopwatch.StartNew();

        while (_pumping)
        {
            int read;
            try { read = provider.Read(buffer, 0, buffer.Length); }
            catch (Exception ex) { Log.Error("读取音频数据失败", ex); CaptureAborted = true; break; }

            try
            {
                if (read > 0)
                {
                    sink.Write(buffer, 0, read);
                    Interlocked.Add(ref _bytesWritten, read);
                    UpdateLevel(buffer, read);
                }
                else
                {
                    // 设备此刻没有数据（系统静音 / 音频引擎空闲）：
                    // 只在"落后于真实时间"时补静音，保证 MP3 时长 == 实际录制时长。
                    var target = (long)(stopwatch.Elapsed.TotalSeconds * bytesPerSecond);
                    var deficit = target - Interlocked.Read(ref _bytesWritten);
                    var blockAlign = Math.Max(1, format.BlockAlign);
                    var count = (int)Math.Min(silence.Length, deficit - deficit % blockAlign);
                    if (count >= blockAlign)
                    {
                        sink.Write(silence, 0, count);
                        Interlocked.Add(ref _bytesWritten, count);
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }
            }
            catch (Exception ex) { Log.Error("写入音频数据失败", ex); CaptureAborted = true; break; }
        }
    }

    private void UpdateLevel(byte[] buffer, int count)
    {
        var handler = LevelChanged;
        if (handler is null) return;

        int peak = 0;
        for (int i = 0; i + 1 < count; i += 2)
        {
            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            var abs = sample == short.MinValue ? 32767 : Math.Abs((int)sample);
            if (abs > peak) peak = abs;
        }
        var level = peak / 32768f;
        if (level > _pendingPeak) _pendingPeak = level;

        var now = Environment.TickCount64;
        if (now - _lastLevelTick >= 60)
        {
            _lastLevelTick = now;
            var report = _pendingPeak;
            _pendingPeak = 0;
            try { handler(report); } catch { /* ignore */ }
        }
    }

    private static ISampleProvider ToSampleProvider(IWaveProvider provider)
    {
        var fmt = provider.WaveFormat;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat) return new WaveToSampleProvider(provider);
        if (fmt.Encoding == WaveFormatEncoding.Pcm)
        {
            return fmt.BitsPerSample switch
            {
                16 => new Pcm16BitToSampleProvider(provider),
                24 => new Pcm24BitToSampleProvider(provider),
                32 => new Pcm32BitToSampleProvider(provider),
                _ => new WaveToSampleProvider(provider),
            };
        }
        return new WaveToSampleProvider(provider);
    }

    private IAudioSink CreateSink(string outputPath, WaveFormat format, int bitrate, TrackTags tags)
    {
        try
        {
            var sink = new LameSink(outputPath, format, bitrate, tags);
            EncoderName = "LAME 3.100（直接编码 MP3，含 ID3 标签）";
            return sink;
        }
        catch (Exception ex)
        {
            Log.Warn($"LAME 编码器不可用（{ex.GetType().Name}: {ex.Message}），改用 MediaFoundation（先录 WAV 再转 MP3）");
            EncoderName = "Windows Media Foundation（先录 WAV 再转 MP3）";
            return new WavFallbackSink(outputPath, format, bitrate, tags);
        }
    }

    // ------------------------------------------------------------------ 编码目标

    private interface IAudioSink : IDisposable
    {
        void Write(byte[] buffer, int offset, int count);
        string Complete();
    }

    private sealed class LameSink : IAudioSink
    {
        private readonly LameMP3FileWriter _writer;
        private readonly string _path;

        public LameSink(string path, WaveFormat format, int bitrate, TrackTags tags)
        {
            _path = path;
            var id3 = new ID3TagData
            {
                Title = tags.Title,
                Artist = tags.Artist,
                Album = tags.Album,
                Comment = "录制自系统播放（WASAPI 环回）· MusicRecorder",
            };
            _writer = new LameMP3FileWriter(path, format, bitrate, id3);
        }

        public void Write(byte[] buffer, int offset, int count) => _writer.Write(buffer, offset, count);

        public string Complete() => _path;

        public void Dispose()
        {
            try { _writer.Dispose(); } catch (Exception ex) { Log.Error("关闭 MP3 写入器失败", ex); }
        }
    }

    private sealed class WavFallbackSink : IAudioSink
    {
        private readonly WaveFileWriter _wav;
        private readonly string _mp3Path;
        private readonly string _wavPath;
        private readonly int _bitrate;
        private readonly TrackTags _tags;
        private bool _completed;

        public WavFallbackSink(string mp3Path, WaveFormat format, int bitrate, TrackTags tags)
        {
            _mp3Path = mp3Path;
            _bitrate = bitrate;
            _tags = tags;
            var tempDir = Path.Combine(Path.GetTempPath(), "MusicRecorder");
            Directory.CreateDirectory(tempDir);
            _wavPath = Path.Combine(tempDir, $"rec-{Guid.NewGuid():N}.wav");
            _wav = new WaveFileWriter(_wavPath, format);
        }

        public void Write(byte[] buffer, int offset, int count) => _wav.Write(buffer, offset, count);

        public string Complete()
        {
            _completed = true;
            _wav.Dispose();
            try
            {
                MediaFoundationApi.Startup();
                using (var wavReader = new WaveFileReader(_wavPath))
                {
                    MediaFoundationEncoder.EncodeToMp3(wavReader, _mp3Path, _bitrate);
                }
                Id3Writer.WriteTag(_mp3Path, _tags.Title, _tags.Artist, _tags.Album, "录制自系统播放（WASAPI 环回）");
                try { File.Delete(_wavPath); } catch { }
                return _mp3Path;
            }
            catch (Exception ex)
            {
                Log.Error("MediaFoundation 转 MP3 失败，保留 WAV 文件", ex);
                try
                {
                    var fallback = Path.ChangeExtension(_mp3Path, ".wav");
                    File.Move(_wavPath, fallback, overwrite: true);
                    return fallback;
                }
                catch { return _wavPath; }
            }
        }

        public void Dispose()
        {
            if (_completed) return;
            try { _wav.Dispose(); } catch { }
            try { if (File.Exists(_wavPath)) File.Delete(_wavPath); } catch { }
        }
    }
}

/// <summary>多声道（&gt;2）下混为立体声。</summary>
public sealed class DownmixToStereoProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _buffer = Array.Empty<float>();

    public DownmixToStereoProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var channels = _source.WaveFormat.Channels;
        var frames = count / 2;
        var need = frames * channels;
        if (_buffer.Length < need) _buffer = new float[need];

        var read = _source.Read(_buffer, 0, need);
        var readFrames = read / channels;
        for (int f = 0; f < readFrames; f++)
        {
            float left = 0, right = 0;
            for (int c = 0; c < channels; c++)
            {
                var v = _buffer[f * channels + c];
                if (c % 2 == 0) left += v; else right += v;
            }
            buffer[offset + f * 2] = Math.Clamp(left / (channels / 2f), -1f, 1f);
            buffer[offset + f * 2 + 1] = Math.Clamp(right / (channels / 2f), -1f, 1f);
        }
        return readFrames * 2;
    }
}
