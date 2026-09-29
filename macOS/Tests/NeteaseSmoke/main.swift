import Foundation

let fixture = #"""
[
  {
    "track": {
      "name": "测试歌曲",
      "duration": 186346,
      "artists": [{"name": "歌手甲"}, {"name": "歌手乙"}],
      "album": {"name": "测试专辑"}
    },
    "playedTime": 93.5
  }
]
"""#

let track = try NeteaseHistoryReader.decode(jsonData: Data(fixture.utf8))
precondition(track.title == "测试歌曲")
precondition(track.artist == "歌手甲 / 歌手乙")
precondition(track.album == "测试专辑")
precondition(abs(track.position - 93.5) < 0.001)
precondition(abs(track.duration - 186.346) < 0.001)
print("NetEase history smoke tests passed")

if CommandLine.arguments.contains("--live") {
    let liveTrack = try NeteaseHistoryReader.read()
    precondition(!liveTrack.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
    print("NetEase live history test passed")
}
