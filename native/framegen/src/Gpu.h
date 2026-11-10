// Adapter enumeration and selection (dual GPU support).
#pragma once
#include "Win.h"
#include "Settings.h"
#include <vector>

namespace sbfg {

struct GpuInfo {
    int index = -1;
    std::string name;
    LUID luid{};
    uint64_t dedicatedMB = 0;
    bool integrated = false;   // shares the computer's memory (exact only when asked for: see below)
    bool software = false;
    bool hasOutputs = false;   // a monitor is connected to this adapter
};

// Telling integrated graphics apart opens each adapter, about 0.25 s, so only on request;
// otherwise integrated is a guess from the memory figure.
std::vector<GpuInfo> EnumerateGpus(bool exactIntegrated);
std::string GpusJson(const std::vector<GpuInfo>& gpus);

// Adapter that drives the monitor the window is on.
int AdapterForWindow(HWND hwnd, const std::vector<GpuInfo>& gpus);

// Picks the frame generation adapter. Returns index into gpus.
int PickGenGpu(const Settings& s, HWND game, const std::vector<GpuInfo>& gpus, std::string& why);

ComPtr<IDXGIAdapter1> AdapterByIndex(int index);
ComPtr<IDXGIAdapter1> WarpAdapter();
void CreateDevice(IDXGIAdapter1* adapter, ComPtr<ID3D11Device>& dev, ComPtr<ID3D11DeviceContext>& ctx);

// The GPU scheduling priority this process holds after RaiseGpuPriority.
struct GpuPriority {
    int cls = -1;          // D3DKMT_SCHEDULINGPRIORITYCLASS 0..5, -1 = left as it was
    bool thread = false;   // the device's GPU thread priority was raised too
    const char* name() const {
        static const char* const names[] = {"idle", "below normal", "normal", "above normal", "high", "realtime"};
        return cls >= 0 && cls <= 5 ? names[cls] : "unchanged";
    }
};
// wanted: kGpuPriorityAuto, kGpuPriorityOff, or a class 0..5 (tests). Changes this process only.
GpuPriority RaiseGpuPriority(ID3D11Device* dev, int wanted);

}  // namespace sbfg
