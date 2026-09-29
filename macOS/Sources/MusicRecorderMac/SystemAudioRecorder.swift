import AVFoundation
import CoreMedia
import Foundation
import ScreenCaptureKit

enum SystemAudioRecorderError: LocalizedError {
    case noDisplay
    case alreadyRecording
    case writerSetupFailed(String)
    case noAudioReceived
    case captureStopped(String)

    var errorDescription: String? {
        switch self {
        case .noDisplay:
            return "没有找到可用于系统音频捕获的显示器。"
        case .alreadyRecording:
            return "当前已经在录制。"
        case .writerSetupFailed(let detail):
            return "无法创建音频文件：\(detail)"
        case .noAudioReceived:
            return "没有收到系统音频。请确认已授予“屏幕与系统音频录制”权限，并且播放器正在发声。"
        case .captureStopped(let detail):
            return "系统音频捕获意外停止：\(detail)"
        }
    }
}

/// Apple Silicon/macOS implementation of the Windows WASAPI loopback recorder.
/// ScreenCaptureKit supplies system-audio sample buffers and AVAssetWriter encodes
/// them as AAC in an M4A container using only Apple frameworks.
final class SystemAudioRecorder: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private let sampleQueue = DispatchQueue(label: "MusicRecorder.system-audio")
    private let lock = NSLock()

    private var stream: SCStream?
    private var writer: AVAssetWriter?
    private var writerInput: AVAssetWriterInput?
    private var sessionStarted = false
    private var sampleCount = 0
    private var stopping = false
    private var terminalError: Error?
    private var outputURL: URL?

    var isRecording: Bool {
        lock.withLock { stream != nil && !stopping }
    }

    func start(outputURL: URL, bitrateKbps: Int, title: String, artist: String) async throws {
        guard !isRecording else { throw SystemAudioRecorderError.alreadyRecording }

        let folder = outputURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? FileManager.default.removeItem(at: outputURL)

        let content = try await SCShareableContent.excludingDesktopWindows(
            false,
            onScreenWindowsOnly: true
        )
        guard let display = content.displays.first else {
            throw SystemAudioRecorderError.noDisplay
        }

        let filter = SCContentFilter(display: display, excludingWindows: [])
        let configuration = SCStreamConfiguration()
        configuration.capturesAudio = true
        configuration.excludesCurrentProcessAudio = true
        configuration.sampleRate = 48_000
        configuration.channelCount = 2
        configuration.width = 2
        configuration.height = 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        configuration.queueDepth = 3
        configuration.showsCursor = false

        let assetWriter: AVAssetWriter
        do {
            assetWriter = try AVAssetWriter(outputURL: outputURL, fileType: .m4a)
        } catch {
            throw SystemAudioRecorderError.writerSetupFailed(error.localizedDescription)
        }

        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: 48_000,
            AVNumberOfChannelsKey: 2,
            AVEncoderBitRateKey: max(96, min(320, bitrateKbps)) * 1_000
        ]
        let input = AVAssetWriterInput(mediaType: .audio, outputSettings: settings)
        input.expectsMediaDataInRealTime = true
        guard assetWriter.canAdd(input) else {
            throw SystemAudioRecorderError.writerSetupFailed("当前系统不支持所选 AAC 编码参数。")
        }
        assetWriter.add(input)
        assetWriter.metadata = Self.metadata(title: title, artist: artist)

        guard assetWriter.startWriting() else {
            throw SystemAudioRecorderError.writerSetupFailed(
                assetWriter.error?.localizedDescription ?? "编码器启动失败"
            )
        }

        let captureStream = SCStream(filter: filter, configuration: configuration, delegate: self)
        try captureStream.addStreamOutput(self, type: .audio, sampleHandlerQueue: sampleQueue)

        lock.withLock {
            self.writer = assetWriter
            self.writerInput = input
            self.stream = captureStream
            self.outputURL = outputURL
            self.sessionStarted = false
            self.sampleCount = 0
            self.stopping = false
            self.terminalError = nil
        }

        do {
            try await captureStream.startCapture()
        } catch {
            await abandonCapture(deleteOutput: true)
            throw error
        }
    }

    func stop() async throws -> URL {
        let snapshot = lock.withLock { () -> (SCStream?, AVAssetWriter?, AVAssetWriterInput?, URL?, Int, Error?) in
            stopping = true
            return (stream, writer, writerInput, outputURL, sampleCount, terminalError)
        }

        if let captureStream = snapshot.0 {
            try? await captureStream.stopCapture()
            try? captureStream.removeStreamOutput(self, type: .audio)
        }

        // Wait for the serial sample queue so no append races markAsFinished().
        await withCheckedContinuation { continuation in
            sampleQueue.async { continuation.resume() }
        }

        let finalSampleCount = lock.withLock { sampleCount }
        guard finalSampleCount > 0 else {
            await abandonCapture(deleteOutput: true)
            throw snapshot.5 ?? SystemAudioRecorderError.noAudioReceived
        }

        snapshot.2?.markAsFinished()
        if let assetWriter = snapshot.1 {
            await withCheckedContinuation { continuation in
                assetWriter.finishWriting { continuation.resume() }
            }
            if assetWriter.status != .completed {
                let detail = assetWriter.error?.localizedDescription ?? "未知编码错误"
                await abandonCapture(deleteOutput: true)
                throw SystemAudioRecorderError.writerSetupFailed(detail)
            }
        }

        guard let result = snapshot.3 else {
            await abandonCapture(deleteOutput: true)
            throw SystemAudioRecorderError.writerSetupFailed("导出路径丢失")
        }
        await abandonCapture(deleteOutput: false)
        return result
    }

    private func abandonCapture(deleteOutput: Bool) async {
        let values = lock.withLock { () -> (SCStream?, AVAssetWriter?, URL?) in
            let values = (stream, writer, outputURL)
            stream = nil
            writer = nil
            writerInput = nil
            outputURL = nil
            sessionStarted = false
            sampleCount = 0
            stopping = false
            terminalError = nil
            return values
        }

        if let captureStream = values.0 {
            try? await captureStream.stopCapture()
        }
        if values.1?.status == .writing {
            values.1?.cancelWriting()
        }
        if deleteOutput, let url = values.2 {
            try? FileManager.default.removeItem(at: url)
        }
    }

    func stream(
        _ stream: SCStream,
        didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
        of outputType: SCStreamOutputType
    ) {
        guard outputType == .audio, sampleBuffer.isValid, sampleBuffer.dataReadiness == .ready else {
            return
        }

        lock.lock()
        defer { lock.unlock() }

        guard !stopping, let assetWriter = writer, let input = writerInput,
              assetWriter.status == .writing else { return }

        if !sessionStarted {
            assetWriter.startSession(atSourceTime: sampleBuffer.presentationTimeStamp)
            sessionStarted = true
        }
        guard input.isReadyForMoreMediaData else { return }

        if input.append(sampleBuffer) {
            sampleCount += 1
        } else if let error = assetWriter.error {
            terminalError = error
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        lock.withLock {
            if !stopping {
                terminalError = SystemAudioRecorderError.captureStopped(error.localizedDescription)
            }
        }
    }

    private static func metadata(title: String, artist: String) -> [AVMetadataItem] {
        var result: [AVMetadataItem] = []

        if !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let item = AVMutableMetadataItem()
            item.keySpace = .common
            item.key = AVMetadataKey.commonKeyTitle as NSString
            item.value = title as NSString
            result.append(item)
        }
        if !artist.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let item = AVMutableMetadataItem()
            item.keySpace = .common
            item.key = AVMetadataKey.commonKeyArtist as NSString
            item.value = artist as NSString
            result.append(item)
        }
        return result
    }
}

private extension NSLock {
    func withLock<T>(_ body: () throws -> T) rethrows -> T {
        lock()
        defer { unlock() }
        return try body()
    }
}
