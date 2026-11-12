#include "Gpu.h"
#include <cstdio>

namespace sbfg {

static ComPtr<IDXGIFactory1> Factory() {
    ComPtr<IDXGIFactory1> f;
    Check(CreateDXGIFactory1(IID_PPV_ARGS(&f)), "CreateDXGIFactory1");
    return f;
}

// Integrated graphics share the computer's memory. The memory figure alone misread an AMD APU,
// which reports what the BIOS sets aside for it, often 512 MB or more.
static bool Integrated(IDXGIAdapter1* a, uint64_t dedicatedMB) {
    ComPtr<ID3D11Device> dev;
    D3D11_FEATURE_DATA_D3D11_OPTIONS2 o{};
    if (SUCCEEDED(D3D11CreateDevice(a, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
                                    &dev, nullptr, nullptr)) &&
        SUCCEEDED(dev->CheckFeatureSupport(D3D11_FEATURE_D3D11_OPTIONS2, &o, sizeof o)))
        return o.UnifiedMemoryArchitecture != FALSE;
    return dedicatedMB < 512;
}

std::vector<GpuInfo> EnumerateGpus(bool exactIntegrated) {
    std::vector<GpuInfo> out;
    auto f = Factory();
    ComPtr<IDXGIAdapter1> a;
    for (UINT i = 0; f->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; ++i, a.Reset()) {
        DXGI_ADAPTER_DESC1 d{};
        a->GetDesc1(&d);
        GpuInfo g;
        g.index = int(i);
        g.name = Narrow(d.Description);
        g.luid = d.AdapterLuid;
        g.dedicatedMB = uint64_t(d.DedicatedVideoMemory) / (1024 * 1024);
        g.software = (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
        g.integrated = !g.software && (exactIntegrated ? Integrated(a.Get(), g.dedicatedMB) : g.dedicatedMB < 512);
        ComPtr<IDXGIOutput> o;
        g.hasOutputs = SUCCEEDED(a->EnumOutputs(0, &o));
        if (!g.software) out.push_back(g);
    }
    return out;
}

std::string GpusJson(const std::vector<GpuInfo>& gpus) {
    std::string s = "{\"type\":\"gpus\",\"list\":[";
    for (size_t i = 0; i < gpus.size(); ++i) {
        const auto& g = gpus[i];
        char buf[512];
        std::snprintf(buf, sizeof buf,
                      "%s{\"index\":%d,\"name\":\"%s\",\"dedicatedMB\":%llu,\"integrated\":%s,\"hasOutputs\":%s}",
                      i ? "," : "", g.index, JsonEscape(g.name).c_str(),
                      (unsigned long long)g.dedicatedMB, g.integrated ? "true" : "false",
                      g.hasOutputs ? "true" : "false");
        s += buf;
    }
    return s + "]}";
}

int AdapterForWindow(HWND hwnd, const std::vector<GpuInfo>& gpus) {
    HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    auto f = Factory();
    ComPtr<IDXGIAdapter1> a;
    for (UINT i = 0; f->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; ++i, a.Reset()) {
        ComPtr<IDXGIOutput> o;
        for (UINT j = 0; a->EnumOutputs(j, &o) != DXGI_ERROR_NOT_FOUND; ++j, o.Reset()) {
            DXGI_OUTPUT_DESC od{};
            o->GetDesc(&od);
            if (od.Monitor == mon) {
                for (const auto& g : gpus) if (g.index == int(i)) return g.index;
            }
        }
    }
    return gpus.empty() ? -1 : gpus.front().index;
}

int PickGenGpu(const Settings& s, HWND game, const std::vector<GpuInfo>& gpus, std::string& why) {
    const int display = AdapterForWindow(game, gpus);
    switch (s.genGpu) {
    case GenGpu::Index:
        for (const auto& g : gpus)
            if (g.index == s.genGpuIndex) { why = "user selected"; return g.index; }
        why = "selected GPU not found, using display GPU";
        return display;
    case GenGpu::Integrated: {
        // Frame generation runs on the integrated GPU. The game's GPU only
        // renders; captured frames reach the iGPU through WGC (OS copy) or the
        // cross-adapter copy path. Useful mainly when the monitor is plugged
        // into the iGPU (laptops / motherboard output).
        for (const auto& g : gpus)
            if (g.integrated) { why = "integrated GPU"; return g.index; }
        why = "no integrated GPU found, using display GPU";
        return display;
    }
    default:
        why = "display GPU";
        return display;
    }
}

ComPtr<IDXGIAdapter1> AdapterByIndex(int index) {
    auto f = Factory();
    ComPtr<IDXGIAdapter1> a;
    Check(f->EnumAdapters1(UINT(index), &a), "EnumAdapters1");
    return a;
}

ComPtr<IDXGIAdapter1> WarpAdapter() {
    ComPtr<IDXGIFactory4> f;
    Check(Factory().As(&f), "IDXGIFactory4");
    ComPtr<IDXGIAdapter1> a;
    Check(f->EnumWarpAdapter(IID_PPV_ARGS(&a)), "EnumWarpAdapter");
    return a;
}

void CreateDevice(IDXGIAdapter1* adapter, ComPtr<ID3D11Device>& dev, ComPtr<ID3D11DeviceContext>& ctx) {
    const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
#ifdef SBFG_DEBUG_LAYER
    flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif
    D3D_FEATURE_LEVEL got{};
    Check(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags, levels, 2,
                            D3D11_SDK_VERSION, &dev, &got, &ctx),
          "D3D11CreateDevice");
    // keep the GPU thread responsive under heavy game load
    ComPtr<IDXGIDevice1> dxgi;
    if (SUCCEEDED(dev.As(&dxgi))) dxgi->SetMaximumFrameLatency(1);
}

// --- GPU scheduling priority --------------------------------------------------
// Windows gives the GPU to the game in front first. Generation is a few milliseconds of work per
// frame, but at the default priority it ran only in the gaps a GPU-bound game left: in recorded
// runs (Star Citizen, Once Human, Wardogs) one generated frame took 50 ms of GPU time from first
// command to last, Present waited 200 to 400 ms, and the screen showed 5 fps while the game alone
// made 35 to 135. With its own class raised, that little work runs when it is due and the game
// keeps the rest. Only this process is changed, never the game's. These are the calls OBS makes for
// the same reason (libobs-d3d11, set_priority), with its rule: the realtime class has hung GPUs
// under hardware-accelerated GPU scheduling. Auto never asks for more than high: the HwSchMode value
// in the registry is what the next boot uses, not what runs now (right after HAGS is switched off it
// reads off while it is still on), and realtime needs administrator rights anyway, so an ordinary
// start always got high. Only a test asks for realtime itself (--gpu-priority 5).

GpuPriority RaiseGpuPriority(ID3D11Device* dev, int wanted) {
    GpuPriority p;
    if (wanted == kGpuPriorityOff) return p;
    using SetFn = LONG(WINAPI*)(HANDLE, int);
    using GetFn = LONG(WINAPI*)(HANDLE, int*);
    HMODULE gdi = LoadLibraryW(L"gdi32.dll");
    const auto set = gdi ? reinterpret_cast<SetFn>(GetProcAddress(gdi, "D3DKMTSetProcessSchedulingPriorityClass")) : nullptr;
    const auto get = gdi ? reinterpret_cast<GetFn>(GetProcAddress(gdi, "D3DKMTGetProcessSchedulingPriorityClass")) : nullptr;
    constexpr int kNormal = 2, kHigh = 4;
    const int first = wanted >= 0 ? wanted : kHigh;
    const int last = wanted >= 0 ? wanted : kNormal + 1;    // auto: the highest class Windows grants
    for (int c = first; set && c >= last; --c) {
        if (set(GetCurrentProcess(), c) < 0) continue;      // NTSTATUS: negative = refused
        int got = c;
        if (get && get(GetCurrentProcess(), &got) >= 0 && got != c) continue;
        p.cls = c;
        break;
    }
    if (p.cls > kNormal) {
        ComPtr<IDXGIDevice> dxgi;
        if (dev && SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&dxgi)))) p.thread = SUCCEEDED(dxgi->SetGPUThreadPriority(7));
    }
    return p;
}

}  // namespace sbfg
