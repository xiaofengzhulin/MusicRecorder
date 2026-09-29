# MusicRecorder for Apple Silicon

这是 MusicRecorder 的原生 macOS 客户端，面向 M1、M2、M3、M4 及后续 Apple Silicon 芯片。

## 功能对应

| Windows 版本 | macOS/Apple Silicon 实现 |
| --- | --- |
| WPF 界面 | SwiftUI 原生界面 |
| WASAPI 环回采集 | ScreenCaptureKit 系统音频捕获 |
| Windows SMTC | macOS“正在播放”通用媒体来源；Apple Music / Spotify 自动化与网易云旧版记录作为兜底 |
| LAME MP3 | 系统 AAC 编码器，导出 `.m4a` |
| Windows 媒体会话定位/暂停 | 系统媒体时间轴定位与媒体键控制；Apple Music / Spotify 使用原生自动化 |

自动读取歌曲信息支持 **Apple Music、Spotify、网易云音乐、QQ 音乐、浏览器和其他第三方播放器**。默认的“自动识别”读取 macOS 控制中心当前媒体；浏览器网页需要支持 Media Session，并向系统提供歌曲名和播放状态。Apple Music / Spotify 保留自动化接口，旧版网易云未发布系统媒体信息时则读取其本地播放记录。

录制模式包括手动、自动单首和自动整张歌单。自动歌单模式检测到换歌后保存上一首，并用新歌的歌曲名和歌手创建独立的 `.m4a` 文件；中途不会暂停播放器。

> 通用适配边界：播放器或网页必须出现在 macOS 控制中心的“正在播放”区域。只输出声音、但不向系统发布标题/状态的应用无法用通用方法可靠识别歌曲和换歌，只能手动填写。个别网页虽发布标题但不支持系统定位，此时无法保证从绝对 0 秒开始，应用会使用媒体键兜底。

> 网易云旧版兼容通道能识别歌曲和换歌，但无法保证实时取得暂停状态和精确进度。自动单首会在本地记录更新时触发，歌单模式可按换歌分段；若单曲循环或末曲结束后记录不再变化，需手动停止。

## 系统要求

- Apple Silicon Mac（arm64）
- macOS 13 Ventura 或更高版本
- 仅构建时需要 Swift 6 / Xcode Command Line Tools（完整 Xcode 也可）

## 构建

```bash
cd macOS
chmod +x build.sh
./build.sh
```

脚本会先运行轻量自检，再直接用本机 Swift 编译器生成 arm64 可执行文件。构建结果位于：

```text
macOS/dist/macos-arm64/MusicRecorder.app
```

应用包使用 ad-hoc 临时签名，适合本机运行和开发验证。正式分发时应换成 Developer ID Application 证书并完成 Apple 公证。

## 首次运行

1. 在音乐 App、第三方播放器或浏览器中播放一首歌，确认它出现在 macOS 控制中心的“正在播放”区域；网易云旧版用户可切换或重新播放一次，让本地播放记录更新。
2. 打开 `MusicRecorder.app`。
3. macOS 请求时，允许“屏幕与系统音频录制”权限；若系统提示重启应用，请退出后重新打开。
4. 使用 Apple Music / Spotify 自动读取时，再允许 MusicRecorder 控制对应播放器；使用通用媒体控制时，请允许“辅助功能”权限。
5. 选择导出目录。手动模式点击“开始录制”；自动单首或整张歌单模式会在检测到播放时自动开始。

权限可在以下位置重新设置：

- `系统设置 → 隐私与安全性 → 屏幕与系统音频录制`
- `系统设置 → 隐私与安全性 → 自动化`
- `系统设置 → 隐私与安全性 → 辅助功能`（通用播放器媒体键控制）

## 为什么导出 M4A 而不是 MP3

macOS 自带稳定的 AAC 编码器，但不提供面向应用的 MP3 编码器。为了保持零第三方运行时依赖，Apple Silicon 版本使用 AAC/M4A；文件可由“音乐”、Finder、QuickTime、iPhone/iPad 及主流播放器直接播放。若必须使用 MP3，可在录制完成后用可信的本地转码工具转换。

## 隐私说明

ScreenCaptureKit 的权限名称同时包含“屏幕”和“系统音频”，但本应用只注册音频输出，不保存屏幕画面，也不采集麦克风。录制时会排除 MusicRecorder 自身产生的声音。
