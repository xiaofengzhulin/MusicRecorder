import Foundation

precondition(!AutomaticRecordingPolicy.shouldStart(
    mode: .off, armed: true, canTrigger: true, isPlaying: true, isNewTrack: true
))
precondition(AutomaticRecordingPolicy.shouldStart(
    mode: .single, armed: true, canTrigger: true, isPlaying: true, isNewTrack: false
))
precondition(!AutomaticRecordingPolicy.shouldStart(
    mode: .single, armed: false, canTrigger: true, isPlaying: true, isNewTrack: true
))
precondition(AutomaticRecordingPolicy.shouldStart(
    mode: .playlist, armed: true, canTrigger: true, isPlaying: false, isNewTrack: true
))
precondition(!AutomaticRecordingPolicy.shouldStart(
    mode: .playlist, armed: true, canTrigger: false, isPlaying: false, isNewTrack: true
))
precondition(AutomaticRecordingPolicy.shouldRollPlaylist(
    mode: .playlist, baselineKey: "track-a", currentKey: "track-b"
))
precondition(!AutomaticRecordingPolicy.shouldRollPlaylist(
    mode: .single, baselineKey: "track-a", currentKey: "track-b"
))
precondition(!AutomaticRecordingPolicy.shouldRollPlaylist(
    mode: .playlist, baselineKey: "track-a", currentKey: "track-a"
))

print("Automatic recording policy smoke tests passed")
