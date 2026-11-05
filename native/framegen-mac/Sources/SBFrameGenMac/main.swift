// SystemBoost FrameGen for macOS - entry point. Same CLI options and JSON
// protocol as native/framegen (Windows):
//   stdout: {"type":"ready"|"stats"|"warning"|"error"|"exit"|"gpus"|"windows", ...}
//   stdin : "mode auto|x2|x3|x4|off", "sharp 0..1", "enable", "disable", "quit"
// Runs next to the game; never touches the game process (screen capture only).
import AppKit
import CoreGraphics
import CoreVideo
import Metal
import QuartzCore
import ScreenCaptureKit

// MARK: helpers

func now() -> Double { CACurrentMediaTime() }

/// Live window bounds (points, CG global coordinates). nil if the window is gone.
func windowBounds(_ id: CGWindowID) -> CGRect? {
    guard let info = CGWindowListCopyWindowInfo(.optionIncludingWindow, id) as? [[String: Any]],
          let first = info.first, let b = first[kCGWindowBounds as String] as? [String: Any],
          let r = CGRect(dictionaryRepresentation: b as CFDictionary) else { return nil }
    return r
}

/// Screen containing most of `cg` (CG coordinates) and its frame in CG coordinates.
func screenFor(_ cg: CGRect) -> (NSScreen, CGRect)? {
    let primaryH = NSScreen.screens.first?.frame.height ?? 0
    var best: (NSScreen, CGRect, CGFloat)?
    for s in NSScreen.screens {
        let f = s.frame
        let sc = CGRect(x: f.minX, y: primaryH - f.maxY, width: f.width, height: f.height)
        let a = sc.intersection(cg)
        let area = a.isNull ? 0 : a.width * a.height
        if best == nil || area > best!.2 { best = (s, sc, area) }
    }
    return best.map { ($0.0, $0.1) }
}

/// Tracks the vblank phase of a display with CVDisplayLink.
final class VBlank {
    private var link: CVDisplayLink?
    private let lock = NSLock()
    private var last = 0.0
    var phase: Double { lock.lock(); defer { lock.unlock() }; return last }

    init(displayID: CGDirectDisplayID) {
        CVDisplayLinkCreateWithCGDisplay(displayID, &link)
        guard let link = link else { return }
        let freq = CVGetHostClockFrequency()
        CVDisplayLinkSetOutputHandler(link) { [weak self] _, inNow, _, _, _ in
            guard let self = self else { return kCVReturnSuccess }
            let t = Double(inNow.pointee.hostTime) / freq
            self.lock.lock(); self.last = t; self.lock.unlock()
            return kCVReturnSuccess
        }
        CVDisplayLinkStart(link)
    }
    deinit { if let l = link { CVDisplayLinkStop(l) } }
}

// MARK: stdin commands

final class Commands {
    private let lock = NSLock()
    private var _mode: MulMode?
    private var _sharp: Float?
    private var _enabled = true
    private var _quit = false
    func setMode(_ m: MulMode) { lock.lock(); _mode = m; lock.unlock() }
    func setSharp(_ s: Float) { lock.lock(); _sharp = s; lock.unlock() }
    func setEnabled(_ e: Bool) { lock.lock(); _enabled = e; lock.unlock() }
    func setQuit() { lock.lock(); _quit = true; lock.unlock() }
    func takeMode() -> MulMode? { lock.lock(); defer { _mode = nil; lock.unlock() }; return _mode }
    func takeSharp() -> Float? { lock.lock(); defer { _sharp = nil; lock.unlock() }; return _sharp }
    var enabled: Bool { lock.lock(); defer { lock.unlock() }; return _enabled }
    var quit: Bool { lock.lock(); defer { lock.unlock() }; return _quit }
}

let commands = Commands()

func startStdinThread() {
    Thread {
        while let line = readLine() {
            if line == "quit" { break }
            if line == "enable" { commands.setEnabled(true) }
            else if line == "disable" { commands.setEnabled(false) }
            else if line.hasPrefix("mode "), let m = Settings.parseMode(String(line.dropFirst(5))) { commands.setMode(m) }
            else if line.hasPrefix("sharp "), let f = Float(line.dropFirst(6)) { commands.setSharp(min(max(f, 0), 1)) }
            else if !line.isEmpty { Out.msg("warning", "unknown command: \(line)") }
        }
        commands.setQuit()  // stdin closed (app exited) -> stop
    }.start()
}

// MARK: listing

func pickDevice(_ s: Settings) -> (MTLDevice, String) {
    let all = MTLCopyAllDevices()
    if s.genGpu == "igpu", let d = all.first(where: { $0.isLowPower }) { return (d, "integrated GPU") }
    if let i = Int(s.genGpu), i >= 0, i < all.count { return (all[i], "user selected") }
    return (MTLCreateSystemDefaultDevice() ?? all[0], "display GPU")
}

func listGpus() {
    let list: [[String: Any]] = MTLCopyAllDevices().enumerated().map { i, d in
        ["index": i, "name": d.name, "integrated": d.isLowPower, "hasOutputs": !d.isHeadless,
         "dedicatedMB": d.hasUnifiedMemory ? 0 : Int(d.recommendedMaxWorkingSetSize / 1_048_576)]
    }
    Out.emit(["type": "gpus", "list": list])
}

func listWindows() throws {
    let wins = try Shareable.windows().filter { $0.windowLayer == 0 && $0.frame.width >= 200 && $0.frame.height >= 150 }
    let list: [[String: Any]] = wins.map {
        ["id": Int($0.windowID), "app": $0.owningApplication?.applicationName ?? "",
         "title": $0.title ?? "", "w": Int($0.frame.width), "h": Int($0.frame.height)]
    }
    Out.emit(["type": "windows", "list": list])
}

// MARK: engine

struct Layout: Equatable {
    var game = CGRect.zero      // game window, points (CG coords)
    var overlay = CGRect.zero   // overlay window, points
    var scale: CGFloat = 1
    var capW = 0, capH = 0, outW = 0, outH = 0, offX = 0, offY = 0
}

func computeLayout(_ id: CGWindowID, upscale: Bool) -> Layout? {
    guard let g = windowBounds(id), let sf = screenFor(g) else { return nil }
    let screen = sf.0, sframe = sf.1
    var L = Layout()
    L.game = g
    L.scale = screen.backingScaleFactor
    L.capW = max(1, Int(g.width * L.scale)); L.capH = max(1, Int(g.height * L.scale))
    if !upscale {
        L.overlay = g; L.outW = L.capW; L.outH = L.capH
        return L
    }
    L.overlay = sframe
    let mw = Int(sframe.width * L.scale), mh = Int(sframe.height * L.scale)
    let s = min(Double(mw) / Double(L.capW), Double(mh) / Double(L.capH))
    L.outW = min(mw, Int(Double(L.capW) * s + 0.5)); L.outH = min(mh, Int(Double(L.capH) * s + 0.5))
    L.offX = (mw - L.outW) / 2; L.offY = (mh - L.outH) / 2
    return L
}

final class Engine {
    let s: Settings
    let fg: FrameGenMetal
    let overlay: Overlay
    let capture: WindowCapture
    let pacer: Pacer
    let gamePid: pid_t
    var layout: Layout
    var vblank: VBlank?
    var refreshHz = 60.0
    var mode: MulMode

    init(settings: Settings, device: MTLDevice, window: SCWindow, layout: Layout, overlay: Overlay) throws {
        s = settings
        self.layout = layout
        self.overlay = overlay
        mode = settings.mode
        fg = try FrameGenMetal(device: device)
        fg.resize(capW: layout.capW, capH: layout.capH, outW: layout.outW, outH: layout.outH, settings: settings)
        capture = try WindowCapture(device: device, window: window, pixelWidth: layout.capW, pixelHeight: layout.capH,
                                    showsCursor: settings.captureCursor && !settings.upscale)
        gamePid = window.owningApplication?.processID ?? 0
        var pc = PacerConfig()
        pc.mode = settings.mode; pc.maxMul = settings.maxMul
        pacer = Pacer(pc)
        updateDisplay()
    }

    func updateDisplay() {
        guard let screen = screenFor(layout.game)?.0 else { return }
        refreshHz = Double(max(30, screen.maximumFramesPerSecond))
        if vblank == nil, let num = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber {
            vblank = VBlank(displayID: CGDirectDisplayID(num.uint32Value))
        }
        pacer.setDisplay(hz: refreshHz, phase: vblank?.phase ?? 0)
    }

    func setOverlay(_ show: Bool) {
        let rect = layout.overlay, scale = layout.scale
        DispatchQueue.main.async {
            if show { self.overlay.setRect(rect, scale: scale) }
            self.overlay.show(show)
        }
    }

    func present(_ t: Float) {
        guard let drawable = overlay.layer.nextDrawable(), let cb = fg.queue.makeCommandBuffer() else { return }
        guard let src = fg.generate(t, into: cb) else { return }
        if s.upscale {  // letterbox: clear to black
            let rp = MTLRenderPassDescriptor()
            rp.colorAttachments[0].texture = drawable.texture
            rp.colorAttachments[0].loadAction = .clear
            rp.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 1)
            rp.colorAttachments[0].storeAction = .store
            cb.makeRenderCommandEncoder(descriptor: rp)?.endEncoding()
        }
        if let blit = cb.makeBlitCommandEncoder() {
            let w = min(src.width, drawable.texture.width - layout.offX)
            let h = min(src.height, drawable.texture.height - layout.offY)
            if w > 0 && h > 0 {
                blit.copy(from: src, sourceSlice: 0, sourceLevel: 0, sourceOrigin: MTLOrigin(x: 0, y: 0, z: 0),
                          sourceSize: MTLSize(width: w, height: h, depth: 1), to: drawable.texture,
                          destinationSlice: 0, destinationLevel: 0,
                          destinationOrigin: MTLOrigin(x: layout.offX, y: layout.offY, z: 0))
            }
            blit.endEncoding()
        }
        cb.present(drawable)
        cb.commit()
    }

    /// Keep the real cursor inside the game window while upscaling.
    func confineCursor() {
        guard s.upscale, s.clipCursor else { return }
        let primaryH = NSScreen.screens.first?.frame.height ?? 0
        let m = NSEvent.mouseLocation
        let p = CGPoint(x: m.x, y: primaryH - m.y)
        let g = layout.game.insetBy(dx: 1, dy: 1)
        if !g.contains(p) {
            CGWarpMouseCursorPosition(CGPoint(x: min(max(p.x, g.minX), g.maxX), y: min(max(p.y, g.minY), g.maxY)))
            CGAssociateMouseAndMouseCursorPosition(1)
        }
    }

    func run() {
        var lastLayout = now(), lastDisplay = now()
        let meter = FrameMeter()
        meter.reset(now())
        var wasActive = false
        var held: WindowCapture.Frame?  // waits for the output clock (Pacer.wantsSource)
        Out.emit(["type": "ready", "capture": "screencapturekit", "genGpu": fg.device.name,
                  "refreshHz": refreshHz, "capture_px": [layout.capW, layout.capH],
                  "output_px": [layout.outW, layout.outH], "crossAdapterCopy": false])
        while !commands.quit {
            let t0 = now()
            guard windowBounds(capture.window.windowID) != nil, !capture.stopped else { break }
            if let m = commands.takeMode() {
                mode = m
                var pc = pacer.cfg; pc.mode = m; pc.refreshHz = refreshHz; pc.vblankPhase = vblank?.phase ?? 0
                pacer.setConfig(pc)
            }
            if let sh = commands.takeSharp() { fg.sharpness = sh }

            let front = NSWorkspace.shared.frontmostApplication?.processIdentifier
            let active = commands.enabled && (front == gamePid || front == getpid())
            if !active {
                if wasActive { meter.pause(t0); setOverlay(false); wasActive = false }
                _ = capture.take()
                held = nil
                Thread.sleep(forTimeInterval: 0.02)
                continue
            }
            if t0 - lastLayout > 0.25 {
                lastLayout = t0
                if let nl = computeLayout(capture.window.windowID, upscale: s.upscale), nl != layout {
                    let resized = nl.capW != layout.capW || nl.capH != layout.capH || nl.outW != layout.outW || nl.outH != layout.outH
                    layout = nl
                    if resized {
                        // capture keeps its configured size; frames are copied up to the new size
                        let sharp = fg.sharpness
                        fg.resize(capW: nl.capW, capH: nl.capH, outW: nl.outW, outH: nl.outH, settings: s)
                        fg.sharpness = sharp
                        pacer.reset()
                    }
                    setOverlay(true)
                }
            }
            if t0 - lastDisplay > 1 { lastDisplay = t0; updateDisplay() }
            if !wasActive {
                pacer.reset(); fg.resetHistory()
                meter.reset(t0)
                if mode != .off || s.upscale { setOverlay(true) }
                wasActive = true
            }
            confineCursor()

            if held == nil { held = capture.take() }
            if let f = held, mode == .off || pacer.wantsSource {
                held = nil
                fg.pushFrame(f.texture)
                meter.onSource()
                pacer.onSource(f.time > 0 ? f.time : t0, procSec: fg.genMs / 1000)
                if mode == .off {
                    if s.upscale && fg.hasPair { present(1); meter.onOutput(repeated: false) } else if !s.upscale { setOverlay(false) }
                }
            }
            if mode != .off && fg.hasPair && pacer.active {
                let slot = pacer.next()
                if now() >= slot.presentAt {
                    let t = pacer.t(for: slot.displayAt)
                    let repeated = pacer.repeats(t)
                    if !repeated { present(t) }
                    meter.onOutput(repeated: repeated)
                    pacer.advance(now())
                } else {
                    Thread.sleep(forTimeInterval: max(0, min(slot.presentAt - now(), 0.001)))
                }
            } else {
                Thread.sleep(forTimeInterval: 0.001)
            }

            let t2 = now()
            if meter.elapsed(t2) * 1000 >= Double(s.statsMs), let m = meter.take(t2) {
                Out.emit(["type": "stats", "srcFps": m.srcFps, "outFps": m.outFps, "multiplier": m.multiplier,
                          "mode": mode.rawValue, "refreshHz": refreshHz, "delayMs": pacer.delayMs,
                          "addedLatencyMs": pacer.addedLatencyMs, "genMs": fg.genMs, "analyzeMs": fg.analyzeMs,
                          "transferMs": 0, "late": pacer.late, "skipped": pacer.skipped,
                          "srcFrames": meter.totalSrc, "outFrames": meter.totalOut, "repeatFrames": meter.totalRepeat,
                          "measuredSec": m.seconds, "activeSec": meter.activeSec,
                          "divider": mode == .auto ? pacer.divider : 1, "intervalMs": pacer.interval * 1000])
            }
        }
        capture.stop()
        setOverlay(false)
        Out.emit(["type": "exit"])
        exit(0)
    }
}

// MARK: main

let (settings, argError) = Settings.parse(Array(CommandLine.arguments.dropFirst()))
if let e = argError { Out.msg("error", e); exit(1) }
if settings.listGpus { listGpus(); exit(0) }
if !CGPreflightScreenCaptureAccess() {
    _ = CGRequestScreenCaptureAccess()
    Out.msg("error", "screen recording permission required (System Settings > Privacy & Security > Screen Recording)")
    exit(5)
}
do {
    if settings.listWindows { try listWindows(); exit(0) }
    guard let window = try Shareable.windows().first(where: { $0.windowID == settings.windowID }) else {
        Out.msg("error", "game window not found"); exit(2)
    }
    guard let layout = computeLayout(window.windowID, upscale: settings.upscale) else {
        Out.msg("error", "game window not visible"); exit(2)
    }
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)   // no Dock icon, never steals focus
    let (device, why) = pickDevice(settings)
    let overlay = Overlay(device: device)
    let engine = try Engine(settings: settings, device: device, window: window, layout: layout, overlay: overlay)
    Out.emit(["type": "warning", "msg": "generation GPU: \(device.name) (\(why))"])
    startStdinThread()
    let t = Thread { engine.run() }
    t.qualityOfService = .userInteractive
    t.start()
    app.run()
} catch {
    Out.msg("error", "\(error)")
    exit(3)
}
