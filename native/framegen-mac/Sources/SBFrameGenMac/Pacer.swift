// Port of native/framegen/src/Pacer.h (display-clock pacing). Keep both in sync;
// the C++ version is the one covered by tests/pacer_sim.cpp.
import Foundation

enum MulMode: Int { case auto = 0, off = 1, x2 = 2, x3 = 3, x4 = 4 }

struct PacerConfig {
    var mode: MulMode = .auto
    var refreshHz: Double = 60
    var maxMul: Int = 4
    var vblankPhase: Double = 0
    var switchHoldSec: Double = 1.0
    var marginSec: Double = 0.0005
}

struct OutputSlot { var presentAt: Double; var displayAt: Double }

final class Pacer {
    private(set) var cfg: PacerConfig
    private var hist = [Double](repeating: 0, count: 30)
    private var pos = 0, count = 0
    private var lastArrival = -1.0
    private var tA = -1.0, tB = -1.0
    private var proc = 0.002
    private var D = -1.0
    private var nextDisplay = -1.0
    private(set) var outputCap = 0.0  // fps, 0 = none (slowDown)
    private(set) var divider = 1
    private var candidate = 0
    private var candidateSince = 0.0
    private var serial: UInt64 = 0
    private var lastPos = -1.0
    private(set) var late: UInt64 = 0
    private(set) var skipped: UInt64 = 0
    // A slower divider cuts the output rate in half, so a game hovering at the limit would
    // flip between 120 and 60 fps. It stays until the source is clearly below the limit.
    private static let dividerSlack = 1.25
    private static let minGain = 1.25  // less output over the source than this adds little

    init(_ c: PacerConfig) { cfg = c }

    func setConfig(_ c: PacerConfig) { cfg = c; outputCap = 0; reset() }
    func setDisplay(hz: Double, phase: Double) { cfg.refreshHz = hz; cfg.vblankPhase = phase }

    func reset() {
        pos = 0; count = 0; lastArrival = -1; tA = -1; tB = -1; nextDisplay = -1; divider = 1; candidate = 0; D = -1
        serial = 0; lastPos = -1
    }

    var active: Bool { tA > 0 }
    var wantsSource: Bool { !active || nextDisplay - D >= tB }
    var vsync: Double { 1.0 / max(1.0, cfg.refreshHz) }

    /// A frame reached the capture, used or not; `frames` = the game's frames it stands for.
    /// The rate comes from here: counting only the frames that get used would read a GPU
    /// that cannot keep up as a slow game, and the output would slow down to match it.
    func onArrival(_ time: Double, frames: Int = 1) {
        if lastArrival > 0 { pushInterval((time - lastArrival) / Double(frames)) }
        lastArrival = time
    }

    func onSource(_ now: Double, procSec: Double) {
        tA = tB; tB = now
        serial += 1
        if procSec > 0 { proc = proc * 0.8 + procSec * 0.2 }
        let target = percentile(0.8) + proc + cfg.marginSec
        D = D < 0 ? target : D + (target - D) * 0.1
        updateDivider(now)
        if nextDisplay < 0 { nextDisplay = snapUp(now + proc + vsync) }
    }

    func next() -> OutputSlot { OutputSlot(presentAt: nextDisplay - proc - 0.0005, displayAt: nextDisplay) }

    func t(for displayAt: Double) -> Float {
        guard active else { return 1 }
        let c = displayAt - D
        let I = tB - tA
        var t = I > 0 ? (c - tA) / I : 1
        if t > 1 { late += 1; t = 1 }
        if t < 0 { t = 0 }
        return Float(t)
    }

    func repeats(_ t: Float) -> Bool {
        let p = Double(serial) + Double(t)
        let same = lastPos >= 0 && abs(p - lastPos) < 1e-3
        lastPos = p
        return same
    }

    /// The GPU cannot hold this output rate: lower it by about a quarter. Auto lands on the next
    /// vblank divider. False when the output is already close to the source rate.
    func slowDown() -> Bool {
        let out = outputFps
        if out <= sourceFps * Pacer.minGain { return false }
        outputCap = out * 0.75
        return true
    }

    func advance(_ now: Double) {
        nextDisplay += outputStep
        if nextDisplay < now + proc {
            skipped += 1
            nextDisplay = snapUp(now + proc + 0.0005)
        }
        if cfg.mode == .auto { nextDisplay = snapNear(nextDisplay) }
    }

    var interval: Double {
        if count == 0 { return 1.0 / 30.0 }
        let limit = percentile(0.5) * 4
        let kept = hist[0..<count].filter { $0 <= limit }
        return kept.reduce(0, +) / Double(kept.count)
    }
    func percentile(_ q: Double) -> Double {
        if count == 0 { return 1.0 / 30.0 }
        let s = hist[0..<count].sorted()
        let k = min(max(Int(q * Double(count - 1) + 0.5), 0), count - 1)
        return s[k]
    }
    var sourceFps: Double { 1.0 / interval }
    var outputFps: Double { 1.0 / outputStep }
    var delayMs: Double { D * 1000 }
    var addedLatencyMs: Double { (D - proc) * 1000 }

    private var outputStep: Double {
        switch cfg.mode {
        case .auto: return Double(divider) * vsync
        case .off: return interval
        default: return max(interval / Double(cfg.mode.rawValue), vsync, outputCap > 0 ? 1 / outputCap : 0)
        }
    }

    private func pushInterval(_ d: Double) {
        guard d > 0, d <= 0.5 else { return }
        hist[pos] = d
        pos = (pos + 1) % hist.count
        count = min(count + 1, hist.count)
    }

    private func snapUp(_ t: Double) -> Double {
        cfg.vblankPhase + ((t - cfg.vblankPhase) / vsync).rounded(.up) * vsync
    }
    private func snapNear(_ t: Double) -> Double {
        cfg.vblankPhase + ((t - cfg.vblankPhase) / vsync).rounded() * vsync
    }

    private func updateDivider(_ now: Double) {
        guard cfg.mode == .auto else { return }
        let maxOut = sourceFps * Double(max(1, cfg.maxMul))
        var want = max(1, Int((cfg.refreshHz / maxOut - 0.05).rounded(.up)))
        if want > divider && cfg.refreshHz / Double(divider) <= maxOut * Pacer.dividerSlack { want = divider }
        if outputCap > 0 { want = max(want, Int((cfg.refreshHz / outputCap - 0.05).rounded(.up))) }
        if want == divider { candidate = 0; return }
        if candidate != want { candidate = want; candidateSince = now; return }
        if now - candidateSince >= cfg.switchHoldSec { divider = want; candidate = 0 }
    }
}
