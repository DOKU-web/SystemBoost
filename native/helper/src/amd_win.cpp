// Windows side of amd.cpp: the clock, the driver fault log and the watch thread.
#include "amd.h"
#include "commands.h"
#include <windows.h>
#include <thread>

void amdSetup() {
    amd::Hooks h;
    h.nowMs = [] { return (unsigned long long)GetTickCount64(); };
    h.sleepMs = [](unsigned ms) { Sleep(ms); };
    h.utcNow = [] { return utcNow(); };
    h.faultSince = [](const std::string& utc) { return amdDriverFaultSince(utc); };
    // started with the first tuning, like gpuoc.cpp's watch; one sample per second
    h.startWatch = [] {
        std::thread([] {
            for (;;) {
                Sleep(1000);
                amd::tick();
            }
        }).detach();
    };
    amd::install(&amdDriver(), std::move(h));
}

void amdShutdown() { amd::shutdown(); }
