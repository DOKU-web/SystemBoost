// AMD Radeon commands of the helper (see amd.cpp). The replies are the pipe's "ok\t<data>" /
// "err\t<word>" lines. Arguments arrive already checked by validate.h; the ranges that ADLX
// reports are checked here.
#pragma once
#include "amd_driver.h"
#include <functional>
#include <optional>
#include <string>

namespace amd {

// What amd.cpp needs from the platform; tests give a fake clock and fault log.
struct Hooks {
    std::function<unsigned long long()> nowMs;                     // monotonic milliseconds
    std::function<void(unsigned)> sleepMs;
    std::function<std::string()> utcNow;                           // for the driver fault check
    std::function<bool(const std::string& utc)> faultSince;        // driverlog.cpp
    std::function<void()> startWatch;                              // a thread that calls tick() every second
};

const int kLeave = -1;                         // '-' in a request: leave this one alone
const unsigned long long kConfirmMs = 30000;   // the app confirms with amd.tuning.keep within this
const unsigned long long kAutoWaitMs = 60000;  // ADLX's auto tuning callback, at most
const int kHotSamples = 3;                     // one temperature reading per second
const int kFaultCheckSeconds = 2;
const int kAutoTempLimit = 83;                 // amd.auto.apply has no limit of its own: the app's default
const int kMaxRaise[3] = {5, 100, 50};         // one apply raises power by at most 5 %, GPU max 100 MHz, VRAM max 50 MHz

// Sets the driver and the hooks and forgets every earlier state (start of the helper, and each test).
void install(Driver* driver, Hooks hooks);

std::string info();
std::string metrics(int gpu);
std::string profileGet(int gpu);
std::string profileApply(int gpu, int antiLag, int chill);   // each 0, 1 or kLeave
std::string profileRevert(int gpu, int antiLagOld, int chillOld, int antiLagSet, int chillSet);
std::string tuningApply(int gpu, std::optional<int> powerPct, std::optional<int> gpuMaxMHz, std::optional<int> vramMaxMHz, int tempLimit);
std::string autoApply(int gpu, AutoMode mode);
std::string tuningKeep();
std::string tuningStatus();
std::string tuningRevert(int gpu, std::optional<int> powerPct, std::optional<int> gpuMaxMHz, std::optional<int> vramMaxMHz);
std::string tuningReset(int gpu);

// One watch sample; the watch thread calls it every second.
void tick();
// The helper is exiting: an active tuning gets its backup back, an applied 3D setting is undone.
void shutdown();

}  // namespace amd
