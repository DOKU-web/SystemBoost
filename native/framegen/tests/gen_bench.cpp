// How much GPU time frame generation costs: no window, no capture, nothing on the screen.
// Runs batches and waits for the GPU at the end of each: a median over batches, per game frame and
// per generated frame, from GPU timestamps around each batch (the time the CPU takes to issue the
// work stays out; it doubled the time of a generated frame on a busy CPU).
// The measurement is the one of sb_framegen --bench (src/Bench.cpp), here on the default adapter and
// with no time limit: full batches (20 analyses, 60 generated frames), 9 rounds per quality.
//   gen_bench.exe <shader folder> [width height]
// Build: cmake -DSBFG_TESTS=ON (target gen_bench), or from native/framegen in a Developer prompt:
//   cl /std:c++20 /O2 /EHsc /utf-8 /DUNICODE /D_UNICODE /DNOMINMAX /DWIN32_LEAN_AND_MEAN /Isrc tests/gen_bench.cpp
//   src/Bench.cpp src/FrameGenGpu.cpp src/Gpu.cpp /Fe:build/tests/gen_bench.exe
//   /link d3d11.lib dxgi.lib d3dcompiler.lib user32.lib
#include "Bench.h"
#include <cstdio>

using namespace sbfg;

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) return 2;
    const UINT w = argc > 3 ? UINT(_wtoi(argv[2])) : 1920, h = argc > 3 ? UINT(_wtoi(argv[3])) : 1080;
    try {
        ComPtr<ID3D11Device> dev;
        ComPtr<ID3D11DeviceContext> ctx;
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev,
                                nullptr, &ctx), "device");
        bench::Scene scene(dev.Get(), ctx.Get(), w, h);
        bench::GpuWait wait(dev.Get(), ctx.Get());
        FrameGenGpu fg(dev.Get(), ctx.Get(), argv[1]);
        std::printf("%ux%u  analysis ms per game frame / ms per generated frame\n", w, h);
        for (Quality q : {Quality::Quality, Quality::Balanced, Quality::Performance, Quality::Low}) {
            const bench::Timing t = bench::MeasureQuality(fg, scene, wait, q, 1e9, nullptr);
            std::printf("  %-12s %6.2f / %5.2f\n", QualityName(q), t.analyzeMs, t.genMs);
        }
    } catch (const HrError& e) {
        std::printf("failed: %s (hr=0x%08lX)\n", e.what, static_cast<unsigned long>(e.hr));
        return 1;
    }
    return 0;
}
