import Foundation

struct NeteaseHistoryTrack: Equatable {
    let title: String
    let artist: String
    let album: String
    let position: TimeInterval
    let duration: TimeInterval
    let updatedAt: Date?

    init(
        title: String,
        artist: String,
        album: String,
        position: TimeInterval,
        duration: TimeInterval,
        updatedAt: Date? = nil
    ) {
        self.title = title
        self.artist = artist
        self.album = album
        self.position = position
        self.duration = duration
        self.updatedAt = updatedAt
    }
}

enum NeteaseHistoryError: LocalizedError {
    case fileMissing
    case stale
    case invalidArchive
    case emptyHistory

    var errorDescription: String? {
        switch self {
        case .fileMissing:
            return "没有找到网易云音乐的本地播放记录。"
        case .stale:
            return "网易云音乐的本地播放记录没有更新，请先切换或重新播放一次歌曲。"
        case .invalidArchive:
            return "网易云音乐的本地播放记录格式无法解析。"
        case .emptyHistory:
            return "网易云音乐的本地播放记录为空。"
        }
    }
}

/// Compatibility fallback for older NetEase Music builds that don't reliably
/// publish MediaRemote metadata. The file contains a keyed archive whose first
/// real object is NetEase's own JSON playback-history array.
enum NeteaseHistoryReader {
    private struct Entry: Decodable {
        let track: Track
        let playedTime: Double?
    }

    private struct Track: Decodable {
        let name: String
        let duration: Double?
        let artists: [Artist]?
        let album: Album?
    }

    private struct Artist: Decodable {
        let name: String
    }

    private struct Album: Decodable {
        let name: String
    }

    static var defaultURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Containers/com.netease.163music/Data/Documents/storage/file_storage/webdata/file/history")
    }

    static func read(url: URL = defaultURL, maximumAge: TimeInterval = 30 * 60) throws -> NeteaseHistoryTrack {
        guard FileManager.default.fileExists(atPath: url.path) else {
            throw NeteaseHistoryError.fileMissing
        }

        let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
        let modified = attributes?[.modificationDate] as? Date
        if let modified, Date().timeIntervalSince(modified) > maximumAge {
            throw NeteaseHistoryError.stale
        }

        let archive = try Data(contentsOf: url, options: .mappedIfSafe)
        guard let plist = try PropertyListSerialization.propertyList(from: archive, options: [], format: nil) as? [String: Any],
              let objects = plist["$objects"] as? [Any],
              objects.count > 1,
              let json = objects[1] as? String else {
            throw NeteaseHistoryError.invalidArchive
        }
        let decoded = try decode(jsonData: Data(json.utf8))
        return NeteaseHistoryTrack(
            title: decoded.title,
            artist: decoded.artist,
            album: decoded.album,
            position: decoded.position,
            duration: decoded.duration,
            updatedAt: modified
        )
    }

    static func decode(jsonData: Data) throws -> NeteaseHistoryTrack {
        let entries: [Entry]
        do {
            entries = try JSONDecoder().decode([Entry].self, from: jsonData)
        } catch {
            throw NeteaseHistoryError.invalidArchive
        }
        guard let entry = entries.first else {
            throw NeteaseHistoryError.emptyHistory
        }

        return NeteaseHistoryTrack(
            title: entry.track.name,
            artist: entry.track.artists?.map(\.name).joined(separator: " / ") ?? "",
            album: entry.track.album?.name ?? "",
            position: max(0, entry.playedTime ?? 0),
            duration: max(0, (entry.track.duration ?? 0) / 1000)
        )
    }
}
