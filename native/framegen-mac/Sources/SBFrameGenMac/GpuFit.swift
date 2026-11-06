// Port of native/framegen/src/GpuFit.h (notices when the GPU cannot keep up). Keep both in sync;
// the C++ version is the one covered by tests/fit_test.cpp.
import Foundation

final class GpuFit {
    private static let missPerSec = 2.0  // at 500 ms stats lines: one miss in every line
    private static let holdSec = 3.0
    private static let settleSec = 3.0   // let the last step show its effect
    private static let heavyPerSec = 15.0  // one output slot in ten at 144 Hz
    private static let holdHeavySec = 1.5
    private static let settleHeavySec = 1.0
    private static let behindSec = 2.5

    private var lastNow = -1.0
    private var lastSkipped: UInt64 = 0
    private var overSince = -1.0
    private var settleUntil = 0.0
    private var behindSince = -1.0

    /// Call at every stats line. True = slots missed for holdSec: time to step down.
    func update(_ now: Double, skipped: UInt64) -> Bool {
        let sec = lastNow < 0 ? 0 : now - lastNow
        let missed = skipped - lastSkipped
        lastNow = now
        lastSkipped = skipped
        if sec <= 0 { return false }
        if Double(missed) / sec < GpuFit.missPerSec || now < settleUntil { overSince = -1; return false }
        if overSince < 0 { overSince = now }
        // far behind: a card with little room should not sit through every step at full length
        let heavy = Double(missed) / sec >= GpuFit.heavyPerSec
        if now - overSince < (heavy ? GpuFit.holdHeavySec : GpuFit.holdSec) { return false }
        stepped(now, settle: heavy ? GpuFit.settleHeavySec : GpuFit.settleSec)
        return true
    }

    /// Call at every stats line. True = the output stayed under floorFps for behindSec. The overlay
    /// covers the game, so that many fewer frames are what gets seen: take the biggest step left at
    /// once, and stop generating only when none is left.
    func behind(_ now: Double, outFps: Double, floorFps: Double) -> Bool {
        if outFps >= floorFps || now < settleUntil { behindSince = -1; return false }
        if behindSince < 0 { behindSince = now }
        if now - behindSince < GpuFit.behindSec { return false }
        stepped(now, settle: GpuFit.settleHeavySec)
        return true
    }

    private func stepped(_ now: Double, settle: Double) {
        overSince = -1
        behindSince = -1
        settleUntil = now + settle
    }
}

/// How fast the game runs with nothing taken from it, read just before generation starts. A GPU with
/// no room gives up game frames to generation, and the source rate alone would hide that. Compared
/// for a short while only: later scenes run at other rates anyway.
final class GameAlone {
    private static let readSec = 1.0
    private static let compareSec = 15.0

    private var from = 0.0
    private var value = 0.0
    private var until = 0.0
    private var frames: UInt64 = 0
    private var isReading = false

    func start(_ now: Double, srcFrames: UInt64) {
        from = now
        frames = srcFrames
        isReading = true
        until = 0
    }

    /// True while the rate is being read: generation waits.
    func reading(_ now: Double, srcFrames: UInt64) -> Bool {
        if !isReading { return false }
        if now - from < GameAlone.readSec { return true }
        value = Double(srcFrames - frames) / (now - from)
        until = now + GameAlone.compareSec
        isReading = false
        return false
    }

    /// 0 = nothing to compare with
    func fps(_ now: Double) -> Double { now < until ? value : 0 }
}
