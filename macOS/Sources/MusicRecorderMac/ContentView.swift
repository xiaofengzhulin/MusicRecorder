import SwiftUI

struct ContentView: View {
    @StateObject private var model = RecorderViewModel()

    var body: some View {
        VStack(spacing: 0) {
            disclaimer
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    outputSection
                    playerSection
                    trackSection
                    optionsSection
                    statusSection
                    actionButton
                }
                .padding(22)
            }
        }
        .frame(minWidth: 760, minHeight: 640)
        .background(Color(nsColor: .windowBackgroundColor))
        .task {
            model.startMonitoring()
            model.refreshTrack(showErrors: false)
        }
    }

    private var disclaimer: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
            Text("本项目仅供学习与技术交流使用。录制内容仅限个人合法使用，请勿传播、售卖或用于任何商业用途。")
                .font(.callout)
            Spacer()
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
        .background(Color.orange.opacity(0.09))
    }

    private var outputSection: some View {
        GroupBox("① 导出路径") {
            HStack {
                TextField("导出目录", text: $model.outputFolder)
                    .textFieldStyle(.roundedBorder)
                    .disabled(model.isRecording)
                Button("浏览…") { model.chooseOutputFolder() }
                    .disabled(model.isRecording)
                Button("打开目录") { model.openOutputFolder() }
            }
            .padding(.top, 6)
        }
    }

    private var playerSection: some View {
        GroupBox("② 播放器与音质") {
            HStack(spacing: 18) {
                VStack(alignment: .leading, spacing: 5) {
                    Text("目标播放器").font(.caption).foregroundStyle(.secondary)
                    Picker("目标播放器", selection: $model.playerSelection) {
                        ForEach(PlayerSelection.allCases) { player in
                            Text(player.displayName).tag(player)
                        }
                    }
                    .labelsHidden()
                    .frame(width: 340)
                    .disabled(model.isRecording)
                }

                VStack(alignment: .leading, spacing: 5) {
                    Text("AAC 码率").font(.caption).foregroundStyle(.secondary)
                    Picker("AAC 码率", selection: $model.bitrate) {
                        ForEach([128, 192, 256, 320], id: \.self) { value in
                            Text("\(value) kbps").tag(value)
                        }
                    }
                    .labelsHidden()
                    .frame(width: 125)
                    .disabled(model.isRecording)
                }

                Spacer()
                Button {
                    model.refreshTrack()
                } label: {
                    Label("读取歌曲", systemImage: "arrow.clockwise")
                }
                .disabled(model.isRecording || model.playerSelection == .manual)
            }
            .padding(.top, 6)
        }
    }

    private var trackSection: some View {
        GroupBox("③ 歌曲信息（用于文件名和标签）") {
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 10) {
                GridRow {
                    Text("歌曲名")
                    TextField("未读取到时可手动填写", text: $model.title)
                        .textFieldStyle(.roundedBorder)
                }
                GridRow {
                    Text("歌手")
                    TextField("可选", text: $model.artist)
                        .textFieldStyle(.roundedBorder)
                }
                GridRow {
                    Text("专辑")
                    TextField("可选", text: $model.album)
                        .textFieldStyle(.roundedBorder)
                }
            }
            .disabled(model.isRecording)
            .padding(.top, 6)
        }
    }

    private var optionsSection: some View {
        GroupBox("④ 录制选项") {
            VStack(alignment: .leading, spacing: 9) {
                HStack {
                    Text("自动录制模式")
                    Picker("自动录制模式", selection: $model.automaticMode) {
                        ForEach(AutomaticRecordingMode.allCases) { mode in
                            Text(mode.displayName).tag(mode)
                        }
                    }
                    .labelsHidden()
                    .frame(width: 230)
                }
                .disabled(model.isRecording)
                Text(model.automaticMode == .playlist
                    ? "检测到换歌后自动保存上一首，并为下一首创建独立文件。"
                    : (model.automaticMode == .single
                        ? "播放器开始播放时自动录制一次，结束后等待下一次播放。"
                        : "关闭时使用下方按钮手动开始和停止录制。"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Toggle("录制前自动回到歌曲开头", isOn: $model.restartFromBeginning)
                    .disabled(model.playerSelection == .manual || model.isRecording)
                Toggle("录制结束后自动暂停播放器", isOn: $model.pauseWhenDone)
                    .disabled(model.playerSelection == .manual || model.isRecording || model.automaticMode == .playlist)
                Toggle("完成后在 Finder 中显示文件", isOn: $model.openFolderWhenDone)
                    .disabled(model.isRecording)
            }
            .padding(.top, 6)
        }
    }

    private var statusSection: some View {
        HStack(spacing: 12) {
            Circle()
                .fill(model.isRecording ? Color.red : Color.secondary)
                .frame(width: 10, height: 10)
            Text(model.status)
                .lineLimit(2)
                .frame(maxWidth: .infinity, alignment: .leading)
            if model.isRecording {
                Text(model.elapsedText)
                    .font(.system(.title3, design: .monospaced).weight(.semibold))
            }
        }
        .padding(14)
        .background(Color(nsColor: .controlBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }

    private var actionButton: some View {
        Button {
            Task {
                if model.isRecording {
                    await model.stopRecording()
                } else {
                    await model.startRecording()
                }
            }
        } label: {
            HStack {
                Image(systemName: model.isRecording ? "stop.fill" : "record.circle")
                Text(model.isRecording ? "停止并保存" : "开始录制")
                    .fontWeight(.semibold)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 10)
        }
        .buttonStyle(.borderedProminent)
        .tint(model.isRecording ? .red : .accentColor)
        .controlSize(.large)
        .disabled(model.isBusy || (!model.isRecording && !model.canStart))
        .keyboardShortcut(.space, modifiers: [.command])
    }
}
