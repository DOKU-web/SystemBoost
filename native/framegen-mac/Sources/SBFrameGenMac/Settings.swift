import Foundation

enum Quality: Int { case low, performance, balanced, quality }    // lowest first: steps down by one

struct Settings {
    var windowID: UInt32 = 0          // CGWindowID (the app passes it as --hwnd too)
    var mode: MulMode = .auto
    var maxMul = 4
    var targetFps = 0.0               // output rate the user chose, 0 = follow the monitor
    var quality: Quality = .balanced
    var upscale = false
    var sharpness: Float = 0.5
    var genGpu = "same"               // same | igpu | <index>
    var captureCursor = false
    var clipCursor = true
    var statsMs = 500
    var listGpus = false
    var listWindows = false

    var procFactor: Int { quality == .low ? 8 : quality == .performance ? 4 : 2 }
    var refineIters: Int { quality == .quality ? 2 : 1 }
    var coarseRadius: Int { quality == .low || quality == .performance ? 4 : 6 }

    static func parseMode(_ v: String) -> MulMode? {
        switch v {
        case "auto": return .auto
        case "x2": return .x2
        case "x3": return .x3
        case "x4": return .x4
        case "x6": return .x6
        case "x8": return .x8
        case "off": return .off
        default: return nil
        }
    }

    static func parseTarget(_ v: String) -> Double? {
        guard let f = Double(v), f == 0 || (10...1000).contains(f) else { return nil }
        return f
    }

    /// Same options as the Windows sb_framegen.exe (unknown Windows-only ones are ignored).
    static func parse(_ args: [String]) -> (Settings, String?) {
        var s = Settings()
        var i = 0
        func value() -> String? { i += 1; return i < args.count ? args[i] : nil }
        while i < args.count {
            let k = args[i]
            switch k {
            case "--list-gpus": s.listGpus = true
            case "--list-windows": s.listWindows = true
            case "--upscale": s.upscale = true
            case "--capture-cursor": s.captureCursor = true
            case "--no-clip-cursor": s.clipCursor = false
            case "--hwnd", "--window":
                guard let v = value(), let id = UInt32(v.hasPrefix("0x") ? String(v.dropFirst(2)) : v,
                                                       radix: v.hasPrefix("0x") ? 16 : 10) else {
                    return (s, "bad \(k)")
                }
                s.windowID = id
            case "--mode":
                guard let v = value(), let m = parseMode(v) else { return (s, "bad --mode") }
                s.mode = m
            case "--target-fps":
                guard let v = value(), let f = parseTarget(v) else { return (s, "--target-fps must be 0 or 10..1000") }
                s.targetFps = f
            case "--max-mul":
                guard let v = value(), let n = Int(v), (2...8).contains(n) else { return (s, "--max-mul must be 2..8") }
                s.maxMul = n
            case "--quality":
                switch value() {
                case "low": s.quality = .low
                case "performance": s.quality = .performance
                case "balanced": s.quality = .balanced
                case "quality": s.quality = .quality
                default: return (s, "bad --quality")
                }
            case "--sharpness":
                guard let v = value(), let f = Float(v), (0...1).contains(f) else { return (s, "--sharpness must be 0..1") }
                s.sharpness = f
            case "--gen-gpu":
                guard let v = value() else { return (s, "bad --gen-gpu") }
                s.genGpu = v
            case "--stats-ms":
                guard let v = value(), let n = Int(v) else { return (s, "bad --stats-ms") }
                s.statsMs = max(100, n)
            case "--capture", "--shader-dir":
                _ = value()  // Windows-only options
            default:
                return (s, "unknown option \(k)")
            }
            i += 1
        }
        if !s.listGpus && !s.listWindows && s.windowID == 0 { return (s, "--hwnd (window id) is required") }
        return (s, nil)
    }
}

// MARK: - JSON output (one object per line, same format as Windows)

enum Out {
    private static let lock = NSLock()
    static func emit(_ obj: [String: Any]) {
        guard let d = try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys]),
              let s = String(data: d, encoding: .utf8) else { return }
        lock.lock(); defer { lock.unlock() }
        FileHandle.standardOutput.write((s + "\n").data(using: .utf8)!)
    }
    static func msg(_ type: String, _ m: String) { emit(["type": type, "msg": m]) }
}
