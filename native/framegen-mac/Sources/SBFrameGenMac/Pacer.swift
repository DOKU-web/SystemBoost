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
    private var hist = [Double](repeating: 0, count: 15)
    private var pos = 0, count = 0
    private var tA = -1.0, tB = -1.0
    private var proc = 0.002
    private var D = -1.0
    private var nextDisplay = -1.0
    private(set) var divider = 1
    private var candidate = 0
    private var candidateSince = 0.0
    private(set) var late: UInt64 = 0
    private(set) var skipped: UInt64 = 0

    init(_ c: PacerConfig) { cfg = c }

    func setConfig(_ c: PacerConfig) { cfg = c; reset() }
    func setDisplay(hz: Double, phase: Double) { cfg.refreshHz = hz; cfg.vblankPhase = phase }

    func reset() {
        pos = 0; count = 0; tA = -1; tB = -1; nextDisplay = -1; divider = 1; candidate = 0; D = -1
    }

    var active: Bool { tA > 0 }
    var vsync: Double { 1.0 / max(1.0, cfg.refreshHz) }

    func onSource(_ now: Double, procSec: Double) {
        if tB > 0 { pushInterval(now - tB) }
        tA = tB; tB = now
        if procSec > 0 { proc = proc * 0.8 + procSec * 0.2 }
        let target = interval + proc + cfg.marginSec
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

    func advance(_ now: Double) {
        nextDisplay += outputStep
        if nextDisplay < now + proc {
            skipped += 1
            nextDisplay = snapUp(now + proc + 0.0005)
        }
        if cfg.mode == .auto { nextDisplay = snapNear(nextDisplay) }
    }

    var interval: Double { percentile(0.5) }
    func percentile(_ q: Double) -> Double {
        if count == 0 { return 1.0 / 30.0 }
        let s = hist[0..<count].sorted()
        let k = min(max(Int(q * Double(count - 1) + 0.5), 0), count - 1)
        return s[k]
    }
    var sourceFps: Double { 1.0 / interval }
    var delayMs: Double { D * 1000 }
    var addedLatencyMs: Double { (D - proc) * 1000 }

    private var outputStep: Double {
        switch cfg.mode {
        case .auto: return Double(divider) * vsync
        case .off: return interval
        default: return interval / Double(cfg.mode.rawValue)
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
        let want = max(1, Int((cfg.refreshHz / maxOut - 0.05).rounded(.up)))
        if want == divider { candidate = 0; return }
        if candidate != want { candidate = want; candidateSince = now; return }
        if now - candidateSince >= cfg.switchHoldSec { divider = want; candidate = 0 }
    }
}
