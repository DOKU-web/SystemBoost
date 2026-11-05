#include "Gpu.h"
#include <cstdio>

namespace sbfg {

static ComPtr<IDXGIFactory1> Factory() {
    ComPtr<IDXGIFactory1> f;
    Check(CreateDXGIFactory1(IID_PPV_ARGS(&f)), "CreateDXGIFactory1");
    return f;
}

std::vector<GpuInfo> EnumerateGpus() {
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
        g.integrated = !g.software && g.dedicatedMB < 512;
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

}  // namespace sbfg
