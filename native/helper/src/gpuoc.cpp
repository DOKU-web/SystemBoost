// GPU tuning through NVML, the management library NVIDIA ships with its driver.
// What is changed here goes back by itself when the app does not confirm in time,
// the GPU runs too hot, the driver reports a fault, or the helper exits.
#include "commands.h"
#include <windows.h>
#include <cstdio>
#include <mutex>
#include <thread>

namespace {

using Device = void*;

struct ClockOffset { unsigned version; int type; int pstate; int offsetMHz; int minMHz; int maxMHz; };
const unsigned kClockOffsetV1 = (unsigned)sizeof(ClockOffset) | (1u << 24);
const int kClockGraphics = 0;
const int kClockMemory = 2;
const int kPstateTop = 0;
const int kTemperatureGpu = 0;

const ULONGLONG kConfirmMs = 30000;
const int kHotSamples = 3;              // one reading per second
const int kFaultCheckSeconds = 2;

struct Nvml {
    int (*init)();
    int (*handle)(unsigned, Device*);
    int (*getOffset)(Device, ClockOffset*);
    int (*setOffset)(Device, ClockOffset*);
    int (*getPower)(Device, unsigned*);
    int (*setPower)(Device, unsigned);
    int (*powerRange)(Device, unsigned*, unsigned*);
    int (*temperature)(Device, int, unsigned*);
    Device device;
};

template <class T> bool bind(HMODULE m, T& fn, const char* name) {
    fn = (T)GetProcAddress(m, name);
    return fn != nullptr;
}

Nvml* nvml() {
    static Nvml n{};
    static bool ready = [] {
        HMODULE m = LoadLibraryExW(L"nvml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        return m && bind(m, n.init, "nvmlInit_v2") && bind(m, n.handle, "nvmlDeviceGetHandleByIndex_v2") &&
               bind(m, n.getOffset, "nvmlDeviceGetClockOffsets") && bind(m, n.setOffset, "nvmlDeviceSetClockOffsets") &&
               bind(m, n.getPower, "nvmlDeviceGetPowerManagementLimit") && bind(m, n.setPower, "nvmlDeviceSetPowerManagementLimit") &&
               bind(m, n.powerRange, "nvmlDeviceGetPowerManagementLimitConstraints") && bind(m, n.temperature, "nvmlDeviceGetTemperature") &&
               n.init() == 0 && n.handle(0, &n.device) == 0;
    }();
    return ready ? &n : nullptr;
}

struct Values {
    unsigned powerMw;
    int coreMHz;
    int memMHz;
    bool operator==(const Values& o) const { return powerMw == o.powerMw && coreMHz == o.coreMHz && memMHz == o.memMHz; }
};

int readOffset(Nvml& n, int type, int& out) {
    ClockOffset o{kClockOffsetV1, type, kPstateTop};
    int r = n.getOffset(n.device, &o);
    out = o.offsetMHz;
    return r;
}

int readValues(Nvml& n, Values& v) {
    int r = n.getPower(n.device, &v.powerMw);
    if (r == 0) r = readOffset(n, kClockGraphics, v.coreMHz);
    if (r == 0) r = readOffset(n, kClockMemory, v.memMHz);
    return r;
}

int writeOffset(Nvml& n, int type, int mhz) {
    ClockOffset o{kClockOffsetV1, type, kPstateTop, mhz};
    return n.setOffset(n.device, &o);
}

int writeValues(Nvml& n, const Values& v) {
    int r = n.setPower(n.device, v.powerMw);
    int c = writeOffset(n, kClockGraphics, v.coreMHz);
    int m = writeOffset(n, kClockMemory, v.memMHz);
    return r ? r : c ? c : m;
}

std::mutex gLock;
bool gActive = false;
bool gConfirmed = false;
bool gWatching = false;
Values gBackup{};
ULONGLONG gConfirmBy = 0;
unsigned gTempLimit = 0;
std::string gTripped;
std::string gSince;

void undo(Nvml& n, const std::string& reason) {
    writeValues(n, gBackup);
    gActive = false;
    gTripped = reason;
}

void watch(Nvml& n) {
    int hot = 0, seconds = 0;
    for (;;) {
        Sleep(1000);
        std::lock_guard<std::mutex> hold(gLock);
        if (!gActive) { hot = 0; continue; }
        if (!gConfirmed && GetTickCount64() > gConfirmBy) { undo(n, "timeout"); continue; }
        unsigned t = 0;
        if (n.temperature(n.device, kTemperatureGpu, &t) != 0) { undo(n, "driver"); continue; }
        hot = t >= gTempLimit ? hot + 1 : 0;
        if (hot >= kHotSamples) { undo(n, "temp:" + std::to_string(t)); continue; }
        if (++seconds % kFaultCheckSeconds == 0 && driverFaultSince(gSince)) undo(n, "driver");
    }
}

}  // namespace

std::string ocSet(int powerW, int coreMHz, int memMHz, int tempLimit) {
    Nvml* n = nvml();
    if (!n) return "err\tnvml";
    std::lock_guard<std::mutex> hold(gLock);
    unsigned lo = 0, hi = 0, mw = (unsigned)powerW * 1000;
    int r = n->powerRange(n->device, &lo, &hi);
    if (r != 0) return "err\tpower range " + std::to_string(r);
    if (mw < lo || mw > hi) return "err\tpower out of range";
    if (!gActive && (r = readValues(*n, gBackup)) != 0) return "err\tread " + std::to_string(r);
    Values want{mw, coreMHz, memMHz}, got{};
    r = writeValues(*n, want);
    if (r == 0) r = readValues(*n, got);
    if (r != 0 || !(got == want)) {
        writeValues(*n, gBackup);
        gActive = false;
        return "err\tnot applied " + std::to_string(r);
    }
    gActive = true;
    gConfirmed = false;
    gConfirmBy = GetTickCount64() + kConfirmMs;
    gTempLimit = (unsigned)tempLimit;
    gTripped.clear();
    gSince = utcNow();
    if (!gWatching) {
        gWatching = true;
        std::thread(watch, std::ref(*n)).detach();
    }
    return "ok\t";
}

std::string ocKeep() {
    std::lock_guard<std::mutex> hold(gLock);
    if (!gActive) return "err\tnot active";
    gConfirmed = true;
    return "ok\t";
}

std::string ocStatus() {
    Nvml* n = nvml();
    if (!n) return "err\tnvml";
    std::lock_guard<std::mutex> hold(gLock);
    unsigned t = 0;
    n->temperature(n->device, kTemperatureGpu, &t);
    return "ok\t" + std::string(gActive ? "1" : "0") + ";" + (gConfirmed ? "1" : "0") + ";" + std::to_string(t) + ";" + gTripped;
}

std::string ocRestore(unsigned powerMw, int coreMHz, int memMHz) {
    Nvml* n = nvml();
    if (!n) return "err\tnvml";
    std::lock_guard<std::mutex> hold(gLock);
    unsigned lo = 0, hi = 0;
    int r = n->powerRange(n->device, &lo, &hi);
    if (r != 0) return "err\tpower range " + std::to_string(r);
    if (powerMw < lo || powerMw > hi) return "err\tpower out of range";
    gActive = false;
    gTripped.clear();
    Values want{powerMw, coreMHz, memMHz}, got{};
    r = writeValues(*n, want);
    if (r == 0) r = readValues(*n, got);
    if (r != 0 || !(got == want)) return "err\tnot restored " + std::to_string(r);
    return "ok\t";
}

void ocShutdown() {
    Nvml* n = nvml();
    if (!n) return;
    std::lock_guard<std::mutex> hold(gLock);
    if (gActive) writeValues(*n, gBackup);
    gActive = false;
}
