import SwiftUI

@main
struct MusicRecorderMacApp: App {
    var body: some Scene {
        WindowGroup("MusicRecorder") {
            ContentView()
        }
        .windowResizability(.contentMinSize)

        Settings {
            VStack(alignment: .leading, spacing: 10) {
                Text("MusicRecorder for Apple Silicon")
                    .font(.headline)
                Text("系统音频通过 ScreenCaptureKit 捕获，文件使用系统 AAC 编码器导出为 M4A。")
                    .foregroundStyle(.secondary)
            }
            .padding(24)
            .frame(width: 420)
        }
    }
}
