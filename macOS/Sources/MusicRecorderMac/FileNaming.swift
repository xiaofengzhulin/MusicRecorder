import Foundation

enum FileNaming {
    private static let invalidCharacters = CharacterSet(charactersIn: "/:\\?%*|\"<>\n\r\t")

    static func safeComponent(_ value: String, fallback: String = "录音") -> String {
        let normalized = value
            .components(separatedBy: invalidCharacters)
            .joined(separator: "_")
            .trimmingCharacters(in: .whitespacesAndNewlines)

        let collapsed = normalized.replacingOccurrences(
            of: #"\s+"#,
            with: " ",
            options: .regularExpression
        )

        if collapsed.isEmpty || collapsed == "." || collapsed == ".." {
            return fallback
        }
        return String(collapsed.prefix(180))
    }

    static func baseName(title: String, artist: String, now: Date = Date()) -> String {
        let fallback = "录音_" + timestampFormatter.string(from: now)
        let safeTitle = safeComponent(title, fallback: fallback)
        let safeArtist = safeComponent(artist, fallback: "")
        return safeArtist.isEmpty ? safeTitle : "\(safeTitle)-\(safeArtist)"
    }

    static func availableURL(
        folder: URL,
        title: String,
        artist: String,
        extension fileExtension: String = "m4a",
        fileManager: FileManager = .default
    ) -> URL {
        let base = baseName(title: title, artist: artist)
        var candidate = folder.appendingPathComponent(base).appendingPathExtension(fileExtension)
        var suffix = 2

        while fileManager.fileExists(atPath: candidate.path) {
            candidate = folder
                .appendingPathComponent("\(base) (\(suffix))")
                .appendingPathExtension(fileExtension)
            suffix += 1
        }
        return candidate
    }

    private static let timestampFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd_HHmmss"
        return formatter
    }()
}
