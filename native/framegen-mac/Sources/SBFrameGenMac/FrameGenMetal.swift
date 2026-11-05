// Metal port of native/framegen/src/FrameGenGpu.cpp. Same dispatch sequence and
// constants; shaders are generated from the same HLSL (GeneratedShaders.swift).
import Metal
import Foundation

/// FgConstants as 36 x 32-bit words (layout = shaders/common.hlsli, 144 bytes).
struct FgConstants {
    var w = [UInt32](repeating: 0, count: 36)
    mutating func u(_ i: Int, _ v: UInt32) { w[i] = v }
    mutating func i(_ i: Int, _ v: Int32) { w[i] = UInt32(bitPattern: v) }
    mutating func f(_ i: Int, _ v: Float) { w[i] = v.bitPattern }
    // word offsets
    static let capSize = 0, procSize = 2, lvlSize = 4, gridSize = 6, parentGrid = 8
    static let isCoarse = 10, isFinest = 11, radius = 12, refineIters = 13, lam = 14, zeroBias = 15
    static let hasTemporal = 16, temporalShift = 17, accumStats = 18, t = 19, fpIters = 20
    static let fbSigma = 21, fbSigmaK = 22, staticThr = 23, cutErr = 24, cutRatio = 25, outSize = 26
    static let sharpness = 28, cursorOn = 29, cursorPos = 30, cursorSize = 32
}

final class FrameGenMetal {
    let device: MTLDevice
    let queue: MTLCommandQueue
    private var pipes: [String: MTLComputePipelineState] = [:]
    private let sampler: MTLSamplerState
    private let stats: MTLBuffer
    private let dummy: MTLTexture

    private(set) var capW = 0, capH = 0, outW = 0, outH = 0
    private var procW = 0, procH = 0
    private var procFactor = 2, refine = 1, radius = 6
    var sharpness: Float = 0.5
    private var frame: [MTLTexture] = []
    private var pyr: [[MTLTexture]] = [[], []]
    private var fwdT: [MTLTexture] = [], fwd: [MTLTexture] = [], bwdT: [MTLTexture] = [], bwd: [MTLTexture] = []
    private var prevFwd: MTLTexture?
    private var havePrevFwd = false
    private var staticMap: MTLTexture?, gen: MTLTexture?, upTmp: MTLTexture?, out: MTLTexture?
    private var cur = 0
    private(set) var frames = 0
    private var _genMs = 0.0, _analyzeMs = 0.0
    private let timeLock = NSLock()
    var genMs: Double { timeLock.lock(); defer { timeLock.unlock() }; return _genMs }
    var analyzeMs: Double { timeLock.lock(); defer { timeLock.unlock() }; return _analyzeMs }

    var hasPair: Bool { frames >= 2 }

    init(device: MTLDevice) throws {
        self.device = device
        guard let q = device.makeCommandQueue() else { throw FGError("makeCommandQueue") }
        queue = q
        let sd = MTLSamplerDescriptor()
        sd.minFilter = .linear; sd.magFilter = .linear
        sd.sAddressMode = .clampToEdge; sd.tAddressMode = .clampToEdge
        sd.normalizedCoordinates = true
        guard let s = device.makeSamplerState(descriptor: sd) else { throw FGError("sampler") }
        sampler = s
        guard let b = device.makeBuffer(length: 16, options: .storageModePrivate) else { throw FGError("stats") }
        stats = b
        let dd = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba16Float, width: 1, height: 1, mipmapped: false)
        dd.usage = [.shaderRead, .shaderWrite]
        guard let d = device.makeTexture(descriptor: dd) else { throw FGError("dummy") }
        dummy = d
        for (name, src) in GeneratedShaders.sources {
            let lib = try device.makeLibrary(source: src, options: nil)
            guard let fn = lib.makeFunction(name: "\(name)_main") else { throw FGError("function \(name)") }
            pipes[name] = try device.makeComputePipelineState(function: fn)
        }
    }

    private func tex(_ w: Int, _ h: Int, _ fmt: MTLPixelFormat) -> MTLTexture {
        let d = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: fmt, width: max(1, w), height: max(1, h), mipmapped: false)
        d.usage = [.shaderRead, .shaderWrite]
        d.storageMode = .private
        return device.makeTexture(descriptor: d)!
    }

    func resize(capW: Int, capH: Int, outW: Int, outH: Int, settings s: Settings) {
        procFactor = s.procFactor; refine = s.refineIters; radius = s.coarseRadius
        sharpness = s.sharpness
        self.capW = capW; self.capH = capH; self.outW = outW; self.outH = outH
        procW = max(16, capW / procFactor); procH = max(16, capH / procFactor)
        frame = [tex(capW, capH, .bgra8Unorm), tex(capW, capH, .bgra8Unorm)]
        var sizes = [(procW, procH)]
        while sizes.last!.0 / 2 >= 64 { sizes.append((sizes.last!.0 / 2, sizes.last!.1 / 2)) }
        pyr = [sizes.map { tex($0.0, $0.1, .r16Float) }, sizes.map { tex($0.0, $0.1, .r16Float) }]
        func fields() -> [MTLTexture] { sizes.map { tex(($0.0 + 7) / 8, ($0.1 + 7) / 8, .rgba16Float) } }
        fwdT = fields(); fwd = fields(); bwdT = fields(); bwd = fields()
        prevFwd = tex(fwd[0].width, fwd[0].height, .rgba16Float)
        havePrevFwd = false
        staticMap = tex(capW, capH, .r8Unorm)
        gen = tex(capW, capH, .bgra8Unorm)
        if s.upscale || outW != capW || outH != capH {
            upTmp = tex(outW, outH, .rgba16Float)
            out = tex(outW, outH, .bgra8Unorm)
        } else { upTmp = nil; out = nil }
        frames = 0
    }

    func resetHistory() { frames = 0; havePrevFwd = false }

    private func base() -> FgConstants {
        var c = FgConstants()
        c.u(FgConstants.capSize, UInt32(capW)); c.u(FgConstants.capSize + 1, UInt32(capH))
        c.u(FgConstants.procSize, UInt32(procW)); c.u(FgConstants.procSize + 1, UInt32(procH))
        c.i(FgConstants.radius, Int32(radius)); c.u(FgConstants.refineIters, UInt32(refine))
        c.f(FgConstants.lam, 0.004); c.f(FgConstants.zeroBias, 0.002)
        c.u(FgConstants.fpIters, 2); c.f(FgConstants.fbSigma, 1.5); c.f(FgConstants.fbSigmaK, 0.10)
        c.f(FgConstants.staticThr, 0.02); c.f(FgConstants.cutErr, 0.07); c.f(FgConstants.cutRatio, 0.60)
        c.u(FgConstants.outSize, UInt32(outW)); c.u(FgConstants.outSize + 1, UInt32(outH))
        c.f(FgConstants.sharpness, sharpness)
        return c
    }

    /// srv = textures for t0..t5 (nil = dummy). statsSlot: 7 (read) or 9 (write) or nil.
    private func run(_ enc: MTLComputeCommandEncoder, _ name: String, _ c: FgConstants,
                     _ srv: [MTLTexture?], _ uav: MTLTexture, statsSlot: Int?, _ gx: Int, _ gy: Int) {
        enc.setComputePipelineState(pipes[name]!)
        var words = c.w
        enc.setBytes(&words, length: 144, index: 0)
        enc.setSamplerState(sampler, index: 1)
        for k in 0..<6 { enc.setTexture(k < srv.count ? (srv[k] ?? dummy) : dummy, index: 2 + k) }
        enc.setTexture(uav, index: 8)
        if let slot = statsSlot { enc.setBuffer(stats, offset: 0, index: slot) }
        enc.dispatchThreadgroups(MTLSize(width: (gx + 7) / 8, height: (gy + 7) / 8, depth: 1),
                                 threadsPerThreadgroup: MTLSize(width: 8, height: 8, depth: 1))
    }

    private func motion(_ enc: MTLComputeCommandEncoder, from: Int, to: Int, tmp: [MTLTexture], out o: [MTLTexture],
                        temporal: MTLTexture?, stats st: Bool) {
        let ps = pyr[from], pd = pyr[to], L = ps.count
        for l in stride(from: L - 1, through: 0, by: -1) {
            var c = base()
            c.u(FgConstants.lvlSize, UInt32(ps[l].width)); c.u(FgConstants.lvlSize + 1, UInt32(ps[l].height))
            c.u(FgConstants.gridSize, UInt32(o[l].width)); c.u(FgConstants.gridSize + 1, UInt32(o[l].height))
            c.u(FgConstants.isCoarse, l == L - 1 ? 1 : 0); c.u(FgConstants.isFinest, l == 0 ? 1 : 0)
            if l < L - 1 {
                c.u(FgConstants.parentGrid, UInt32(o[l + 1].width)); c.u(FgConstants.parentGrid + 1, UInt32(o[l + 1].height))
            }
            c.u(FgConstants.hasTemporal, temporal != nil ? 1 : 0)
            c.u(FgConstants.temporalShift, UInt32(l))
            c.u(FgConstants.accumStats, st && l == 0 ? 1 : 0)
            let parent: MTLTexture? = l < L - 1 ? o[l + 1] : nil
            run(enc, "motion_search", c, [ps[l], pd[l], parent, temporal], tmp[l], statsSlot: nil, o[l].width, o[l].height)
            run(enc, "motion_median", c, [ps[l], pd[l], tmp[l]], o[l], statsSlot: 9, o[l].width, o[l].height)
        }
    }

    /// Copy a captured frame (BGRA, any size >= cap) and analyse the new pair.
    func pushFrame(_ src: MTLTexture) {
        let slot = frames == 0 ? 0 : 1 - cur
        guard let cb = queue.makeCommandBuffer(), let blit = cb.makeBlitCommandEncoder() else { return }
        blit.copy(from: src, sourceSlice: 0, sourceLevel: 0, sourceOrigin: MTLOrigin(x: 0, y: 0, z: 0),
                  sourceSize: MTLSize(width: min(capW, src.width), height: min(capH, src.height), depth: 1),
                  to: frame[slot], destinationSlice: 0, destinationLevel: 0, destinationOrigin: MTLOrigin(x: 0, y: 0, z: 0))
        let pairReady = frames + 1 >= 2
        if pairReady { blit.fill(buffer: stats, range: 0..<16, value: 0) }
        blit.endEncoding()
        cur = slot
        frames += 1
        guard let enc = cb.makeComputeCommandEncoder() else { return }
        // pyramid of the new frame (the previous one is reused as A)
        var c = base()
        run(enc, "luma_down", c, [frame[slot]], pyr[slot][0], statsSlot: nil, procW, procH)
        for l in 1..<pyr[slot].count {
            c.u(FgConstants.lvlSize, UInt32(pyr[slot][l].width)); c.u(FgConstants.lvlSize + 1, UInt32(pyr[slot][l].height))
            run(enc, "pyr_down", c, [pyr[slot][l - 1]], pyr[slot][l], statsSlot: nil, pyr[slot][l].width, pyr[slot][l].height)
        }
        if frames >= 2 {
            let a = 1 - cur, b = cur
            motion(enc, from: a, to: b, tmp: fwdT, out: fwd, temporal: havePrevFwd ? prevFwd : nil, stats: true)
            motion(enc, from: b, to: a, tmp: bwdT, out: bwd, temporal: nil, stats: false)
            run(enc, "static_map", base(), [frame[a], frame[b]], staticMap!, statsSlot: nil, capW, capH)
        }
        enc.endEncoding()
        if frames >= 2, let pf = prevFwd, let blit2 = cb.makeBlitCommandEncoder() {
            blit2.copy(from: fwd[0], to: pf)
            blit2.endEncoding()
            havePrevFwd = true
        }
        cb.addCompletedHandler { [weak self] cb in self?.record(cb, gen: false) }
        cb.commit()
    }

    /// Frame at t between the last two frames, upscaled/sharpened if configured.
    /// The returned texture is BGRA8 (outW x outH or capW x capH).
    func generate(_ t: Float, into cb: MTLCommandBuffer) -> MTLTexture? {
        guard frames >= 2, let enc = cb.makeComputeCommandEncoder() else { return nil }
        let a = 1 - cur, b = cur
        var c = base()
        c.f(FgConstants.t, min(max(t, 0), 1))
        run(enc, "interpolate", c, [frame[a], frame[b], fwd[0], bwd[0], staticMap, nil], gen!, statsSlot: 7, capW, capH)
        var result = gen!
        if let up = upTmp, let o = out {
            run(enc, "upscale", c, [gen], up, statsSlot: nil, outW, outH)
            run(enc, "sharpen", c, [up, nil], o, statsSlot: nil, outW, outH)
            result = o
        }
        enc.endEncoding()
        cb.addCompletedHandler { [weak self] cb in self?.record(cb, gen: true) }
        return result
    }

    private func record(_ cb: MTLCommandBuffer, gen isGen: Bool) {
        let ms = (cb.gpuEndTime - cb.gpuStartTime) * 1000
        guard ms > 0 else { return }
        timeLock.lock(); defer { timeLock.unlock() }
        if isGen { _genMs = _genMs == 0 ? ms : _genMs * 0.9 + ms * 0.1 }
        else { _analyzeMs = _analyzeMs == 0 ? ms : _analyzeMs * 0.9 + ms * 0.1 }
    }
}

struct FGError: Error, CustomStringConvertible {
    let description: String
    init(_ s: String) { description = s }
}
