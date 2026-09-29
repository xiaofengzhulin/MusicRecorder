import AppKit
import CoreGraphics
import Foundation

struct SystemNowPlayingSnapshot {
    let title: String
    let artist: String
    let album: String
    let bundleIdentifier: String
    let displayName: String
    let position: TimeInterval
    let duration: TimeInterval
    let playbackRate: Double?
    let isPlaying: Bool?

    var state: PlaybackState {
        if let isPlaying { return isPlaying ? .playing : .paused }
        guard let playbackRate else { return .unknown }
        return playbackRate > 0.01 ? .playing : .paused
    }
}

enum SystemNowPlayingError: LocalizedError {
    case unavailable(String)
    case timedOut
    case invalidResponse

    var errorDescription: String? {
        switch self {
        case .unavailable(let detail):
            return "无法读取 macOS 正在播放信息：\(detail)"
        case .timedOut:
            return "读取 macOS 正在播放信息超时。"
        case .invalidResponse:
            return "macOS 返回的正在播放信息格式无效。"
        }
    }
}

/// Reads the system-wide Now Playing item through an Apple-signed osascript host.
/// NetEase Music publishes its metadata to MediaRemote but doesn't expose an
/// AppleScript dictionary, so this is the only useful metadata path on current macOS.
enum SystemNowPlaying {
    private struct Payload: Decodable {
        let error: String?
        let title: String?
        let artist: String?
        let album: String?
        let bundleIdentifier: String?
        let displayName: String?
        let position: Double?
        let duration: Double?
        let playbackRate: Double?
        let isPlaying: Bool?
        let processIdentifier: Int32?
    }

    static func read(timeout: TimeInterval = 2.0) throws -> SystemNowPlayingSnapshot {
        let process = Process()
        let stdout = Pipe()
        let stderr = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = ["-l", "JavaScript", "-e", script]
        process.standardOutput = stdout
        process.standardError = stderr

        do {
            try process.run()
        } catch {
            throw SystemNowPlayingError.unavailable(error.localizedDescription)
        }

        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.01)
        }
        if process.isRunning {
            process.terminate()
            throw SystemNowPlayingError.timedOut
        }

        let output = stdout.fileHandleForReading.readDataToEndOfFile()
        let errorData = stderr.fileHandleForReading.readDataToEndOfFile()
        guard process.terminationStatus == 0 else {
            let detail = String(data: errorData, encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines)
            throw SystemNowPlayingError.unavailable(detail?.isEmpty == false ? detail! : "系统媒体中心没有响应")
        }

        guard let payload = try? JSONDecoder().decode(Payload.self, from: output) else {
            throw SystemNowPlayingError.invalidResponse
        }
        if let detail = payload.error, !detail.isEmpty {
            throw SystemNowPlayingError.unavailable(detail)
        }

        let title = payload.title?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !title.isEmpty else {
            throw SystemNowPlayingError.unavailable("当前没有播放器向系统发布歌曲信息")
        }

        var bundleIdentifier = payload.bundleIdentifier ?? ""
        var displayName = payload.displayName ?? ""
        if let pid = payload.processIdentifier,
           let application = NSRunningApplication(processIdentifier: pid) {
            if bundleIdentifier.isEmpty { bundleIdentifier = application.bundleIdentifier ?? "" }
            if displayName.isEmpty { displayName = application.localizedName ?? "" }
        }

        return SystemNowPlayingSnapshot(
            title: title,
            artist: payload.artist ?? "",
            album: payload.album ?? "",
            bundleIdentifier: bundleIdentifier,
            displayName: displayName,
            position: max(0, payload.position ?? 0),
            duration: max(0, payload.duration ?? 0),
            playbackRate: payload.playbackRate,
            isPlaying: payload.isPlaying
        )
    }

    /// Requests a timeline seek through the same Apple-signed host used for reads.
    /// Some players don't implement seeking; callers should retain a media-key fallback.
    static func seekToBeginning(timeout: TimeInterval = 2.0) -> Bool {
        let seekScript = #"""
        ObjC.import('Foundation');
        try {
            const framework = $.NSBundle.bundleWithPath('/System/Library/PrivateFrameworks/MediaRemote.framework/');
            framework.load;
            ObjC.bindFunction('MRMediaRemoteSetElapsedTime', ['void', ['double']]);
            $.MRMediaRemoteSetElapsedTime(0.0);
            'OK';
        } catch (error) {
            'ERROR:' + String(error);
        }
        """#

        let process = Process()
        let stdout = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = ["-l", "JavaScript", "-e", seekScript]
        process.standardOutput = stdout
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return false
        }

        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.01)
        }
        if process.isRunning {
            process.terminate()
            return false
        }
        let output = stdout.fileHandleForReading.readDataToEndOfFile()
        let text = String(data: output, encoding: .utf8) ?? ""
        return process.terminationStatus == 0 && text.contains("OK")
    }

    private static let script = #"""
    ObjC.import('Foundation');

    function value(info, key) {
        try {
            const result = info.valueForKey(key);
            return result ? ObjC.unwrap(result) : null;
        } catch (_) {
            return null;
        }
    }

    function property(object, key) {
        try {
            if (!object) return null;
            const result = object[key];
            if (result === null || result === undefined) return null;
            return ObjC.unwrap(result);
        } catch (_) {
            return null;
        }
    }

    function numberValue(info, key) {
        const result = value(info, key);
        if (result === null || result === undefined) return null;
        const number = Number(result);
        return Number.isFinite(number) ? number : null;
    }

    function run() {
        try {
            const framework = $.NSBundle.bundleWithPath('/System/Library/PrivateFrameworks/MediaRemote.framework/');
            framework.load;
            const Request = $.NSClassFromString('MRNowPlayingRequest');
            if (!Request) return JSON.stringify({error: '系统 MediaRemote 不可用'});

            const item = Request.localNowPlayingItem;
            const info = item ? item.nowPlayingInfo : null;
            if (!info) return JSON.stringify({error: '当前没有正在播放的媒体'});

            const path = Request.localNowPlayingPlayerPath;
            const client = path ? path.client : null;
            let positionValue = property(item, 'calculatedPlaybackPosition');
            let calculatedPosition = Number(positionValue);
            let position = Number.isFinite(calculatedPosition)
                ? calculatedPosition
                : numberValue(info, 'kMRMediaRemoteNowPlayingInfoElapsedTime');
            const rate = numberValue(info, 'kMRMediaRemoteNowPlayingInfoPlaybackRate');

            return JSON.stringify({
                error: null,
                title: value(info, 'kMRMediaRemoteNowPlayingInfoTitle'),
                artist: value(info, 'kMRMediaRemoteNowPlayingInfoArtist'),
                album: value(info, 'kMRMediaRemoteNowPlayingInfoAlbum'),
                position: position,
                duration: numberValue(info, 'kMRMediaRemoteNowPlayingInfoDuration'),
                playbackRate: rate,
                isPlaying: property(Request, 'localIsPlaying'),
                bundleIdentifier: property(client, 'bundleIdentifier'),
                displayName: property(client, 'displayName'),
                processIdentifier: property(client, 'processIdentifier')
            });
        } catch (error) {
            return JSON.stringify({error: String(error)});
        }
    }
    """#
}

enum MediaKey {
    static let playPause = 16
    static let next = 17
    static let previous = 18

    @discardableResult
    static func send(_ keyCode: Int) -> Bool {
        if !CGPreflightPostEventAccess(), !CGRequestPostEventAccess() {
            return false
        }
        post(keyCode, keyState: 0xA)
        post(keyCode, keyState: 0xB)
        return true
    }

    private static func post(_ keyCode: Int, keyState: Int) {
        let data = (keyCode << 16) | (keyState << 8)
        let event = NSEvent.otherEvent(
            with: .systemDefined,
            location: .zero,
            modifierFlags: NSEvent.ModifierFlags(rawValue: UInt(keyState << 8)),
            timestamp: ProcessInfo.processInfo.systemUptime,
            windowNumber: 0,
            context: nil,
            subtype: 8,
            data1: data,
            data2: -1
        )
        event?.cgEvent?.post(tap: .cghidEventTap)
    }
}
