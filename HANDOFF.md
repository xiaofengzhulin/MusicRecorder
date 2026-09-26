# MusicRecorder 开发交接上下文（v1.1.0 基线 → 下一版本）

> **用途**：新对话开始时只要读完本文件，就能获得开发所需的全部上下文，无需重新扫描仓库。
> 生成时间：基于 `main` @ tag `v1.1.0`，Release 编译 0 warning / 0 error。
> 面向使用者的完整文档见 [README.md](README.md)；版本变更见 [CHANGELOG.md](CHANGELOG.md)。

---

## 0. 一句话定位

Windows 桌面小工具：**读取音乐软件当前播放的歌曲 → 自动倒回 0:00 → WASAPI 环回内录系统声音 → 曲末/换歌自动停止 → 导出以歌曲名命名的 MP3**（并自动写 ID3 标签、自动暂停播放器）。

## 1. 仓库与当前状态

| 项 | 值 |
| --- | --- |
| 工程目录 | `G:\aiagent\ms\MusicRecorder` |
| 工作区根目录 | `G:\aiagent\ms` —— 根下的 `logs\`、`scan\` 属于**另一个无关项目**（图片恢复脚本），不要改动 |
| Git | `main` 已发布 **v1.1.0**（v1.0.0 = `2c4318a`）。本文件描述的架构与红线对 v1.1.0 及之后的版本都适用 |
| 版本号来源 | `MusicRecorder.csproj` 的 `<Version>1.1.0</Version>`（`build.ps1` 从这里读取，改版本只改这一处；`app.manifest` 里的 assemblyIdentity 也一并改） |
| 未跟踪产物 | `dist\`、`release\`（已被 `.gitignore` 忽略；`*.exe`、`*.pdb`、`*.zip`、`release-notes-*.md` 也忽略） |
| 基线验证 | .NET SDK `8.0.425`（`%USERPROFILE%\.dotnet\dotnet.exe`）；`dotnet build -c Release` → **0 warning / 0 error**，约 7 秒 |
| 目标环境 | Windows 10 2004 (19041)+ / Windows 11，x64；发布版自包含运行时，用户无需装 .NET |

## 2. 技术栈

- **.NET 8 WPF**，`net8.0-windows10.0.19041.0`，`Platforms=x64`，`Nullable=enable`，`ImplicitUsings=enable`，`NoWarn=CA1416`
- **NAudio 2.2.1**（`WasapiLoopbackCapture`、`BufferedWaveProvider`、`WdlResamplingSampleProvider`、`SampleToWaveProvider16`）
- **NAudio.Lame 2.1.0**（LAME 直接编码 MP3；不可用时退化为「先录 WAV → Media Foundation 转 MP3」）
- **WinRT SMTC**（`Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`）读取/控制播放器
- 纯本地程序：**不联网、不上传**，配置与日志只写本机

## 3. 文件地图

| 文件 | 职责 | 关键入口 |
| --- | --- | --- |
| `MusicRecorder.csproj` | 工程定义、版本号、依赖 | `<Version>` |
| `App.xaml(.cs)` | 启动、单实例互斥（`Local\MusicRecorder.SingleInstance`）、命令行自检分流、全局异常处理 | `App.xaml.cs:19` 命令行分支 |
| `MainWindow.xaml(.cs)` | 全部 UI：导出路径 / 播放器与设备与码率 / 歌曲信息与电平 / 大号录制按钮 | `Timer_Tick`（250ms 轮询，`MainWindow.xaml.cs:30`）、`StartRecordingAsync(auto)`、`MaybeAutoStartAsync`（自动录制）、`HandleFinished`/`ConfirmShortRecording`（过短录音确认） |
| `SelfTest.cs` | 自检与开发测试工具（`--selftest/--e2e/--diag/--compare/--gentest/--fakeplayer/--pause`） | `SelfTest.Run(args)` |
| `Core/MediaSessionService.cs` | SMTC 会话：挑会话、读歌曲信息/进度/状态、定位/切歌/暂停控制、诊断导出 | `RefreshAsync`、`TrySeekToStartAsync`、`PauseCurrentAsync` |
| `Core/RecorderEngine.cs` | **业务编排核心**：开始 → 轮询结尾判定 → 停止 → 命名导出；`ShortRecordingThreshold`(10s) 与 `RecordResult.IsInterrupted/NeedsExportConfirmation` 决定是否询问导出 | `StartAsync:106`、`TickAsync:322`、`StopAsync:408` |
| `Core/AudioRecorder.cs` | WASAPI 环回采集 → 下混/重采样 → 16bit PCM → MP3/WAV 落盘；峰值电平、采集中断事件 | `Start:88`、`Stop:142`、`GetRenderDevices:64` |
| `Core/MediaKeySender.cs` | 系统媒体键兜底（`SendInput` + `VK_MEDIA_*`） | `SendPreviousTrack` 等 |
| `Core/WindowProbe.cs` | 无媒体会话时用播放器窗口标题兜底识别 | `TryGetNowPlayingFromWindowTitle` |
| `Core/Id3Writer.cs` | ID3v2.3 标签写入（WAV 兜底编码路径用） | `WriteTag` |
| `Core/AppSettings.cs` | 配置持久化 `%APPDATA%\MusicRecorder\settings.json` | `Load()` / `Save()` |
| `Core/Log.cs` | 日志 `%LOCALAPPDATA%\MusicRecorder\logs\app-yyyyMMdd.log` | `Log.Info/Warn/Error` |
| `build.ps1` | 构建 / `-Publish` 单文件 / `-Package` 打包 zip + SHA256 | — |
| `使用说明.txt` | 面向使用者的说明书（随发布包分发） | — |

`AppSettings` 可持久化字段：`OutputFolder`、`Bitrate`(默认 320)、`CaptureDeviceNumber`(-1=系统默认设备)、`RestartFromStart`、`OpenFolderWhenDone`、`PausePlaybackWhenDone`、`AutoStartRecordingWhenSongDetected`(界面上的「自动录制（单首）」；字段名沿用旧名以兼容已有配置)、`AutoRecordPlaylistMode`(界面上的「自动录制整张歌单」)、`PreferredPlayerAppId`。

## 4. 主流程

```
MainWindow (DispatcherTimer 250ms)
  └─ RecorderEngine.TickAsync()
       ├─ MediaSessionService.RefreshAsync()   ← 每次轮询都刷新会话快照
       ├─ UpdateNowPlayingUi(info)             ← 界面显示（进度条 / 已录制时长 / 播放器列表）
       └─ 命中结尾条件 → StopAsync(reason)      ← 保存、暂停播放、回传 RecordResult
  └─ HandleFinished(result)                     ← 若 NeedsExportConfirmation 先弹窗问是否导出；选「否」删除文件
  └─ MaybeAutoStartAsync()                      ← 自动录制：播放上升沿（单首）／TrackKey 换歌（歌单连录）→ StartRecordingAsync(auto: true, forceRestart: 换歌)
```

`StartAsync` 的顺序（`RecorderEngine.cs:106`）：
1. 状态守卫（Recording/Preparing 时拒绝）→ `Preparing`
2. 惰性初始化 SMTC（失败则明确报错：需 Win10 2004+）
3. `RefreshAsync(forceSessionRescan: true)` 挑目标播放器；无会话 / 无歌曲时给出**可操作的中文提示**并回到 `Idle`
4. 播放器不提供歌曲名时**不拒绝**：警告 + 用 `录音_yyyyMMdd_HHmmss` 命名
5. `EnsurePlayingAsync()`（播放指令失败时用媒体键兜底）
6. `RestartFromStart`（默认开）→ `RestartFromStartAsync`，失败只降级为 warning，不阻断录制
7. `BuildOutputPath`（`文件名 = 净化后的歌曲名.mp3`，重名自动 `(2)`…；异常字符替换、`\s+` 折叠、截断 120 字符）
8. `AudioRecorder.Start(device, path, bitrate, tags)` 启动采集
9. 记录基线 `_baselineKey = info.TrackKey`、`_maxPosition`、`_startedAt` → `Recording`

`StopAsync`（`RecorderEngine.cs:408`）：换歌时**先立刻暂停播放器**（避免下一首被录进去）→ 按 reason 留尾音余量（换歌 150ms / 曲末与停止 700ms `EndTailGrace`）→ `AudioRecorder.Stop()`（内部再等 400ms 排空缓冲、join 采集线程、完成编码）→ 生成 `RecordResult` → 若尚未暂停则再暂停一次 → 录制 <3s 或未生成文件时追加 warning → `Idle`。

## 5. 两个核心算法

### 5.1 「自动倒回开头」四级降级（`RestartFromStartAsync`，`RecorderEngine.cs:233`）

| 级 | 条件（**以播放器能力声明为准**） | 动作 | 成功校验 |
| --- | --- | --- | --- |
| 0 | `TimelineReliable && Position ≤ 1.5s && IsPlaying` | 什么都不做 | — |
| 1 | `SupportsSeek` | `TryChangePlaybackPositionAsync(0)`，等 600ms | 时间轴可信时 `Position ≤ 2.5s`；不可信时无法校验 |
| 2 | `SupportsSkipNext && SupportsSkipPrevious` | **下一曲 → 等 1.1s → 上一曲 → 等 1.4s**（最多 2 轮） | 刷新后 `TrackKey` 回到原歌 → 切回的歌必然从 0:00 开始 |
| 3 | 只有「上一曲」可用 | 上一曲；若真的跳到上一首则下一曲切回 | `TrackKey` 回到原歌 |
| 4 | 兜底 | `MediaKeySender.SendPreviousTrack()` | 同上 |

全部失败 → 仍从当前位置录制，并给出「建议手动拖到 0:00」的中文提示。

### 5.2 「自动停止」判定优先级（`TickAsync`，`RecorderEngine.cs:322`）

判定顺序（命中即停）：
1. **采集流异常中断** `CaptureAborted` → `CaptureError`
2. **连续 8 秒读不到会话**（`_noInfoTicks × 0.25 ≥ 8`）→ `PlaybackStopped`（视为播放器退出）
3. **换歌**：开录 3s 后 `TrackKey != _baselineKey` → `TrackChanged`（仅当开场拿到了歌曲名）
4. **离开 Playing**：连续 3 个 tick（≈0.75s）→ `PlaybackStopped`
5. **曲末**：`HasDuration && Position ≥ Duration − 400ms` → `ReachedEnd`
6. **进度回退兜底**（无总时长的播放器）：已录 >35s 且历史最大进度 >30s 且当前 `Position < 3s` → `ReachedEnd`
7. **超长保护**：>20 分钟 `MaxRecordDuration` → `Timeout`

`TickAsync` 用 `Interlocked.Exchange(ref _tickBusy)` 防重入（上一 tick 未完成时直接返回 null）。

## 6. 设计红线（不要回退这些决策）

1. **一切以播放器"声明的能力"为准，绝不信返回值。** QQ音乐 的 `IsPlaybackPositionEnabled = false`，但 `TryChangePlaybackPositionAsync(0)` **返回 True 却什么都不做**；其 SMTC 时间轴恒为 0（位置/起点/终点/最小定位全 0，更新时间是未刷新的 08:00:00）。旧逻辑因「位置为 0」误判"已在开头"而跳过倒回。→ 新增能力判断必须走 `SupportsSeek / TimelineReliable / SupportsSkipNext` 这类声明字段。
2. **`BufferedWaveProvider.ReadFully` 必须为 `false`**（`AudioRecorder.cs:110-117`）。设为 true 时音频泵会以远快于实时的速度用静音填充，6 秒录制产出 35 分钟 MP3。静音补填由泵循环按真实时间完成。
3. **`NowPlayingInfo.TrackKey` 是"是否换歌"的稳定标识**，不要用 `Title` 直接比较（不同播放器字段缺失程度不同）。
4. **不因"拿不到歌曲名/进度"而拒绝录制**：降级为时间戳命名、显示"已录制时长"（工具总从 0:00 开始，故录制时长＝歌曲进度）并给出提示。
5. **录制结束必须尝试暂停播放器**，且换歌时必须**在停止流程最开始**就暂停（否则下一首会继续播并被录进去）。
6. 界面文案、日志、提示均为**中文**；面向用户的错误提示要可操作（告诉用户下一步做什么）。
7. **自定义 `TextBox` 模板不要再套一次 `Padding`**（`App.xaml` 的 `TextBoxStyle`）。`PART_ContentHost`
   本身会消费 `TextBox.Padding`，模板里再用 `Margin="{TemplateBinding Padding}"` 或 `Border.Padding` 会把
   Padding 算两遍：34 DIP 高的输入框内容区只剩 4 DIP，文字被裁成一排小白点（v1.0.0 的「导出路径」框就是这个 bug，
   与 DPI 无关、150% 缩放下尤其明显）。正确写法见 `App.xaml` 中现有模板 + 样式上的 `VerticalContentAlignment="Center"`。
8. **导出文件名 = `歌曲名-歌手`**（`RecorderEngine.BuildFileBase`），但 **ID3 标签里的 TIT2 必须是纯歌曲名**
   （歌手写在 TPE1）。这两者刻意分开：改文件名时别顺手把 `title` 变量也换成组合串，否则 ID3 标题会被污染。
   重名加 `(2)`、非法字符净化、120 字符截断仍在 `BuildOutputPath` / `SanitizeFileName` 里。
9. **用户选定的「目标播放器」必须对"监控显示"和"自动录制"同样生效**。`MediaSessionService.PickBestSession()`
   只在 `PreferredAppId` 命中时才优先它，因此 `MainWindow` 必须在启动、用户切换下拉框、保存设置三处把
   `Media.PreferredAppId` 同步过去；只把它塞进 `RecorderOptions` 会出现"界面盯着别的播放器、自动录制不触发"。
   另外下拉框会随会话列表刷新而重建，**程序化刷新期间不要改写用户的选择**（`_suppressPlayerComboEvent`）。
10. **「自动录制」必须是"播放上升沿"触发，且一次播放只触发一次**（`_autoRecordArmed`）。若写成"只要在播放就开录"，
   录制结束时我们主动暂停播放器这一动作会被反复判定，造成死循环开录。自动模式下**不要弹模态框**。
11. **「自动录制（单首）」与「自动录制整张歌单」是两个独立开关，前者绝不追踪换歌**（用户明确要求）。
   歌单连录（`PlaylistCheck`）才比对 `TrackKey` 变化续录，且必须满足两条：
     · 换歌触发的开录要设 `RecorderOptions.ForceRestart=true` —— 新歌此时已播了一小段，靠"位置判据"会误判成
       "已在开头"，不强制重定位就会漏掉开头；
     · 该模式下 `PausePlaybackWhenDone` 必须为 false（`StartRecordingAsync` 里已强制），否则每首录完暂停就断了连录；
       界面上把「结束后自动暂停播放」置灰提示（`UpdatePlaylistUiState`）。
    切歌续录必须比对 `TrackKey` 而不是"是否在播放"，并且只在能读到歌曲名（`HasTrack`）时才判定。
12. **界面顶部有一行红色免责声明**（"仅供学习与技术交流使用，严禁任何形式的商业用途"）——这是用户要求的固定文案，
   改版时不要顺手删掉；用户文档（README／使用说明.txt）里也有对应段落。

## 7. 已实测结论（开发机 Windows 11 26200 / Realtek）

- 环回录音 + LAME 编码：8s → 解码 8.49s、320kbps、峰值电平 30%（确实录到声音）。
- PotPlayer 端到端：第 7 秒点录制 → 系统媒体定位到开头 → 整首录完 → `测试歌曲.mp3` 解码 25.48s。
- **QQ音乐**：对 `TryChangePlaybackPositionAsync` 假装成功，改用「下一曲 + 上一曲」后导出正常并自动暂停。
- **音频指纹验证**（`--compare`）：同一首歌两次独立录制互相关最佳匹配 **偏移 0ms / 相关度 0.817**（偏移 50ms 跌到 0.476），不同歌曲对照组最高 0.105 → 倒回开头真实生效。
- 网易云音乐：识别 `人是猫 - 张卡斯` → 导出 `人是猫.mp3`。
- 发布包在干净目录可独立运行，SHA256 一致。

## 8. 已知限制（＝下一版本的改进候选）

| 限制 | 说明 / 可能方向 |
| --- | --- |
| QQ音乐 / 网易云音乐 **不提供进度与总时长** | UI 自动化树为空（腾讯自绘 UI），无法显示百分比进度条；结尾依赖「换歌 / 停止播放」。可考虑：录后按音频静音段/指纹切分、或读取播放器本地数据库 |
| **单曲循环**时无法自动判尾 | 需手动停止，仅有 20 分钟兜底。可考虑：无总时长时用「静音段检测 + 音频自相似」判尾 |
| 录制的是**系统总输出** | 通知音、其他视频声音会被录入。可考虑：按进程分轨（Win10 2004+ `AudioClient` 进程环回 / `ActivateAudioInterfaceAsync`） |
| 无代码签名 | 首次运行有 SmartScreen 提示 |
| 无剪裁/静音检测/元数据回填 | 可考虑：录后自动去除首尾静音、多平台标签补全 |
| ~~无「录整个播放列表」模式~~ | **已实现**：「自动录制整张歌单」= 点播放开始 + 换歌续录 + 每首单独导出（`PlaylistCheck`／`AutoRecordPlaylistMode`）。仍可继续做的：按歌单顺序**跳过已录过的曲目**、失败重试、录制期间的歌单进度面板 |
| 无进度百分比时的曲库级批处理 | 可考虑：批量录制歌单 + 断点续录 |

界面截图式结构、常见问题解答见 [README.md](README.md) 第一、五节。

## 9. 常用命令

```powershell
# 构建（已在 v1.0.0 基线上验证：0 warning / 0 error）
dotnet build MusicRecorder.csproj -c Release

# 发布单文件绿色版 → dist\ ；打包 zip（含 SHA256）→ release\
pwsh -File build.ps1 -Publish
pwsh -File build.ps1 -Package

# 自检 / 诊断（结果同时写 %TEMP%\MusicRecorder-selftest.txt）
MusicRecorder.exe --selftest --seconds=8 --tone   # SMTC/设备检查 + 边放测试音边录
MusicRecorder.exe --e2e --seconds=150             # 对当前播放歌曲走完整录制流程
MusicRecorder.exe --diag --play --seconds=6       # 导出各播放器媒体会话能力 + 实测定位/切歌是否生效
MusicRecorder.exe --compare --a=1.mp3 --b=2.mp3   # 音频指纹：验证两次录制是否都从歌曲开头开始
MusicRecorder.exe --gentest --seconds=25          # 生成 25 秒带 ID3 的测试歌曲
MusicRecorder.exe --fakeplayer --file=<mp3>       # 启动会注册 SMTC 会话的模拟播放器
MusicRecorder.exe --pause                         # 暂停当前播放器
```

**版本发布流程**：改 `csproj` 的 `<Version>` → 更新 `CHANGELOG.md`（Keep a Changelog 风格）→ 可选写 `release-notes-vX.Y.Z.md` → `build.ps1 -Package` → `git commit` + `git tag vX.Y.Z` → 推送 tag（release/ 与 dist/ 不入库）。

**界面渲染调试（高 DPI）**：`G:\aiagent\ms\uiprobe\`（不在 git 仓库内）是一个最小复现探针 WPF 工程，
在真实 DPI（本机 144 / 150%）下并排渲染同一个控件的多种模板写法，输出 `probe.txt`（各控件/`PART_ContentHost`
的 Actual/Desired/Viewport 数值）与 `probe.png`（144 DPI 渲染结果）。加一个变体只需改 `probe.xaml` 并把它加进
`Program.cs` 的 `Names` 数组。定位「文字被裁/不显示/错位」这类问题比直接改主程序快得多。
另外注意：用 DPI 不感知的 PowerShell 抓窗口截图只会得到左上角一块物理区域（本机窗口 900×610 DIP = 1350×915 物理），
别把抓图缺失误判成布局溢出；要核对界面元素真实位置请用 UI Automation 读 `BoundingRectangle`。

## 10. 运行时路径

| 内容 | 路径 |
| --- | --- |
| 配置 | `%APPDATA%\MusicRecorder\settings.json` |
| 日志 | `%LOCALAPPDATA%\MusicRecorder\logs\app-yyyyMMdd.log` |
| 自检输出 | `%TEMP%\MusicRecorder-selftest.txt` |
| 默认导出目录 | `%USERPROFILE%\Music\MusicRecorder` |
| 发布产物 | `dist\MusicRecorder.exe`（≈70MB 单文件）、`release\MusicRecorder-v1.1.0-win-x64.zip` |

---

## 11. 可直接粘贴到新对话的开场提示词

```
我在开发 Windows 音乐内录工具 MusicRecorder，工程在 G:\aiagent\ms\MusicRecorder，
当前基线是 v1.1.0（tag v1.1.0，Release 编译 0 warning）。
请先完整读 G:\aiagent\ms\MusicRecorder\HANDOFF.md，再按需读具体源码；
不要修改 G:\aiagent\ms 根目录下的 logs\、scan\（那是无关项目）。

本次要做的下一版本需求是：<在这里写需求>

约束：
- 保持 .NET 8 WPF + NAudio，界面与提示文案用中文；
- 遵守 HANDOFF.md 第 6 节的「设计红线」（尤其：播放器能力声明优先、ReadFully=false、换歌瞬间先暂停）；
- 改动完成后用 dotnet build -c Release 验证编译通过，并说明如何用 --selftest / --e2e / --diag / --compare 验证行为。
```
