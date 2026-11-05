// ScreenCaptureKit capture of one window. Needs the Screen Recording permission
// (System Settings > Privacy & Security > Screen Recording). No injection.
import Foundation
import ScreenCaptureKit
import CoreMedia
import CoreVideo
import Metal

final class WindowCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    struct Frame { let texture: MTLTexture; let time: Double; let keep: CVMetalTexture }

    private let device: MTLDevice
    private var cache: CVMetalTextureCache?
    private var stream: SCStream?
    private let queue = DispatchQueue(label: "sbfg.capture", qos: .userInteractive)
    private let lock = NSLock()
    private var latest: Frame?
    private(set) var stopped = false
    let window: SCWindow

    init(device: MTLDevice, window: SCWindow, pixelWidth: Int, pixelHeight: Int, showsCursor: Bool) throws {
        self.device = device
        self.window = window
        super.init()
        CVMetalTextureCacheCreate(kCFAllocatorDefault, nil, device, nil, &cache)
        let cfg = SCStreamConfiguration()
        cfg.width = pixelWidth
        cfg.height = pixelHeight
        cfg.pixelFormat = kCVPixelFormatType_32BGRA
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: 240)  // deliver every game frame (up to 240)
        cfg.queueDepth = 3
        cfg.showsCursor = showsCursor
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        stream = s
        let sem = DispatchSemaphore(value: 0)
        var startError: Error?
        s.startCapture { err in startError = err; sem.signal() }
        sem.wait()
        if let e = startError { throw e }
    }

    func stop() {
        guard let s = stream else { return }
        stream = nil
        let sem = DispatchSemaphore(value: 0)
        s.stopCapture { _ in sem.signal() }
        _ = sem.wait(timeout: .now() + 1)
    }

    /// Newest frame since the last call (older frames are dropped: they only add latency).
    func take() -> Frame? {
        lock.lock(); defer { lock.unlock() }
        let f = latest
        latest = nil
        return f
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sb.isValid, let pb = CMSampleBufferGetImageBuffer(sb), let cache = cache else { return }
        if let att = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
           let raw = att.first?[.status] as? Int, let st = SCFrameStatus(rawValue: raw), st != .complete {
            return  // idle / blank frames carry no new image
        }
        let w = CVPixelBufferGetWidth(pb), h = CVPixelBufferGetHeight(pb)
        var cvTex: CVMetalTexture?
        guard CVMetalTextureCacheCreateTextureFromImage(kCFAllocatorDefault, cache, pb, nil, .bgra8Unorm, w, h, 0, &cvTex) == kCVReturnSuccess,
              let ct = cvTex, let tex = CVMetalTextureGetTexture(ct) else { return }
        // presentation time is on the host clock, same as CACurrentMediaTime()
        let t = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sb))
        lock.lock()
        latest = Frame(texture: tex, time: t, keep: ct)
        lock.unlock()
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        stopped = true
        Out.msg("warning", "capture stopped: \(error.localizedDescription)")
    }
}

enum Shareable {
    /// On-screen windows (blocking wrapper around the async API).
    static func windows() throws -> [SCWindow] {
        let sem = DispatchSemaphore(value: 0)
        var result: [SCWindow] = []
        var failure: Error?
        SCShareableContent.getExcludingDesktopWindows(true, onScreenWindowsOnly: true) { content, error in
            result = content?.windows ?? []
            failure = error
            sem.signal()
        }
        sem.wait()
        if let e = failure { throw e }
        return result
    }
}
