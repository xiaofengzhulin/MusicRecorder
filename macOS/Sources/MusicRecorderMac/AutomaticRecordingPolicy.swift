import Foundation

enum AutomaticRecordingMode: String, CaseIterable, Identifiable {
    case off
    case single
    case playlist

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .off: return "关闭（手动录制）"
        case .single: return "自动录制单首"
        case .playlist: return "自动录制整张歌单"
        }
    }
}

enum AutomaticRecordingPolicy {
    static func shouldStart(
        mode: AutomaticRecordingMode,
        armed: Bool,
        canTrigger: Bool,
        isPlaying: Bool,
        isNewTrack: Bool
    ) -> Bool {
        guard mode != .off, armed, canTrigger else { return false }
        return isPlaying || isNewTrack
    }

    static func shouldRollPlaylist(
        mode: AutomaticRecordingMode,
        baselineKey: String,
        currentKey: String
    ) -> Bool {
        mode == .playlist
            && !baselineKey.isEmpty
            && !currentKey.isEmpty
            && baselineKey != currentKey
    }
}
