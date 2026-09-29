import AppKit
import CoreGraphics
import Foundation

@MainActor
final class RecorderViewModel: ObservableObject {
    @Published var outputFolder: String
    @Published var title = ""
    @Published var artist = ""
    @Published var album = ""
    @Published var status = "请先在播放器中播放歌曲，然后读取歌曲信息。"
    @Published var isRecording = false
    @Published var isBusy = false
    @Published var elapsed: TimeInterval = 0
    @Published var lastOutput: URL?
    @Published var playerSelection: PlayerSelection
    @Published var bitrate: Int
    @Published var restartFromBeginning: Bool
    @Published var pauseWhenDone: Bool
    @Published var openFolderWhenDone: Bool
    @Published var automaticMode: AutomaticRecordingMode {
        didSet {
            guard automaticMode != oldValue else { return }
            automaticArmed = true
            lastObservedTrackKey = ""
            notPlayingTicks = 0
            savePreferences()
            startMonitoring()
            if automaticMode != .off {
                status = "自动录制已开启，正在等待播放器开始播放…"
            }
        }
    }

    private let recorder = SystemAudioRecorder()
    private let playback = PlaybackService()
    private var currentTrack: TrackInfo?
    private var baselineTrackKey = ""
    private var startedAt: Date?
    private var monitorTimer: Timer?
    private var notPlayingTicks = 0
    private var pollCounter = 0
    private var stopInProgress = false
    private var automaticArmed = true
    private var lastObservedTrackKey = ""

    init() {
        let defaults = UserDefaults.standard
        let defaultFolder = FileManager.default.urls(for: .musicDirectory, in: .userDomainMask)
            .first?
            .appendingPathComponent("MusicRecorder", isDirectory: true)
            .path
            ?? NSString(string: "~/Music/MusicRecorder").expandingTildeInPath

        outputFolder = defaults.string(forKey: "outputFolder") ?? defaultFolder
        playerSelection = PlayerSelection(rawValue: defaults.string(forKey: "playerSelection") ?? "") ?? .automatic
        bitrate = defaults.object(forKey: "bitrate") as? Int ?? 256
        restartFromBeginning = defaults.object(forKey: "restartFromBeginning") as? Bool ?? true
        pauseWhenDone = defaults.object(forKey: "pauseWhenDone") as? Bool ?? true
        openFolderWhenDone = defaults.object(forKey: "openFolderWhenDone") as? Bool ?? true
        automaticMode = AutomaticRecordingMode(rawValue: defaults.string(forKey: "automaticMode") ?? "") ?? .off
    }

    var elapsedText: String {
        let seconds = max(0, Int(elapsed))
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }

    var canStart: Bool {
        !isBusy && !isRecording && !outputFolder.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    func startMonitoring() {
        guard monitorTimer == nil else { return }
        monitorTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                await self?.tick()
            }
        }
    }

    func chooseOutputFolder() {
        let panel = NSOpenPanel()
        panel.title = "选择录音导出目录"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.directoryURL = URL(fileURLWithPath: outputFolder, isDirectory: true)
        if panel.runModal() == .OK, let url = panel.url {
            outputFolder = url.path
            savePreferences()
        }
    }

    func openOutputFolder() {
        let url = URL(fileURLWithPath: outputFolder, isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        NSWorkspace.shared.open(url)
    }

    func refreshTrack(showErrors: Bool = true) {
        guard !isRecording, playerSelection != .manual else { return }
        do {
            let info = try playback.refresh(selection: playerSelection)
            currentTrack = info
            if let info {
                applyMetadata(info)
                status = "已读取：\(info.displayName)（\(info.sourceDisplayName)）"
            }
        } catch {
            if showErrors { status = error.localizedDescription }
        }
    }

    func startRecording(
        preloadedInfo: TrackInfo? = nil,
        initiatedAutomatically: Bool = false
    ) async {
        guard canStart else { return }
        isBusy = true
        status = initiatedAutomatically ? "检测到播放，正在自动开始录制…" : "正在准备系统音频录制…"
        savePreferences()

        do {
            var info = preloadedInfo
            if playerSelection != .manual, info == nil {
                info = try playback.refresh(selection: playerSelection)
            }
            if let info {
                currentTrack = info
                applyMetadata(info)
            }

            if restartFromBeginning, let initialInfo = info {
                status = "正在把歌曲倒回开头…"
                try playback.restartFromBeginning(initialInfo)
                try await Task.sleep(nanoseconds: 350_000_000)
                if let refreshed = try? playback.refresh(selection: initialInfo.source) {
                    currentTrack = refreshed
                    applyMetadata(refreshed)
                    info = refreshed
                }
            }

            let folder = URL(
                fileURLWithPath: NSString(string: outputFolder).expandingTildeInPath,
                isDirectory: true
            )
            let output = FileNaming.availableURL(
                folder: folder,
                title: title,
                artist: artist,
                extension: "m4a"
            )

            if !CGPreflightScreenCaptureAccess() {
                status = "正在请求“屏幕与系统音频录制”权限…"
                guard CGRequestScreenCaptureAccess() else {
                    throw NSError(
                        domain: "MusicRecorderMac.Permission",
                        code: 1,
                        userInfo: [NSLocalizedDescriptionKey:
                            "未获得系统音频录制权限。请在“系统设置 → 隐私与安全性 → 屏幕与系统音频录制”中启用 MusicRecorder，然后重新打开应用。"]
                    )
                }
            }

            try await recorder.start(
                outputURL: output,
                bitrateKbps: bitrate,
                title: title,
                artist: artist
            )

            baselineTrackKey = info?.key ?? ""
            notPlayingTicks = 0
            startedAt = Date()
            elapsed = 0
            isRecording = true
            if automaticMode != .off { automaticArmed = false }
            status = title.isEmpty
                ? "正在录制系统声音…"
                : "正在录制：\(artist.isEmpty ? title : "\(title) - \(artist)")"
        } catch {
            status = initiatedAutomatically
                ? "自动录制启动失败：\(error.localizedDescription)"
                : error.localizedDescription
        }
        isBusy = false
    }

    func stopRecording(
        reason: String = "已手动停止",
        pausePlayer: Bool? = nil,
        revealOutput: Bool? = nil
    ) async {
        guard isRecording, !stopInProgress else { return }
        stopInProgress = true
        status = "正在完成音频编码…"

        do {
            let url = try await recorder.stop()
            lastOutput = url
            isRecording = false
            elapsed = Date().timeIntervalSince(startedAt ?? Date())

            if pausePlayer ?? pauseWhenDone, let info = currentTrack {
                try? playback.pause(info)
            }

            status = "\(reason)：\(url.lastPathComponent)"
            if revealOutput ?? openFolderWhenDone {
                NSWorkspace.shared.activateFileViewerSelecting([url])
            }
        } catch {
            isRecording = false
            status = error.localizedDescription
        }

        stopInProgress = false
        startedAt = nil
        baselineTrackKey = ""
    }

    private func tick() async {
        guard !stopInProgress, !isBusy else { return }

        if isRecording {
            elapsed = Date().timeIntervalSince(startedAt ?? Date())
            if elapsed >= 20 * 60 {
                await stopRecording(reason: "已达到 20 分钟安全上限")
                return
            }
        } else if automaticMode == .off {
            return
        }

        guard playerSelection != .manual else {
            if automaticMode != .off {
                status = "自动录制需要选择“自动识别”或一个播放器，不能使用手动歌曲信息。"
            }
            return
        }

        pollCounter += 1
        guard pollCounter.isMultiple(of: 2) else { return }

        do {
            let selection = isRecording ? (currentTrack?.source ?? playerSelection) : playerSelection
            guard let info = try playback.refresh(selection: selection) else { return }

            let previousObservedKey = lastObservedTrackKey
            lastObservedTrackKey = info.key
            currentTrack = info

            if isRecording {
                if !baselineTrackKey.isEmpty, info.key != baselineTrackKey {
                    if AutomaticRecordingPolicy.shouldRollPlaylist(
                        mode: automaticMode,
                        baselineKey: baselineTrackKey,
                        currentKey: info.key
                    ) {
                        await rollPlaylistForward(to: info)
                    } else {
                        automaticArmed = false
                        await stopRecording(reason: "检测到换歌，已自动结束")
                    }
                    return
                }

                if info.state != .unknown,
                   info.duration > 5,
                   info.position >= info.duration - 0.6 {
                    await stopRecording(
                        reason: "歌曲播放完毕，已自动结束",
                        pausePlayer: automaticMode == .playlist ? false : nil
                    )
                    if automaticMode == .playlist {
                        automaticArmed = true
                        status += "；正在等待下一首。"
                    }
                    return
                }

                if info.state == .playing {
                    notPlayingTicks = 0
                } else if info.state == .paused || info.state == .stopped {
                    notPlayingTicks += 1
                    if notPlayingTicks >= 2 {
                        await stopRecording(
                            reason: "播放器已暂停或停止，已自动结束",
                            pausePlayer: automaticMode == .playlist ? false : nil
                        )
                        if automaticMode == .playlist {
                            automaticArmed = true
                            status += "；正在等待继续播放。"
                        }
                    }
                }
                return
            }

            guard automaticMode != .off else { return }

            if info.state == .paused || info.state == .stopped {
                automaticArmed = true
                status = "自动录制已开启，正在等待播放器开始播放…"
                return
            }

            let isNewTrack = previousObservedKey.isEmpty || previousObservedKey != info.key
            if AutomaticRecordingPolicy.shouldStart(
                mode: automaticMode,
                armed: automaticArmed,
                canTrigger: info.canTriggerAutomaticRecording,
                isPlaying: info.state == .playing,
                isNewTrack: isNewTrack
            ) {
                automaticArmed = false
                await startRecording(preloadedInfo: info, initiatedAutomatically: true)
            }
        } catch {
            if isRecording {
                status = "正在录制（暂时无法读取播放器状态）：\(error.localizedDescription)"
            } else if automaticMode != .off {
                status = "等待自动录制（暂时未检测到媒体）：\(error.localizedDescription)"
            }
        }
    }

    private func rollPlaylistForward(to nextTrack: TrackInfo) async {
        await stopRecording(
            reason: "上一首已完成",
            pausePlayer: false,
            revealOutput: false
        )
        guard automaticMode == .playlist else { return }
        try? await Task.sleep(nanoseconds: 150_000_000)
        automaticArmed = false
        await startRecording(preloadedInfo: nextTrack, initiatedAutomatically: true)
    }

    private func applyMetadata(_ info: TrackInfo) {
        title = info.title
        artist = info.artist
        album = info.album
    }

    private func savePreferences() {
        let defaults = UserDefaults.standard
        defaults.set(outputFolder, forKey: "outputFolder")
        defaults.set(playerSelection.rawValue, forKey: "playerSelection")
        defaults.set(bitrate, forKey: "bitrate")
        defaults.set(restartFromBeginning, forKey: "restartFromBeginning")
        defaults.set(pauseWhenDone, forKey: "pauseWhenDone")
        defaults.set(openFolderWhenDone, forKey: "openFolderWhenDone")
        defaults.set(automaticMode.rawValue, forKey: "automaticMode")
    }
}
