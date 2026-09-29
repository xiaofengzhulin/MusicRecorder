import XCTest
@testable import MusicRecorderMac

final class FileNamingTests: XCTestCase {
    func testInvalidPathCharactersAreReplaced() {
        XCTAssertEqual(FileNaming.safeComponent("A/B:C?D"), "A_B_C_D")
    }

    func testArtistIsIncludedWhenPresent() {
        XCTAssertEqual(FileNaming.baseName(title: "歌曲", artist: "歌手"), "歌曲-歌手")
        XCTAssertEqual(FileNaming.baseName(title: "歌曲", artist: ""), "歌曲")
    }

    func testDuplicateNameGetsNumericSuffix() throws {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }

        let first = folder.appendingPathComponent("歌曲-歌手.m4a")
        XCTAssertTrue(FileManager.default.createFile(atPath: first.path, contents: Data()))

        let result = FileNaming.availableURL(folder: folder, title: "歌曲", artist: "歌手")
        XCTAssertEqual(result.lastPathComponent, "歌曲-歌手 (2).m4a")
    }
}
