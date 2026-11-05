// Port of native/framegen/src/FrameMeter.h. Keep both in sync.
final class FrameMeter {
    struct Sample {
        var seconds = 0.0
        var src: UInt64 = 0
        var out: UInt64 = 0
        var repeated: UInt64 = 0
        var srcFps: Double { seconds > 0 ? Double(src) / seconds : 0 }
        var outFps: Double { seconds > 0 ? Double(out) / seconds : 0 }
        var multiplier: Double { src > 0 ? Double(out) / Double(src) : 0 }
    }

    private static let windowCount = 4
    private var start = 0.0
    private var cur = Sample()
    private var windows = [Sample]()
    private var next = 0
    private(set) var totalSrc: UInt64 = 0
    private(set) var totalOut: UInt64 = 0
    private(set) var totalRepeat: UInt64 = 0

    func reset(_ now: Double) {
        start = now
        cur = Sample()
        windows.removeAll()
        next = 0
    }

    func onSource() { cur.src += 1; totalSrc += 1 }

    func onOutput(repeated: Bool) {
        if repeated { cur.repeated += 1; totalRepeat += 1 } else { cur.out += 1; totalOut += 1 }
    }

    func elapsed(_ now: Double) -> Double { now - start }

    func take(_ now: Double) -> Sample {
        cur.seconds = now - start
        if windows.count < Self.windowCount { windows.append(cur) } else { windows[next] = cur }
        next = (next + 1) % Self.windowCount
        start = now
        cur = Sample()
        var sum = Sample()
        for w in windows {
            sum.seconds += w.seconds
            sum.src += w.src
            sum.out += w.out
            sum.repeated += w.repeated
        }
        return sum
    }
}
