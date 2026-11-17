// The few ADLX calls the AMD commands need, behind an interface: the rules and the watch in amd.cpp
// work on this only, so tests/amd_test.cpp runs them with a fake driver on any platform. The real one
// is amd_adlx.cpp (Windows, AMD's ADLX). No Windows or ADLX headers here.
#pragma once
#include <string>

namespace amd {

enum class Status { Ok, NoDll, OldDriver, NoGpu, Failed };

// Unsupported: ADLX's IsSupported* (or the interface query) says no. ResetNeeded: auto tuning answered
// ADLX_RESET_NEEDED (a one-click tuning of the user's is on).
enum class Res { Ok, Unsupported, Failed, ResetNeeded };

// Power limit in percent (ManualPowerTuning), GPU max clock and VRAM max clock in MHz
// (ManualGraphicsTuning2, ManualVRAMTuning2). The order is the order on the pipe.
enum class Knob { Power = 0, GpuMax = 1, VramMax = 2 };

// AntiLag, Chill, Boost per GPU; AMD Fluid Motion Frames for all GPUs (3DSettingsServices1).
// ADLX's 3D settings are global: there are no per-game profiles in ADLX.
enum class Toggle { AntiLag, Chill, Boost, Afmf };

enum class AutoMode { Undervolt, OcGpu, OcVram };

struct Range { int min = 0, max = 0, step = 0; };

// -1: ADLX does not support that metric on this GPU.
struct Metrics { int tempC = -1, hotspotC = -1, gpuMHz = -1, vramMHz = -1, powerW = -1, usagePct = -1, vramUsedMB = -1; };

class Driver {
public:
    virtual ~Driver() = default;

    // Loads and starts ADLX the first time; later calls give the same answer.
    virtual Status open() = 0;
    virtual std::string adlxVersion() = 0;     // "" when there is no ADLX
    virtual std::string driverVersion() = 0;   // "" when unknown
    virtual int gpuCount() = 0;
    virtual std::string gpuName(int gpu) = 0;
    virtual bool integrated(int gpu) = 0;

    virtual Res atFactory(int gpu, bool& factory) = 0;
    virtual Res resetToFactory(int gpu) = 0;
    // Unsupported for a GPU that only has the older state-based tuning (ManualGraphicsTuning1 /
    // ManualVRAMTuning1): no single max clock exists there.
    virtual Res range(int gpu, Knob k, Range& r) = 0;
    virtual Res get(int gpu, Knob k, int& value) = 0;
    virtual Res set(int gpu, Knob k, int value) = 0;

    virtual bool autoSupported(int gpu, AutoMode m) = 0;
    // Starts ADLX auto tuning and returns at once; autoDone() turns true when ADLX calls back.
    virtual Res autoStart(int gpu, AutoMode m) = 0;
    virtual bool autoDone() = 0;

    virtual Res toggleGet(int gpu, Toggle t, bool& on) = 0;
    virtual Res toggleSet(int gpu, Toggle t, bool on) = 0;

    virtual Res metrics(int gpu, Metrics& m) = 0;
    virtual Res temperature(int gpu, int& celsius) = 0;   // edge temperature (GPUTemperature)
};

}  // namespace amd
