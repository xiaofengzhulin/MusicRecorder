import AppKit
import Foundation

enum PlayerSelection: String, CaseIterable, Identifiable {
    case automatic
    case systemMedia
    case appleMusic
    case spotify
    case neteaseMusic
    case manual

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .automatic: return "自动识别（推荐）"
        case .systemMedia: return "系统正在播放（浏览器/QQ音乐等）"
        case .appleMusic: return "Apple Music"
        case .spotify: return "Spotify"
        case .neteaseMusic: return "网易云音乐"
        case .manual: return "手动填写歌曲信息"
        }
    }
}

enum PlaybackState: String {
    case playing
    case paused
    case stopped
    case unknown
}

struct TrackInfo: Equatable {
    let title: String
    let artist: String
    let album: String
    let source: PlayerSelection
    let state: PlaybackState
    let position: TimeInterval
    let duration: TimeInterval
    let playerName: String
    let bundleIdentifier: String
    let metadataUpdatedAt: Date?

    init(
        title: String,
        artist: String,
        album: String,
        source: PlayerSelection,
        state: PlaybackState,
        position: TimeInterval,
        duration: TimeInterval,
        playerName: String = "",
        bundleIdentifier: String = "",
        metadataUpdatedAt: Date? = nil
    ) {
        self.title = title
        self.artist = artist
        self.album = album
        self.source = source
        self.state = state
        self.position = position
        self.duration = duration
        self.playerName = playerName
        self.bundleIdentifier = bundleIdentifier
        self.metadataUpdatedAt = metadataUpdatedAt
    }

    var key: String {
        title.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        + "\u{1}"
        + artist.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }

    var displayName: String {
        artist.isEmpty ? title : "\(title) - \(artist)"
    }

    var sourceDisplayName: String {
        playerName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? source.displayName
            : playerName
    }

    var canTriggerAutomaticRecording: Bool {
        if state == .playing { return true }
        guard state == .unknown, let metadataUpdatedAt else { return false }
        return abs(metadataUpdatedAt.timeIntervalSinceNow) <= 8
    }
}

enum PlaybackServiceError: LocalizedError {
    case playerNotRunning(String)
    case noTrack(String)
    case automationDenied(String)
    case mediaControlDenied

    var errorDescription: String? {
        switch self {
        case .playerNotRunning(let name):
            return "\(name) 没有运行。"
        case .noTrack(let name):
            return "\(name) 当前没有可读取的歌曲。"
        case .automationDenied(let detail):
            return "无法读取或控制播放器：\(detail)。请在“系统设置 → 隐私与安全性 → 自动化”中允许 MusicRecorder 控制播放器。"
        case .mediaControlDenied:
            return "需要“辅助功能”权限才能通过系统媒体键控制播放器。请在“系统设置 → 隐私与安全性 → 辅助功能”中启用 MusicRecorder。"
        }
    }
}

/// Apple Music and Spotify expose stable AppleScript dictionaries. NetEase Music does
/// not, so it is read through the system now-playing session with its local history file
/// as a compatibility fallback for older 2.x releases.
@MainActor
final class PlaybackService {
    private let delimiter = "\u{1f}"

    func refresh(selection: PlayerSelection) throws -> TrackInfo? {
        switch selection {
        case .manual:
            return nil
        case .systemMedia:
            return try querySystemMedia()
        case .appleMusic:
            return try query(.appleMusic)
        case .spotify:
            return try query(.spotify)
        case .neteaseMusic:
            return try query(.neteaseMusic)
        case .automatic:
            if let systemInfo = try? querySystemMedia() {
                return systemInfo
            }
            let candidates = runningPlayers()
            var first: TrackInfo?
            var lastError: Error?
            for player in candidates {
                do {
                    let info = try query(player)
                    if info.state == .playing { return info }
                    if first == nil { first = info }
                } catch {
                    lastError = error
                }
            }
            if let first { return first }
            if let lastError { throw lastError }
            throw PlaybackServiceError.playerNotRunning("系统媒体播放器")
        }
    }

    func restartFromBeginning(_ info: TrackInfo) throws {
        switch info.source {
        case .appleMusic:
            _ = try run(#"tell application "Music" to set player position to 0"#)
            _ = try run(#"tell application "Music" to play"#)
        case .spotify:
            _ = try run(#"tell application "Spotify" to set player position to 0"#)
            _ = try run(#"tell application "Spotify" to play"#)
        case .systemMedia:
            if info.position > 1.5 {
                guard seekSystemToBeginning(info) || MediaKey.send(MediaKey.previous) else {
                    throw PlaybackServiceError.mediaControlDenied
                }
                Thread.sleep(forTimeInterval: 0.2)
            }
            if info.state == .paused || info.state == .stopped {
                guard MediaKey.send(MediaKey.playPause) else {
                    throw PlaybackServiceError.mediaControlDenied
                }
            }
        case .neteaseMusic:
            if info.position > 1.5 {
                let publishedToSystem = info.bundleIdentifier.lowercased() == "com.netease.163music"
                guard (publishedToSystem && seekSystemToBeginning(info))
                        || MediaKey.send(MediaKey.previous) else {
                    throw PlaybackServiceError.mediaControlDenied
                }
                Thread.sleep(forTimeInterval: 0.2)
            }
            if info.state == .paused || info.state == .stopped {
                guard MediaKey.send(MediaKey.playPause) else {
                    throw PlaybackServiceError.mediaControlDenied
                }
            }
        case .automatic, .manual:
            break
        }
    }

    func pause(_ info: TrackInfo) throws {
        switch info.source {
        case .appleMusic:
            _ = try run(#"tell application "Music" to pause"#)
        case .spotify:
            _ = try run(#"tell application "Spotify" to pause"#)
        case .neteaseMusic, .systemMedia:
            if info.state == .playing {
                guard MediaKey.send(MediaKey.playPause) else {
                    throw PlaybackServiceError.mediaControlDenied
                }
            }
        case .automatic, .manual:
            break
        }
    }

    private func runningPlayers() -> [PlayerSelection] {
        var result: [PlayerSelection] = []
        if !NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.Music").isEmpty {
            result.append(.appleMusic)
        }
        if !NSRunningApplication.runningApplications(withBundleIdentifier: "com.spotify.client").isEmpty {
            result.append(.spotify)
        }
        if !NSRunningApplication.runningApplications(withBundleIdentifier: "com.netease.163music").isEmpty {
            result.append(.neteaseMusic)
        }
        return result
    }

    private func query(_ player: PlayerSelection) throws -> TrackInfo {
        let script: String
        let name: String

        switch player {
        case .systemMedia:
            return try querySystemMedia()
        case .appleMusic:
            name = "Apple Music"
            guard !NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.Music").isEmpty else {
                throw PlaybackServiceError.playerNotRunning(name)
            }
            script = #"""
            set d to ASCII character 31
            tell application "Music"
                set s to (player state as text)
                if s is "stopped" then return s & d & "" & d & "" & d & "" & d & "0" & d & "0"
                set t to name of current track
                set ar to artist of current track
                set al to album of current track
                set p to ((player position) as integer) as text
                set du to ((duration of current track) as integer) as text
                return s & d & t & d & ar & d & al & d & p & d & du
            end tell
            """#
        case .spotify:
            name = "Spotify"
            guard !NSRunningApplication.runningApplications(withBundleIdentifier: "com.spotify.client").isEmpty else {
                throw PlaybackServiceError.playerNotRunning(name)
            }
            script = #"""
            set d to ASCII character 31
            tell application "Spotify"
                set s to (player state as text)
                if s is "stopped" then return s & d & "" & d & "" & d & "" & d & "0" & d & "0"
                set t to name of current track
                set ar to artist of current track
                set al to album of current track
                set p to ((player position) as integer) as text
                set du to (((duration of current track) div 1000) as integer) as text
                return s & d & t & d & ar & d & al & d & p & d & du
            end tell
            """#
        case .neteaseMusic:
            name = "网易云音乐"
            guard !NSRunningApplication.runningApplications(withBundleIdentifier: "com.netease.163music").isEmpty else {
                throw PlaybackServiceError.playerNotRunning(name)
            }
            do {
                let snapshot = try SystemNowPlaying.read()
                let source = (snapshot.bundleIdentifier + " " + snapshot.displayName).lowercased()
                guard source.contains("com.netease.163music")
                        || source.contains("netease")
                        || source.contains("网易云") else {
                    throw PlaybackServiceError.noTrack("网易云音乐（当前系统媒体来源：\(snapshot.displayName.isEmpty ? "未知" : snapshot.displayName)）")
                }
                return TrackInfo(
                    title: snapshot.title,
                    artist: snapshot.artist,
                    album: snapshot.album,
                    source: .neteaseMusic,
                    state: snapshot.state,
                    position: snapshot.position,
                    duration: snapshot.duration,
                    playerName: snapshot.displayName,
                    bundleIdentifier: snapshot.bundleIdentifier
                )
            } catch {
                let history = try NeteaseHistoryReader.read()
                return TrackInfo(
                    title: history.title,
                    artist: history.artist,
                    album: history.album,
                    source: .neteaseMusic,
                    state: .unknown,
                    position: history.position,
                    duration: history.duration,
                    playerName: "网易云音乐",
                    bundleIdentifier: "com.netease.163music",
                    metadataUpdatedAt: history.updatedAt
                )
            }
        case .automatic, .manual:
            throw PlaybackServiceError.playerNotRunning("播放器")
        }

        let value = try run(script)
        let fields = value.components(separatedBy: delimiter)
        guard fields.count >= 6 else {
            throw PlaybackServiceError.automationDenied("播放器返回的数据格式无效")
        }

        let state = PlaybackState(rawValue: fields[0].lowercased()) ?? .unknown
        let info = TrackInfo(
            title: fields[1],
            artist: fields[2],
            album: fields[3],
            source: player,
            state: state,
            position: TimeInterval(fields[4]) ?? 0,
            duration: TimeInterval(fields[5]) ?? 0,
            playerName: name,
            bundleIdentifier: player == .appleMusic ? "com.apple.Music" : "com.spotify.client"
        )
        if info.title.isEmpty {
            throw PlaybackServiceError.noTrack(name)
        }
        return info
    }

    private func querySystemMedia() throws -> TrackInfo {
        let snapshot = try SystemNowPlaying.read()
        let bundle = snapshot.bundleIdentifier.lowercased()
        let source: PlayerSelection
        if bundle == "com.apple.music" {
            source = .appleMusic
        } else if bundle == "com.spotify.client" {
            source = .spotify
        } else if bundle == "com.netease.163music" {
            source = .neteaseMusic
        } else {
            source = .systemMedia
        }

        return TrackInfo(
            title: snapshot.title,
            artist: snapshot.artist,
            album: snapshot.album,
            source: source,
            state: snapshot.state,
            position: snapshot.position,
            duration: snapshot.duration,
            playerName: snapshot.displayName,
            bundleIdentifier: snapshot.bundleIdentifier
        )
    }

    private func seekSystemToBeginning(_ original: TrackInfo) -> Bool {
        guard SystemNowPlaying.seekToBeginning() else { return false }
        Thread.sleep(forTimeInterval: 0.25)
        guard let refreshed = try? querySystemMedia(),
              refreshed.key == original.key else {
            return false
        }
        return refreshed.position <= 1.5
    }

    private func run(_ source: String) throws -> String {
        guard let script = NSAppleScript(source: source) else {
            throw PlaybackServiceError.automationDenied("AppleScript 初始化失败")
        }
        var details: NSDictionary?
        let descriptor = script.executeAndReturnError(&details)
        if let details {
            let message = details[NSAppleScript.errorMessage] as? String
                ?? details.description
            throw PlaybackServiceError.automationDenied(message)
        }
        return descriptor.stringValue ?? ""
    }
}
