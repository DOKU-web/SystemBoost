// Benchmark mode: GPU time of the real analysis and interpolation passes (FrameGenGpu) on a
// synthetic moving picture. No window, no capture, no overlay, no cursor; no other process is opened.
//   sb_framegen.exe --bench --bench-width W --bench-height H [--gen-gpu ...] [--parent-pid P] [--shader-dir D]
//   sb_framegen.exe --bench-load S --bench-width W --bench-height H [...]
// Lines and time plan: BenchProtocol.h.
#pragma once
#include "BenchProtocol.h"
#include "FrameGenGpu.h"
#include "Settings.h"
#include <atomic>

namespace sbfg::bench {

// Waits for the GPU to finish everything issued so far (an event query at the end of a batch).
class GpuWait {
public:
    GpuWait(ID3D11Device* dev, ID3D11DeviceContext* ctx);
    ~GpuWait();
    GpuWait(const GpuWait&) = delete;
    GpuWait& operator=(const GpuWait&) = delete;
    // expectedSec: about how long the batch started at t0 takes. Most of it is slept through, the
    // end is spun, so the measured time is the GPU's and not a wake-up. 0 = spin all the way.
    void Wait(double t0 = 0, double expectedSec = 0);
    void Flush() { ctx_->Flush(); }
    // GPU time of a batch: StartTiming() before its first command, then WaitTimed() instead of
    // Wait(): GPU timestamps around the batch (TIMESTAMP_DISJOINT), read once it is done. Returns
    // ms, or -1 when the GPU could not time it (its clock changed in between, or no answer).
    void StartTiming();
    double WaitTimed(double t0, double expectedSec);
    // Sleeps about this long without asking for a finer system timer (a timer of its own).
    void Nap(double sec);
    // Throws HrError with the device's removed reason.
    [[noreturn]] void Lost(HRESULT hr);

private:
    ID3D11Device* dev_;
    ID3D11DeviceContext* ctx_;
    ComPtr<ID3D11Query> done_, disjoint_, tsBegin_, tsEnd_;
    HANDLE timer_ = nullptr;
};

// A picture with detail at every scale (as tests/gen_bench.cpp had it), a little larger than W x H.
// Frame i is a W x H region of it, moved 6 px across and 2 px down per frame, back and forth.
class Scene {
public:
    Scene(ID3D11Device* dev, ID3D11DeviceContext* ctx, UINT w, UINT h);
    UINT W() const { return w_; }
    UINT H() const { return h_; }
    D3D11_BOX Box(int frame) const;
    void Push(FrameGenGpu& fg, int frame) { fg.PushFrame(tex_.Get(), Box(frame)); }

private:
    UINT w_, h_;
    ComPtr<ID3D11Texture2D> tex_;
};

struct Timing { double analyzeMs = 0, genMs = 0; int rounds = 0; };

// One quality at the scene's size: GPU-waited batches of analyses and of generated frames, the
// median ms per analysed game frame and per generated frame. Each batch is issued whole (no Flush
// in between, so the GPU runs it back to back) and timed with GPU timestamps: GPU time, not the
// time the CPU takes to issue it. Wall time only when the GPU timed no batch. Ends early when
// *stop turns true.
Timing MeasureQuality(FrameGenGpu& fg, Scene& scene, GpuWait& wait, Quality q, double budgetSec,
                      const std::atomic<bool>* stop);

// --bench / --bench-load. Prints the lines of BenchProtocol.h and returns the exit code:
// 0 done, 1 bad arguments, 2 no GPU or no shaders, 3 Direct3D failed (also a lost device),
// 4 other error, 8 over the time limit, 9 the --parent-pid process ended.
int Run(const Settings& s);
// {"type":"error","message":...} on stdout.
void EmitError(const std::string& message);

}  // namespace sbfg::bench
