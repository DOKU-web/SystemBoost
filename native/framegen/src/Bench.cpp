// Benchmark mode (see Bench.h). Nothing here creates a window, captures the screen, changes the
// cursor or opens another process; the --parent-pid process is only waited on (SYNCHRONIZE).
#include "Bench.h"
#include "Gpu.h"
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <new>
#include <thread>

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

namespace sbfg::bench {

namespace {

constexpr int kExitNoGpu = 2, kExitD3D = 3, kExitOther = 4, kExitTimeLimit = 8, kExitParentGone = 9;

std::mutex g_out;
std::atomic<bool> g_stop{false};        // the parent ended: stop at the next check
std::atomic<bool> g_parentGone{false};

void Emit(const std::string& line) {
    std::lock_guard<std::mutex> l(g_out);
    std::fwrite(line.data(), 1, line.size(), stdout);
    std::fputc('\n', stdout);
    std::fflush(stdout);
}

// From the watch thread: the main thread may hold the lock on a full pipe, so only if it is free.
void TryEmit(const std::string& line) {
    std::unique_lock<std::mutex> l(g_out, std::try_to_lock);
    if (!l.owns_lock()) return;
    std::fwrite(line.data(), 1, line.size(), stdout);
    std::fputc('\n', stdout);
    std::fflush(stdout);
}

// The parent's end and the time limit. A GPU wait that never returns is ended here too.
void Watch(HANDLE parent, double limitAt) {
    for (;;) {
        if (parent) {
            if (WaitForSingleObject(parent, 100) == WAIT_OBJECT_0) {
                g_parentGone = true;
                g_stop = true;
                Sleep(3000);    // the main thread ends at its next check, well before this
                TerminateProcess(GetCurrentProcess(), kExitParentGone);
            }
        } else {
            Sleep(100);
        }
        if (QpcNow() > limitAt) {
            TryEmit(ErrorLine("the benchmark took longer than its time limit"));
            TerminateProcess(GetCurrentProcess(), kExitTimeLimit);
        }
    }
}

int Stopped() {
    if (g_parentGone) { Emit(ErrorLine("the parent process ended")); return kExitParentGone; }
    Emit(ErrorLine("stopped"));
    return kExitOther;
}

std::wstring ShaderDir(const Settings& s) {
    if (!s.shaderDir.empty()) {
        std::wstring dir;
        const int n = MultiByteToWideChar(CP_UTF8, 0, s.shaderDir.c_str(), -1, nullptr, 0);
        dir.resize(n > 0 ? size_t(n - 1) : 0);
        if (n > 1) MultiByteToWideChar(CP_UTF8, 0, s.shaderDir.c_str(), -1, dir.data(), n);
        return dir;
    }
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring dir = path;
    return dir.substr(0, dir.find_last_of(L"\\/")) + L"\\shaders";
}

bool HasShaders(const std::wstring& dir) {
    for (const wchar_t* ext : {L".cso", L".hlsl"})
        if (GetFileAttributesW((dir + L"\\luma_down" + ext).c_str()) != INVALID_FILE_ATTRIBUTES) return true;
    return false;
}

float GenT(int i) { return 0.25f + 0.25f * float(i % 3); }

// Balanced passes until the clocks are up and the driver has seen every shader once.
void Warmup(FrameGenGpu& fg, Scene& scene, GpuWait& wait, double seconds) {
    Settings s;
    s.quality = Quality::Balanced;
    fg.Resize(scene.W(), scene.H(), scene.W(), scene.H(), s);
    fg.ResetHistory();
    const double start = QpcNow();
    int pic = 0;
    do {    // spun, not slept: the first passes are slow (the driver compiles), a nap from them would overshoot
        if (g_stop) return;
        scene.Push(fg, pic++);
        for (int g = 0; g < 3; ++g) fg.Generate(GenT(g));
        wait.Wait();
    } while (QpcNow() - start < seconds);
}

int RunTimes(FrameGenGpu& fg, Scene& scene, GpuWait& wait, const std::string& gpu, double start) {
    Warmup(fg, scene, wait, kWarmupSec);
    const Quality order[] = {Quality::Low, Quality::Performance, Quality::Balanced, Quality::Quality};
    double lastSec = 0;
    for (int i = 0; i < 4; ++i) {
        if (g_stop) return Stopped();
        const double t0 = QpcNow();
        if (i > 0 && !QualityFits(t0 - start, lastSec)) {
            Emit(ErrorLine("the GPU is too slow for the benchmark at " + std::to_string(scene.W()) + "x" +
                           std::to_string(scene.H()) + ": " + QualityName(order[i]) + " did not fit in the time limit"));
            return kExitTimeLimit;
        }
        const Timing t = MeasureQuality(fg, scene, wait, order[i], QualityBudgetSec(t0 - start, 4 - i), &g_stop);
        if (g_stop) return Stopped();
        lastSec = QpcNow() - t0;
        Emit(BenchLine(QualityName(order[i]), scene.W(), scene.H(), t.analyzeMs, t.genMs, gpu));
    }
    Emit(DoneLine());
    return 0;
}

int RunLoad(FrameGenGpu& fg, Scene& scene, GpuWait& wait, ID3D11Device* dev, ID3D11DeviceContext* ctx,
            const std::string& gpu, int seconds) {
    Settings s;
    s.quality = Quality::Balanced;
    fg.Resize(scene.W(), scene.H(), scene.W(), scene.H(), s);
    fg.ResetHistory();
    int pic = 0;
    const auto cycle = [&] {
        scene.Push(fg, pic++);
        for (int g = 0; g < kLoadGenPerCycle; ++g) fg.Generate(float(g + 1) / float(kLoadGenPerCycle + 1));
        ctx->Flush();
    };
    for (int i = 0; i < 3; ++i) scene.Push(fg, pic++);
    wait.Wait();
    const double c0 = QpcNow();
    for (int i = 0; i < 2; ++i) cycle();
    wait.Wait();
    const int perBatch = LoadCyclesPerBatch((QpcNow() - c0) * 1000.0 / 2);

    struct Slot { ComPtr<ID3D11Query> done; int frames = 0; };
    Slot slots[kLoadInFlight];
    for (auto& sl : slots) {
        D3D11_QUERY_DESC d{D3D11_QUERY_EVENT, 0};
        Check(dev->CreateQuery(&d, &sl.done), "event query");
    }
    int head = 0, inFlight = 0;    // batches complete in the order they were issued
    const double loadStart = QpcNow();
    double frames = 0, lastDone = loadStart, prevFrames = 0, prevDone = loadStart;
    for (int sec = 1; sec <= seconds;) {
        if (g_stop) return Stopped();
        while (inFlight < kLoadInFlight) {
            Slot& sl = slots[(head + inFlight) % kLoadInFlight];
            for (int c = 0; c < perBatch; ++c) cycle();
            ctx->End(sl.done.Get());
            ctx->Flush();
            sl.frames = perBatch * kLoadGenPerCycle;
            ++inFlight;
        }
        bool progressed = false;
        while (inFlight > 0) {
            Slot& sl = slots[head];
            BOOL done = FALSE;
            const HRESULT hr = ctx->GetData(sl.done.Get(), &done, sizeof done, D3D11_ASYNC_GETDATA_DONOTFLUSH);
            if (FAILED(hr)) wait.Lost(hr);
            if (hr != S_OK) break;
            frames += sl.frames;
            lastDone = QpcNow();
            head = (head + 1) % kLoadInFlight;
            --inFlight;
            progressed = true;
        }
        if (QpcNow() >= loadStart + sec) {
            Emit(LoadLine(sec, FramesPerSec(frames - prevFrames, prevDone, lastDone), gpu));
            prevFrames = frames;
            prevDone = lastDone;
            ++sec;
        } else if (!progressed) {
            wait.Nap(0.0007);
        }
    }
    wait.Wait();    // the GPU is idle again when bench_done is read
    Emit(DoneLine());
    return 0;
}

int RunOn(const Settings& s, double start) {
    const auto gpus = EnumerateGpus(s.genGpu == GenGpu::Integrated);
    ComPtr<IDXGIAdapter1> adapter;
    if (s.genGpu == GenGpu::Warp) {
        adapter = WarpAdapter();    // tests only: stands in for a slow GPU
    } else {
        // "same" is the adapter of the monitor the game would be on; without a game, the primary one
        const int display = AdapterForMonitor(MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY), gpus);
        std::string why;
        const int idx = PickGenGpuFor(s, display, gpus, why);
        if (idx < 0) { Emit(ErrorLine("no GPU found")); return kExitNoGpu; }
        adapter = AdapterByIndex(idx);
    }
    DXGI_ADAPTER_DESC1 desc{};
    adapter->GetDesc1(&desc);
    const std::string gpu = Narrow(desc.Description);

    const std::wstring dir = ShaderDir(s);
    if (!HasShaders(dir)) { Emit(ErrorLine("shaders not found in " + Narrow(dir.c_str()))); return kExitNoGpu; }
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    CreateDevice(adapter.Get(), dev, ctx);
    FrameGenGpu fg(dev.Get(), ctx.Get(), dir);
    Scene scene(dev.Get(), ctx.Get(), s.benchW, s.benchH);
    GpuWait wait(dev.Get(), ctx.Get());
    return s.bench ? RunTimes(fg, scene, wait, gpu, start)
                   : RunLoad(fg, scene, wait, dev.Get(), ctx.Get(), gpu, s.benchLoadSec);
}

}  // namespace

// --- GpuWait -------------------------------------------------------------------
GpuWait::GpuWait(ID3D11Device* dev, ID3D11DeviceContext* ctx) : dev_(dev), ctx_(ctx) {
    D3D11_QUERY_DESC d{D3D11_QUERY_EVENT, 0};
    Check(dev_->CreateQuery(&d, &done_), "event query");
    d.Query = D3D11_QUERY_TIMESTAMP_DISJOINT;
    Check(dev_->CreateQuery(&d, &disjoint_), "timestamp disjoint query");
    d.Query = D3D11_QUERY_TIMESTAMP;
    Check(dev_->CreateQuery(&d, &tsBegin_), "timestamp query");
    Check(dev_->CreateQuery(&d, &tsEnd_), "timestamp query");
    timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (!timer_) timer_ = CreateWaitableTimerW(nullptr, FALSE, nullptr);
}

GpuWait::~GpuWait() {
    if (timer_) CloseHandle(timer_);
}

void GpuWait::Nap(double sec) {
    if (!timer_) { Sleep(1); return; }
    LARGE_INTEGER due;
    due.QuadPart = -LONGLONG(std::max(sec, 0.0001) * 1e7);    // relative, 100 ns
    SetWaitableTimer(timer_, &due, 0, nullptr, nullptr, FALSE);
    WaitForSingleObject(timer_, 50);
}

void GpuWait::Lost(HRESULT hr) {
    const HRESULT why = dev_->GetDeviceRemovedReason();
    throw HrError{FAILED(why) ? why : hr, "GPU device lost"};
}

void GpuWait::Wait(double t0, double expectedSec) {
    ctx_->End(done_.Get());
    ctx_->Flush();
    const double wakeAt = t0 + expectedSec;
    for (;;) {
        BOOL done = FALSE;
        const HRESULT hr = ctx_->GetData(done_.Get(), &done, sizeof done, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (hr == S_OK) return;
        if (FAILED(hr)) Lost(hr);
        const double left = wakeAt - QpcNow();
        if (left > 0.002) Nap(std::min(left - 0.001, 0.01));
        else YieldProcessor();
    }
}

void GpuWait::StartTiming() {
    ctx_->Begin(disjoint_.Get());
    ctx_->End(tsBegin_.Get());
}

double GpuWait::WaitTimed(double t0, double expectedSec) {
    ctx_->End(tsEnd_.Get());
    ctx_->End(disjoint_.Get());
    Wait(t0, expectedSec);
    // everything before the event is done, so the answers are there or follow at once
    D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj{};
    UINT64 b = 0, e = 0;
    const double giveUp = QpcNow() + 0.1;
    for (;;) {
        const HRESULT h1 = ctx_->GetData(disjoint_.Get(), &dj, sizeof dj, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        const HRESULT h2 = ctx_->GetData(tsBegin_.Get(), &b, sizeof b, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        const HRESULT h3 = ctx_->GetData(tsEnd_.Get(), &e, sizeof e, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (FAILED(h1) || FAILED(h2) || FAILED(h3)) Lost(FAILED(h1) ? h1 : FAILED(h2) ? h2 : h3);
        if (h1 == S_OK && h2 == S_OK && h3 == S_OK) break;
        if (QpcNow() > giveUp) return -1;
        YieldProcessor();
    }
    return TicksToMs(b, e, dj.Frequency, dj.Disjoint != FALSE);
}

// --- Scene ---------------------------------------------------------------------
constexpr int kSceneSteps = 6;                // positions, visited back and forth
constexpr UINT kStepX = 6, kStepY = 2;        // px per frame

Scene::Scene(ID3D11Device* dev, ID3D11DeviceContext* ctx, UINT w, UINT h) : w_(w), h_(h) {
    const UINT tw = w + kStepX * (kSceneSteps - 1), th = h + kStepY * (kSceneSteps - 1);
    D3D11_TEXTURE2D_DESC d{};
    d.Width = tw; d.Height = th; d.MipLevels = 1; d.ArraySize = 1; d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    Check(dev->CreateTexture2D(&d, nullptr, &tex_), "bench picture");
    // the pattern of tests/gen_bench.cpp, from tables: a sine across, one down, one diagonal, a checker
    std::vector<double> across(tw), down(th), diag(size_t(tw) + th);
    for (UINT x = 0; x < tw; ++x) across[x] = 50 * std::sin(x / 37.0);
    for (UINT y = 0; y < th; ++y) down[y] = 40 * std::cos(y / 23.0);
    for (size_t k = 0; k < diag.size(); ++k) diag[k] = 30 * std::sin(double(k) / 7.0);
    constexpr UINT kStrip = 128;
    std::vector<uint32_t> px(size_t(tw) * kStrip);
    for (UINT y0 = 0; y0 < th; y0 += kStrip) {
        const UINT rows = std::min(kStrip, th - y0);
        for (UINT r = 0; r < rows; ++r) {
            const UINT y = y0 + r;
            uint32_t* row = px.data() + size_t(r) * tw;
            for (UINT x = 0; x < tw; ++x) {
                const int c = 128 + int(across[x] + down[y] + diag[size_t(x) + y]);
                const int checker = int(((x / 16 + y / 16) & 1) * 40);
                const uint32_t g = uint32_t(std::clamp(c + checker, 0, 255));
                row[x] = 0xFF000000u | g | (g << 8) | ((255 - g) << 16);
            }
        }
        const D3D11_BOX b{0, y0, 0, tw, y0 + rows, 1};
        ctx->UpdateSubresource(tex_.Get(), 0, &b, px.data(), tw * 4, 0);
    }
}

D3D11_BOX Scene::Box(int frame) const {
    const int period = 2 * (kSceneSteps - 1);
    const int p = ((frame % period) + period) % period;
    const UINT k = UINT(p < kSceneSteps ? p : period - p);
    return D3D11_BOX{k * kStepX, k * kStepY, 0, k * kStepX + w_, k * kStepY + h_, 1};
}

// --- measuring -----------------------------------------------------------------
Timing MeasureQuality(FrameGenGpu& fg, Scene& scene, GpuWait& wait, Quality q, double budgetSec,
                      const std::atomic<bool>* stop) {
    const double start = QpcNow();
    Settings s;
    s.quality = q;
    fg.Resize(scene.W(), scene.H(), scene.W(), scene.H(), s);
    fg.ResetHistory();
    int pic = 0;
    // the first frame has no pair yet; the next two are analysed in full and give the first estimate
    scene.Push(fg, pic++);
    wait.Wait();
    double t0 = QpcNow();
    for (int i = 0; i < 2; ++i) { scene.Push(fg, pic++); wait.Flush(); }
    wait.Wait();
    const double calA = (QpcNow() - t0) * 1000.0 / 2;
    t0 = QpcNow();
    for (int i = 0; i < 4; ++i) { fg.Generate(GenT(i)); wait.Flush(); }
    wait.Wait();
    const double calG = (QpcNow() - t0) * 1000.0 / 4;

    const BatchPlan plan = PlanBatches(calA, calG, budgetSec - (QpcNow() - start));
    // Per piece: GPU time from the timestamps, and wall time for when the GPU timed nothing.
    std::vector<double> analyse, gen, analyseWall, genWall;
    // One batch, issued whole (no Flush in between: the GPU runs it back to back, and the time the CPU
    // takes to issue it stays out of the timestamps). The wait naps through part of the GPU time of the
    // fastest batch so far (round 0 spins); a late wake-up costs wall time only, not accuracy.
    const auto batch = [&](int n, bool analysis, double bestMs, std::vector<double>& gpu, std::vector<double>& wall) {
        const double b0 = QpcNow();
        wait.StartTiming();
        for (int i = 0; i < n; ++i) {
            if (analysis) scene.Push(fg, pic++);
            else fg.Generate(GenT(i));
        }
        const double ms = wait.WaitTimed(b0, 0.7 * bestMs * n / 1000.0);
        wall.push_back((QpcNow() - b0) * 1000.0 / n);
        if (ms >= 0) gpu.push_back(ms / n);
    };
    const auto best = [](const std::vector<double>& v) { return v.empty() ? 0.0 : *std::min_element(v.begin(), v.end()); };
    double lastRound = 0;
    for (int round = 0; NextRound(round, QpcNow() - start, lastRound, budgetSec); ++round) {
        if (stop && *stop) break;
        const double r0 = QpcNow();
        batch(plan.analyses, true, best(analyse), analyse, analyseWall);
        batch(plan.generates, false, best(gen), gen, genWall);
        lastRound = QpcNow() - r0;
    }
    if (analyse.empty()) analyse = analyseWall;
    if (gen.empty()) gen = genWall;
    if (analyse.empty()) analyse.push_back(calA);
    if (gen.empty()) gen.push_back(calG);
    return {Median(analyse), Median(gen), int(analyseWall.size())};
}

void EmitError(const std::string& message) { Emit(ErrorLine(message)); }

int Run(const Settings& s) {
    const double start = QpcNow();
    HANDLE parent = nullptr;
    if (s.parentPid) {
        // only waited on; a process that exists but cannot be opened is not watched (as in the engine)
        parent = OpenProcess(SYNCHRONIZE, FALSE, s.parentPid);
        if (!parent && GetLastError() == ERROR_INVALID_PARAMETER) {    // no such process: it has ended
            Emit(ErrorLine("the parent process ended"));
            return kExitParentGone;
        }
    }
    const double limit = s.bench ? kLimitSec : s.benchLoadSec + kLoadExtraSec;
    std::thread(Watch, parent, start + limit).detach();
    try {
        return RunOn(s, start);
    } catch (const HrError& e) {
        char buf[32];
        std::snprintf(buf, sizeof buf, " (hr=0x%08lX)", static_cast<unsigned long>(e.hr));
        Emit(ErrorLine(std::string(e.what) + buf));
        return kExitD3D;
    } catch (const std::bad_alloc&) {
        Emit(ErrorLine("out of memory"));
        return kExitOther;
    } catch (const std::exception& e) {
        Emit(ErrorLine(e.what()));
        return kExitOther;
    } catch (...) {
        Emit(ErrorLine("unexpected error"));
        return kExitOther;
    }
}

}  // namespace sbfg::bench
