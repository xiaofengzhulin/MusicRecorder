import Foundation

precondition(FileNaming.safeComponent("A/B:C?D") == "A_B_C_D")
precondition(FileNaming.baseName(title: "歌曲", artist: "歌手") == "歌曲-歌手")

let folder = FileManager.default.temporaryDirectory
    .appendingPathComponent(UUID().uuidString, isDirectory: true)
try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: folder) }

let first = folder.appendingPathComponent("歌曲-歌手.m4a")
precondition(FileManager.default.createFile(atPath: first.path, contents: Data()))
let duplicate = FileNaming.availableURL(folder: folder, title: "歌曲", artist: "歌手")
precondition(duplicate.lastPathComponent == "歌曲-歌手 (2).m4a")

print("FileNaming smoke tests passed")
