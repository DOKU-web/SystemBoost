// Port of native/framegen/src/FrameMeter.h. Keep both in sync.
final class FrameMeter {
    static let rateSec = 1.0
    static let settleSec = 0.5

    struct Sample {
        var seconds = 0.0
        var src: UInt64 = 0
        var out: UInt64 = 0
        var repeated: UInt64 = 0
        var srcFps: Double { seconds > 0 ? Double(src) / seconds : 0 }
        var outFps: Double { seconds > 0 ? Double(out) / seconds : 0 }
        var multiplier: Double { src > 0 ? Double(out) / Double(src) : 0 }
    }

    private static let windowCount = 11
    private var start = 0.0
    private var settleUntil = 0.0
    private var cur = Sample()
    private var windows = [Sample]()
    private(set) var totalSrc: UInt64 = 0
    private(set) var totalOut: UInt64 = 0
    private(set) var totalRepeat: UInt64 = 0
    private(set) var activeSec = 0.0

    func reset(_ now: Double) {
        start = now
        settleUntil = now + Self.settleSec
        cur = Sample()
        windows.removeAll()
    }

    func pause(_ now: Double) {
        activeSec += now - start
        start = now
    }

    func onSource() { cur.src += 1; totalSrc += 1 }

    func onOutput(repeated: Bool) {
        if repeated { cur.repeated += 1; totalRepeat += 1 } else { cur.out += 1; totalOut += 1 }
    }

    func elapsed(_ now: Double) -> Double { now - start }

    /// Ends the window at `now`. Non-nil once the result covers at least rateSec.
    func take(_ now: Double) -> Sample? {
        cur.seconds = now - start
        activeSec += cur.seconds
        let settled = start >= settleUntil
        start = now
        let done = cur
        cur = Sample()
        if !settled { return nil }
        windows.append(done)
        if windows.count > Self.windowCount { windows.removeFirst() }
        var rate = Sample()
        for w in windows.reversed() where rate.seconds < Self.rateSec {
            rate.seconds += w.seconds
            rate.src += w.src
            rate.out += w.out
            rate.repeated += w.repeated
        }
        return rate.seconds >= Self.rateSec ? rate : nil
    }
}
