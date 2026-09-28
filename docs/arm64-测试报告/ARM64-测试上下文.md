# MusicRecorder win-arm64 验证包 — 测试上下文（轮次 2）

> 给「另一台 ARM64 设备」上的测试者（人或 DSH 会话）的完整上下文：包在哪、怎么测、什么算通过。
> 轮次 1 报告：`ARM64-测试报告-20260928.md`（已通过，发现 F1 文件句柄泄漏 bug）→ 本包为**修复后复测**。
> 生成：2026-09-28；构建来源：`main` @ `67a4976`。

---

## 1. 被测包

| 项 | 值 |
|---|---|
| 文件名 | `MusicRecorder-v1.1.2-win-arm64.zip` |
| 本机路径（x64 开发机） | `G:\aiagent\ms\MusicRecorder\release\MusicRecorder-v1.1.2-win-arm64.zip` |
| zip SHA256 | `12BDACD3120E9BF927119D1C4C7B75F0AA4CD8AD192F3943CF5044A60EE2343A` |
| zip 大小 | 60.3 MB |
| 包内 `MusicRecorder.exe` SHA256 | `996574CE74EF521B907E9A53DF7D83217FD4D3E9F19429137388CC979D551A09` |
| 架构 | PE Machine `0xAA64`（ARM64 原生） |
| 版本 | `FileVersion=1.1.2.0`，`ProductVersion=1.1.2+67a4976`（对应已推送提交） |
| 形态 | 自包含单文件，无需安装 .NET |

> 轮次 1 的 `MusicRecorder-v1.1.1-win-arm64.zip`（exe `0C284D7A...F1D96`）仍保留在 release 目录，
> 两者**以文件名版本号和哈希区分**，不要混用。

## 2. 本包改了什么（相对轮次 1）

| # | 内容 | 来源 |
|---|---|---|
| **F1 修复** | LAME 不可用时不再泄漏输出文件句柄：`CreateSink` 先用 `NativeLibrary.TryLoad` 探测 LAME 原生库，**可加载才构造 LameSink**；失败兜底里强制 `GC.Collect + WaitForPendingFinalizers()` 释放句柄 → Media Foundation 转码不再撞 `0x80070020`，selftest 应直接产出**有效 MP3**（不再 0 字节 .mp3 残留 + WAV） | 报告 F1 |
| F6 文档 | `使用说明.txt` / README 系统要求加入 ARM64 | 报告 F6 |
| 版本 | 1.1.1 → 1.1.2（csproj + app.manifest） | — |
| 已有改动（轮次1即含） | `--selftest` 架构行用 `RuntimeInformation.ProcessArchitecture` | — |

本机已做双回归（x64 开发机）：
- **R1 有 LAME**：仍走 `LAME 3.100`（探测无误判）；
- **R2 删掉 LAME**：采集期间目标 mp3 **不存在**（结束时才创建）、日志 `LAME 原生库不可用（libmp3lame.64.dll 加载失败）`、产出有效 MP3 78KB、**无 0x80070020**。

## 3. 验证目标（轮次 2 要回答的问题）

| # | 问题 | 对应 |
|---|---|---|
| **Q6** | **F1 是否修复**：selftest 在 LAME 不可用时能否直接产出有效 MP3？ | 报告 `[3] 输出文件=*.mp3`、`解码校验` 行、**日志无 0x80070020**、无 0 字节 .mp3 残留 |
| Q1/Q2/Q4 | 回归轮次 1 结论（原生 arm64、音频驱动、SMTC） | 同轮次 1 判读 |
| Q3 复评 | 本机 MF→MP3 能力（按修正后的判读规则） | 见 §5 |
| **Q5** | 与 **x64 模拟版播放器**混跑（轮次 1 未覆盖） | 需装一个 x64 播放器（见步骤 5） |
| 步骤4 | 过短录音确认弹窗 / 设置持久化（轮次 1 未覆盖） | 见步骤 4 |

---

## 4. 测试步骤

传输：U盘/局域网/网盘（任选），**传完先校验**：

```powershell
(Get-FileHash "…\MusicRecorder-v1.1.2-win-arm64.zip" -Algorithm SHA256).Hash
# 预期 12BDACD3120E9BF927119D1C4C7B75F0AA4CD8AD192F3943CF5044A60EE2343A
```

解压后核对：

```powershell
cd <解压目录>
$b=[IO.File]::ReadAllBytes(".\MusicRecorder.exe")[0..1023]; $pe=[BitConverter]::ToInt32($b,60)
"0x{0:X4}" -f [BitConverter]::ToUInt16($b,$pe+4)      # 预期 0xaa64
(Get-FileHash .\MusicRecorder.exe -Algorithm SHA256).Hash
# 预期 996574CE74EF521B907E9A53DF7D83217FD4D3E9F19429137388CC979D551A09
```

**步骤 1（核心）**：`--selftest --seconds=6`（不加 tone）

```powershell
$p = Start-Process .\MusicRecorder.exe -ArgumentList '--selftest','--seconds=6' -Wait -PassThru
"exit=$($p.ExitCode)"
Get-Content "$env:TEMP\MusicRecorder-selftest.txt" -Encoding UTF8
```

**步骤 2（建议）**：`--selftest --seconds=8 --tone`（扬声器外放 440Hz 测试音 8 秒）→ 峰值应 >1%。

**步骤 3**：GUI 冒烟（窗口/中文/设备列表/手动录一段并播放检查）。

**步骤 4（轮次1未覆盖）**：录制 5 秒手动停止 → 应弹「是否仍要导出这段音频？」；改导出路径 → 重启后设置保留。

**步骤 5（轮次1未覆盖，Q5）**：安装任一 **x64 模拟版**播放器（如 x64 版 PotPlayer/VLC 安装包；在任务管理器
→ 详细信息 里确认其 `MusicRecorder` 之外的播放器进程「体系结构」列为 x64），播放歌曲 → 跑步骤 1，
看 `[1] 会话列表` 能否识别、录音是否含其声音（`--tone` 峰值 0% 但播放中有声即可判断）。

**步骤 6（可选）**：`--e2e --seconds=120`（需有正在播放的歌，可像轮次 1 那样用系统媒体播放器 + 测试音构造）。

---

## 5. 判读标准（轮次 2 修订版）

### Q6 —— F1 回归（本轮核心）

| 观察点 | 预期（修复生效） | 异常（说明没修好，上报） |
|---|---|---|
| `[3] 输出文件` | `…selftest.mp3` | `…selftest.wav` |
| `解码校验` 行 | 存在，时长 ≈6.xx 秒 | 缺失 |
| `%LOCALAPPDATA%\MusicRecorder\logs\` 日志 | `LAME 原生库不可用（libmp3lame.64.dll 加载失败），改用 MediaFoundation…`；**没有** `0x80070020` | 仍有 `MediaFoundation 转 MP3 失败…(0x80070020)` |
| selftest 目录 | 只有 `selftest.mp3`（>0 字节），**无 0 字节 mp3、无 selftest.wav** | 0 字节 mp3 或 wav 残留 |

### 其余判读（按轮次 1 报告修订）

| 位置 | 预期 | 说明 |
|---|---|---|
| 报告第 2 行 | `进程: arm64` | 显示 x64 = 拿错包 |
| `[1] SMTC` | `可用` | 不可用记 `LastError` |
| `[2] 播放设备` | ≥1（轮次 1 已确认驱动正常，本轮应一致） | 0 个 = 回归异常，重点上报 |
| `[3] 编码器` | `Windows Media Foundation（先录 WAV 再转 MP3）` | **预期**：ARM64 包不带 LAME；LAME arm64 交叉编译是后续正式版工作 |
| **（修订）产出 `.wav`** | **不能直接判定为"系统无 MF MP3 编码器"**（轮次1 F2） | 先看日志：`0x80070020`=文件占用（F1 回归失败）；类型不支持类错误才是真的无编码器 |
| **（修订）LAME 日志措辞** | `LAME 原生库不可用（…加载失败）`（新探测逻辑） | 轮次1 的 `FileLoadException/BadImageFormatException` 措辞已过时 |
| `峰值`（--tone） | >1% | 0% = 环回回归异常 |

### 步骤 4 / 步骤 5 判读

- 步骤 4：5 秒停止 → 必须弹确认窗；设置重启保留；
- 步骤 5：x64 模拟版播放器出现在 `[1] 会话列表` 且录音含其声音 → Q5 通过。

---

## 6. 给平板上 DSH 会话的提示词（直接粘贴）

```
你在一台 Windows 11 ARM64 设备上：小米平板 5（代号 nabu），骁龙 860，WOA 社区刷机系统，
本机已装 DeepSeek Harness（DSH）。任务：验证 MusicRecorder v1.1.2 ARM64 验证包（F1 修复复测）。
约束：不安装软件、不改系统设置、不联网下载；只做校验、解压、运行自带自检、读报告。

包：MusicRecorder-v1.1.2-win-arm64.zip（我已放到 <在此填路径>）
  zip SHA256 应为 12BDACD3120E9BF927119D1C4C7B75F0AA4CD8AD192F3943CF5044A60EE2343A
  exe SHA256 应为 996574CE74EF521B907E9A53DF7D83217FD4D3E9F19429137388CC979D551A09
  期望 PE = 0xAA64、FileVersion 1.1.2.0

执行：
1. 校验 zip/exe 哈希与 PE（不符即停止上报）
2. 解压，跑 --selftest --seconds=6（Start-Process -Wait -PassThru 记退出码），
   读 %TEMP%\MusicRecorder-selftest.txt（Get-Content -Encoding UTF8）
3. 读日志 %LOCALAPPDATA%\MusicRecorder\logs\ 下最新文件，
   grep：LAME / 80070020 / 转 MP3 失败
4. 检查 %TEMP%\MusicRecorder\selftest\ 目录产物
5. 按判读标准给结论表（重点 Q6 F1 回归），异常项附原文
6. 第 1 步通过后再跑 --selftest --seconds=8 --tone（会外放 440Hz 测试音 8 秒，
   确认可外放再跑），峰值应 >1%

Q6 判定标准：
- 输出文件 = selftest.mp3 且 >0 字节、有「解码校验」行 → F1 已修复
- 日志只有「LAME 原生库不可用（libmp3lame.64.dll 加载失败）」，无 0x80070020 → 通过
- 若仍出现 0x80070020 / 0 字节 mp3 / 输出 wav → F1 未修复，附完整日志上报

汇报格式：| 项 | 结果 | 关键原文 |，另附退出码与产物清单。
```

---

## 7. 常见故障排查

沿用轮次 1 文档的排查表（SmartScreen、单实例残留、音频设备为 0、SMTC 不可用等），
另加：

| 症状 | 判定 |
|---|---|
| 日志仍出现 `0x80070020` | **F1 回归失败** → 附日志与产物上报（不要下"无编码器"结论） |
| 日志出现 MF 类型不支持/无编码器类错误且产出 wav | 系统真的无 MF MP3 编码器 → 记录（但轮次 1 已证明本机有，出现即异常） |

---

## 8. 验证通过后的后续

1. 编 LAME ARM64（VS 2022「C++ ARM64 生成工具」+ `vcpkg install lame:arm64-windows` →
   `libmp3lame.dll` 改名 `libmp3lame.64.dll`）→ selftest 编码器应显示 `LAME 3.100`；
2. 发布 **v1.1.2 三架构正式版**（win-x64 / win-x86 / win-arm64，CHANGELOG 已写好 [1.1.2] 段）；
3. 补测本包未覆盖的 Q5 与步骤 4；
4. （可选）`SelfTest` 增加「F1 回归探针」：无 LAME 场景断言产物为有效 MP3。
