// Port of native/framegen/src/GpuFit.h (notices when the GPU cannot keep up). Keep both in sync;
// the C++ version is the one covered by tests/fit_test.cpp.
import Foundation

final class GpuFit {
    private static let missPerSec = 2.0  // at 500 ms stats lines: one miss in every line
    private static let holdSec = 3.0
    private static let settleSec = 3.0   // let the last step show its effect
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
        if now - overSince < GpuFit.holdSec { return false }
        overSince = -1
        settleUntil = now + GpuFit.settleSec
        return true
    }

    /// Call at every stats line. True = the output stayed under floorFps for behindSec. The overlay
    /// covers the game, so that many fewer frames are what gets seen: no step down beats the game alone.
    func behind(_ now: Double, outFps: Double, floorFps: Double) -> Bool {
        if outFps >= floorFps { behindSince = -1; return false }
        if behindSince < 0 { behindSince = now }
        if now - behindSince < GpuFit.behindSec { return false }
        behindSince = -1
        return true
    }
}
