// The real AMD driver access for amd.cpp: ADLX, the library AMD installs with its graphics driver.
// amdadlx64.dll is never shipped with SystemBoost; it is loaded from System32 only. ADLX's own
// loader (SDK/Platform/Windows/WinAPIs.cpp, used by ADLXHelper) also searches the application
// folder and the user directories, so neither is used: this file resolves ADLX's documented entry
// points itself. Only ADLX's interface headers are compiled in (third_party/adlx/README.md).
// Every feature is checked with ADLX's IsSupported* (or by querying its interface) before use;
// a missing DLL, missing entry points or a failed start end as a status, never a crash.
#include "amd.h"
#include "commands.h"
#include <windows.h>
#include <atomic>
#include <cmath>
#include "ADLX.h"
#include "I3DSettings.h"
#include "I3DSettings1.h"
#include "IGPUAutoTuning.h"
#include "IGPUManualGFXTuning.h"
#include "IGPUManualPowerTuning.h"
#include "IGPUManualVRAMTuning.h"
#include "IGPUTuning.h"
#include "IPerformanceMonitoring.h"
#include "ISystem2.h"

using namespace adlx;

namespace {

amd::Res res(ADLX_RESULT r) {
    if (ADLX_SUCCEEDED(r)) return amd::Res::Ok;
    if (r == ADLX_NOT_SUPPORTED) return amd::Res::Unsupported;
    if (r == ADLX_RESET_NEEDED) return amd::Res::ResetNeeded;
    return amd::Res::Failed;
}

int whole(double d) { return std::isfinite(d) && d >= 0 && d < 2e9 ? (int)std::lround(d) : -1; }

// ADLX calls this on its own thread when auto tuning is done.
class AutoListener : public IADLXGPUAutoTuningCompleteListener {
public:
    std::atomic<bool> done{false};
    adlx_bool ADLX_STD_CALL OnGPUAutoTuningComplete(IADLXGPUAutoTuningCompleteEvent*) override {
        done = true;
        return true;
    }
};

class Adlx final : public amd::Driver {
public:
    amd::Status open() override {
        if (tried_) return status_;
        tried_ = true;
        return status_ = start();
    }

    std::string adlxVersion() override { return version_; }
    std::string driverVersion() override { return driver_; }
    int gpuCount() override { return gpus_ ? (int)gpus_->Size() : 0; }

    std::string gpuName(int i) override {
        IADLXGPUPtr g = gpu(i);
        const char* name = nullptr;
        return g && g->Name(&name) == ADLX_OK && name ? name : "";
    }

    bool integrated(int i) override {
        IADLXGPUPtr g = gpu(i);
        ADLX_GPU_TYPE type = GPUTYPE_UNDEFINED;
        return g && g->Type(&type) == ADLX_OK && type == GPUTYPE_INTEGRATED;
    }

    amd::Res atFactory(int i, bool& factory) override {
        IADLXGPUPtr g = gpu(i);
        if (!g || !tuning_) return amd::Res::Unsupported;
        adlx_bool f = false;
        amd::Res r = res(tuning_->IsAtFactory(g, &f));
        factory = f;
        return r;
    }

    amd::Res resetToFactory(int i) override {
        IADLXGPUPtr g = gpu(i);
        if (!g || !tuning_) return amd::Res::Unsupported;
        return res(tuning_->ResetToFactory(g));
    }

    amd::Res range(int i, amd::Knob k, amd::Range& out) override {
        ADLX_IntRange r{};
        amd::Res got = amd::Res::Failed;
        if (k == amd::Knob::Power) {
            IADLXManualPowerTuningPtr p;
            if ((got = power(i, p)) == amd::Res::Ok) got = res(p->GetPowerLimitRange(&r));
        } else if (k == amd::Knob::GpuMax) {
            IADLXManualGraphicsTuning2Ptr g;
            if ((got = graphics(i, g)) == amd::Res::Ok) got = res(g->GetGPUMaxFrequencyRange(&r));
        } else {
            IADLXManualVRAMTuning2Ptr v;
            if ((got = vram(i, v)) == amd::Res::Ok) got = res(v->GetMaxVRAMFrequencyRange(&r));
        }
        if (got == amd::Res::Ok) out = {r.minValue, r.maxValue, r.step};
        return got;
    }

    amd::Res get(int i, amd::Knob k, int& value) override {
        adlx_int v = 0;
        amd::Res got = amd::Res::Failed;
        if (k == amd::Knob::Power) {
            IADLXManualPowerTuningPtr p;
            if ((got = power(i, p)) == amd::Res::Ok) got = res(p->GetPowerLimit(&v));
        } else if (k == amd::Knob::GpuMax) {
            IADLXManualGraphicsTuning2Ptr g;
            if ((got = graphics(i, g)) == amd::Res::Ok) got = res(g->GetGPUMaxFrequency(&v));
        } else {
            IADLXManualVRAMTuning2Ptr m;
            if ((got = vram(i, m)) == amd::Res::Ok) got = res(m->GetMaxVRAMFrequency(&v));
        }
        if (got == amd::Res::Ok) value = v;
        return got;
    }

    amd::Res set(int i, amd::Knob k, int value) override {
        if (k == amd::Knob::Power) {
            IADLXManualPowerTuningPtr p;
            amd::Res got = power(i, p);
            return got == amd::Res::Ok ? res(p->SetPowerLimit(value)) : got;
        }
        if (k == amd::Knob::GpuMax) {
            IADLXManualGraphicsTuning2Ptr g;
            amd::Res got = graphics(i, g);
            return got == amd::Res::Ok ? res(g->SetGPUMaxFrequency(value)) : got;
        }
        IADLXManualVRAMTuning2Ptr m;
        amd::Res got = vram(i, m);
        return got == amd::Res::Ok ? res(m->SetMaxVRAMFrequency(value)) : got;
    }

    bool autoSupported(int i, amd::AutoMode mode) override {
        IADLXGPUAutoTuningPtr a;
        if (autoTuning(i, a) != amd::Res::Ok) return false;
        adlx_bool s = false;
        ADLX_RESULT r = mode == amd::AutoMode::Undervolt ? a->IsSupportedUndervoltGPU(&s)
                        : mode == amd::AutoMode::OcGpu   ? a->IsSupportedOverclockGPU(&s)
                                                         : a->IsSupportedOverclockVRAM(&s);
        return r == ADLX_OK && s;
    }

    amd::Res autoStart(int i, amd::AutoMode mode) override {
        if (!autoSupported(i, mode)) return amd::Res::Unsupported;
        IADLXGPUAutoTuningPtr a;
        amd::Res got = autoTuning(i, a);
        if (got != amd::Res::Ok) return got;
        listener_.done = false;
        return res(mode == amd::AutoMode::Undervolt ? a->StartUndervoltGPU(&listener_)
                   : mode == amd::AutoMode::OcGpu   ? a->StartOverclockGPU(&listener_)
                                                    : a->StartOverclockVRAM(&listener_));
    }

    bool autoDone() override { return listener_.done; }

    amd::Res toggleGet(int i, amd::Toggle t, bool& on) override {
        switch (t) {
        case amd::Toggle::AntiLag: { IADLX3DAntiLagPtr x; return fetch(i, t, x) ? read(x, on) : unsupportedOr(i); }
        case amd::Toggle::Chill: { IADLX3DChillPtr x; return fetch(i, t, x) ? read(x, on) : unsupportedOr(i); }
        case amd::Toggle::Boost: { IADLX3DBoostPtr x; return fetch(i, t, x) ? read(x, on) : unsupportedOr(i); }
        default: { IADLX3DAMDFluidMotionFramesPtr x; return fetchAfmf(x) ? read(x, on) : amd::Res::Unsupported; }
        }
    }

    amd::Res toggleSet(int i, amd::Toggle t, bool on) override {
        switch (t) {
        case amd::Toggle::AntiLag: { IADLX3DAntiLagPtr x; return fetch(i, t, x) ? write(x, on) : unsupportedOr(i); }
        case amd::Toggle::Chill: { IADLX3DChillPtr x; return fetch(i, t, x) ? write(x, on) : unsupportedOr(i); }
        case amd::Toggle::Boost: { IADLX3DBoostPtr x; return fetch(i, t, x) ? write(x, on) : unsupportedOr(i); }
        default: { IADLX3DAMDFluidMotionFramesPtr x; return fetchAfmf(x) ? write(x, on) : amd::Res::Unsupported; }
        }
    }

    amd::Res metrics(int i, amd::Metrics& m) override {
        IADLXGPUMetricsSupportPtr s;
        IADLXGPUMetricsPtr c;
        amd::Res got = current(i, s, c);
        if (got != amd::Res::Ok) return got;
        adlx_bool on = false;
        adlx_double d = 0;
        adlx_int n = 0;
        if (s->IsSupportedGPUTemperature(&on) == ADLX_OK && on && c->GPUTemperature(&d) == ADLX_OK) m.tempC = whole(d);
        if (s->IsSupportedGPUHotspotTemperature(&on) == ADLX_OK && on && c->GPUHotspotTemperature(&d) == ADLX_OK) m.hotspotC = whole(d);
        if (s->IsSupportedGPUClockSpeed(&on) == ADLX_OK && on && c->GPUClockSpeed(&n) == ADLX_OK) m.gpuMHz = n;
        if (s->IsSupportedGPUVRAMClockSpeed(&on) == ADLX_OK && on && c->GPUVRAMClockSpeed(&n) == ADLX_OK) m.vramMHz = n;
        // board power like NVML reports it for NVIDIA; the chip's own power where there is none
        if (s->IsSupportedGPUTotalBoardPower(&on) == ADLX_OK && on && c->GPUTotalBoardPower(&d) == ADLX_OK) m.powerW = whole(d);
        else if (s->IsSupportedGPUPower(&on) == ADLX_OK && on && c->GPUPower(&d) == ADLX_OK) m.powerW = whole(d);
        if (s->IsSupportedGPUUsage(&on) == ADLX_OK && on && c->GPUUsage(&d) == ADLX_OK) m.usagePct = whole(d);
        if (s->IsSupportedGPUVRAM(&on) == ADLX_OK && on && c->GPUVRAM(&n) == ADLX_OK) m.vramUsedMB = n;
        return amd::Res::Ok;
    }

    amd::Res temperature(int i, int& celsius) override {
        IADLXGPUMetricsSupportPtr s;
        IADLXGPUMetricsPtr c;
        amd::Res got = current(i, s, c);
        if (got != amd::Res::Ok) return got;
        adlx_bool on = false;
        adlx_double d = 0;
        if (s->IsSupportedGPUTemperature(&on) != ADLX_OK || !on) return amd::Res::Unsupported;
        if (c->GPUTemperature(&d) != ADLX_OK || whole(d) < 0) return amd::Res::Failed;
        celsius = whole(d);
        return amd::Res::Ok;
    }

private:
    using Init_Fn = ADLX_RESULT(ADLX_CDECL_CALL*)(adlx_uint64, IADLXSystem**);
    using Init2_Fn = ADLX_RESULT(ADLX_CDECL_CALL*)(adlx_uint64, IADLXSystem**, IADLMapping**);

    amd::Status start() {
        HMODULE dll = LoadLibraryExW(L"amdadlx64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!dll) return amd::Status::NoDll;
        auto version = (ADLXQueryVersion_Fn)GetProcAddress(dll, ADLX_QUERY_VERSION_FUNCTION_NAME);
        auto init2 = (Init2_Fn)GetProcAddress(dll, ADLX_INIT2_FUNCTION_NAME);
        auto init = (Init_Fn)GetProcAddress(dll, ADLX_INIT_FUNCTION_NAME);
        auto legacy2 = (Init2_Fn)GetProcAddress(dll, ADLX_INIT2_WITH_INCOMPATIBLE_DRIVER_FUNCTION_NAME);
        auto legacy = (Init_Fn)GetProcAddress(dll, ADLX_INIT_WITH_INCOMPATIBLE_DRIVER_FUNCTION_NAME);
        auto terminate = (ADLXTerminate_Fn)GetProcAddress(dll, ADLX_TERMINATE_FUNCTION_NAME);
        if (!version || (!init2 && !init) || !terminate) return amd::Status::OldDriver;
        const char* v = nullptr;
        if (version(&v) == ADLX_OK && v) version_ = v;

        IADLMapping* mapping = nullptr;
        ADLX_RESULT r = init2 ? init2(ADLX_FULL_VERSION, &sys_, &mapping) : init(ADLX_FULL_VERSION, &sys_);
        if (r != ADLX_OK || !sys_) {
            sys_ = nullptr;
            // ADLX starts only in its legacy mode: an AMD GPU or driver it no longer fully supports.
            // Nothing is used in that mode.
            IADLXSystem* old = nullptr;
            ADLX_RESULT l = legacy2 ? legacy2(ADLX_FULL_VERSION, &old, &mapping) : legacy ? legacy(ADLX_FULL_VERSION, &old) : ADLX_FAIL;
            if (l == ADLX_OK && old) {
                terminate();
                return amd::Status::OldDriver;
            }
            return r == ADLX_BAD_VER ? amd::Status::OldDriver : r == ADLX_ADL_INIT_ERROR ? amd::Status::NoGpu : amd::Status::Failed;
        }
        if (sys_->GetGPUs(&gpus_) != ADLX_OK || !gpus_ || gpus_->Size() == 0) return amd::Status::NoGpu;
        // A service ADLX does not offer stays empty; every call then answers unsupported.
        if (sys_->GetGPUTuningServices(&tuning_) != ADLX_OK) tuning_ = nullptr;
        if (sys_->Get3DSettingsServices(&settings_) != ADLX_OK) settings_ = nullptr;
        if (sys_->GetPerformanceMonitoringServices(&perf_) != ADLX_OK) perf_ = nullptr;
        IADLXGPU2Ptr first(gpu(0));
        if (first) {
            if (first->AMDSoftwareVersion(&v) == ADLX_OK && v && *v) driver_ = v;
            else if (first->DriverVersion(&v) == ADLX_OK && v) driver_ = v;
        }
        return amd::Status::Ok;
    }

    IADLXGPUPtr gpu(int i) {
        IADLXGPUPtr g;
        if (!gpus_ || i < 0 || (adlx_uint)i >= gpus_->Size() || gpus_->At((adlx_uint)i, &g) != ADLX_OK) return nullptr;
        return g;
    }

    amd::Res power(int i, IADLXManualPowerTuningPtr& out) {
        IADLXGPUPtr g = gpu(i);
        adlx_bool s = false;
        if (!g || !tuning_) return amd::Res::Unsupported;
        if (tuning_->IsSupportedManualPowerTuning(g, &s) != ADLX_OK) return amd::Res::Failed;
        if (!s) return amd::Res::Unsupported;
        IADLXInterfacePtr ifc;
        if (tuning_->GetManualPowerTuning(g, &ifc) != ADLX_OK || !ifc) return amd::Res::Failed;
        out = IADLXManualPowerTuningPtr(ifc);
        return out ? amd::Res::Ok : amd::Res::Unsupported;
    }

    // Only GPUs with one max clock (ManualGraphicsTuning2, RDNA on). Older ones have only
    // ManualGraphicsTuning1 (a list of clock/voltage states): unsupported here rather than a guess.
    amd::Res graphics(int i, IADLXManualGraphicsTuning2Ptr& out) {
        IADLXGPUPtr g = gpu(i);
        adlx_bool s = false;
        if (!g || !tuning_) return amd::Res::Unsupported;
        if (tuning_->IsSupportedManualGFXTuning(g, &s) != ADLX_OK) return amd::Res::Failed;
        if (!s) return amd::Res::Unsupported;
        IADLXInterfacePtr ifc;
        if (tuning_->GetManualGFXTuning(g, &ifc) != ADLX_OK || !ifc) return amd::Res::Failed;
        out = IADLXManualGraphicsTuning2Ptr(ifc);
        return out ? amd::Res::Ok : amd::Res::Unsupported;
    }

    // Likewise ManualVRAMTuning2 only (ManualVRAMTuning1 is state-based).
    amd::Res vram(int i, IADLXManualVRAMTuning2Ptr& out) {
        IADLXGPUPtr g = gpu(i);
        adlx_bool s = false;
        if (!g || !tuning_) return amd::Res::Unsupported;
        if (tuning_->IsSupportedManualVRAMTuning(g, &s) != ADLX_OK) return amd::Res::Failed;
        if (!s) return amd::Res::Unsupported;
        IADLXInterfacePtr ifc;
        if (tuning_->GetManualVRAMTuning(g, &ifc) != ADLX_OK || !ifc) return amd::Res::Failed;
        out = IADLXManualVRAMTuning2Ptr(ifc);
        return out ? amd::Res::Ok : amd::Res::Unsupported;
    }

    amd::Res autoTuning(int i, IADLXGPUAutoTuningPtr& out) {
        IADLXGPUPtr g = gpu(i);
        adlx_bool s = false;
        if (!g || !tuning_) return amd::Res::Unsupported;
        if (tuning_->IsSupportedAutoTuning(g, &s) != ADLX_OK) return amd::Res::Failed;
        if (!s) return amd::Res::Unsupported;
        IADLXInterfacePtr ifc;
        if (tuning_->GetAutoTuning(g, &ifc) != ADLX_OK || !ifc) return amd::Res::Failed;
        out = IADLXGPUAutoTuningPtr(ifc);
        return out ? amd::Res::Ok : amd::Res::Unsupported;
    }

    bool fetch(int i, amd::Toggle t, IADLX3DAntiLagPtr& x) { IADLXGPUPtr g = gpu(i); return g && settings_ && t == amd::Toggle::AntiLag && settings_->GetAntiLag(g, &x) == ADLX_OK && x; }
    bool fetch(int i, amd::Toggle t, IADLX3DChillPtr& x) { IADLXGPUPtr g = gpu(i); return g && settings_ && t == amd::Toggle::Chill && settings_->GetChill(g, &x) == ADLX_OK && x; }
    bool fetch(int i, amd::Toggle t, IADLX3DBoostPtr& x) { IADLXGPUPtr g = gpu(i); return g && settings_ && t == amd::Toggle::Boost && settings_->GetBoost(g, &x) == ADLX_OK && x; }

    // AMD Fluid Motion Frames: the newer services interface, for all GPUs at once.
    bool fetchAfmf(IADLX3DAMDFluidMotionFramesPtr& x) {
        if (!settings_) return false;
        IADLX3DSettingsServices1Ptr s1(settings_);
        return s1 && s1->GetAMDFluidMotionFrames(&x) == ADLX_OK && x;
    }

    amd::Res unsupportedOr(int i) { return gpu(i) ? amd::Res::Unsupported : amd::Res::Failed; }

    template <class T> static amd::Res read(T& x, bool& on) {
        adlx_bool s = false, e = false;
        if (x->IsSupported(&s) != ADLX_OK) return amd::Res::Failed;
        if (!s) return amd::Res::Unsupported;
        if (x->IsEnabled(&e) != ADLX_OK) return amd::Res::Failed;
        on = e;
        return amd::Res::Ok;
    }

    template <class T> static amd::Res write(T& x, bool on) {
        adlx_bool s = false;
        if (x->IsSupported(&s) != ADLX_OK) return amd::Res::Failed;
        if (!s) return amd::Res::Unsupported;
        return res(x->SetEnabled(on));
    }

    amd::Res current(int i, IADLXGPUMetricsSupportPtr& s, IADLXGPUMetricsPtr& c) {
        IADLXGPUPtr g = gpu(i);
        if (!g) return amd::Res::Failed;
        if (!perf_) return amd::Res::Unsupported;
        if (perf_->GetSupportedGPUMetrics(g, &s) != ADLX_OK || !s) return amd::Res::Failed;
        if (perf_->GetCurrentGPUMetrics(g, &c) != ADLX_OK || !c) return amd::Res::Failed;
        return amd::Res::Ok;
    }

    bool tried_ = false;
    amd::Status status_ = amd::Status::Failed;
    IADLXSystem* sys_ = nullptr;
    IADLXGPUListPtr gpus_;
    IADLXGPUTuningServicesPtr tuning_;
    IADLX3DSettingsServicesPtr settings_;
    IADLXPerformanceMonitoringServicesPtr perf_;
    std::string version_, driver_;
    AutoListener listener_;
};

}  // namespace

// Never destroyed: ADLX interfaces must not be released after ADLX is unloaded at process exit,
// and the auto tuning listener has to outlive every callback.
amd::Driver& amdDriver() {
    static Adlx* adlx = new Adlx;
    return *adlx;
}
