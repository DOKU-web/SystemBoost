#pragma once
#include <string>
#include <vector>

// Runs one request and returns the reply line (without the newline).
std::string dispatch(const std::vector<std::string>& fields);

std::wstring widen(const std::string& utf8);
std::string narrow(const std::wstring& wide);

// netpower.cpp: "allow the computer to turn off this device" on physical network adapters
std::string netPowerList();
std::string netPowerSet(const std::string& instance, bool enable);

// nettop.cpp: TCP bytes per process
std::string netTop();

// nvidia.cpp: per-game power management mode and frame limit through NVAPI driver settings
std::string nvGet(const std::string& exe);
std::string nvSet(const std::string& exe);
std::string nvRestore(const std::string& exe, const std::string& old);
std::string nvLimitSet(const std::string& exe, unsigned fps);
std::string nvLimitRestore(const std::string& exe, const std::string& old);
void nvLimitShutdown();

// gpuoc.cpp: power limit and clock offsets through NVML, with automatic undo
std::string ocSet(int powerW, int coreMHz, int memMHz, int tempLimit);
std::string ocKeep();
std::string ocStatus();
std::string ocRestore(unsigned powerMw, int coreMHz, int memMHz);
void ocShutdown();

// driverlog.cpp: display driver faults in the System log
std::string utcNow();
bool driverFaultSince(const std::string& utc);

// fpsmeter.cpp: frame timing from Windows event tracing
std::string fpsStart();
std::string fpsMark();
std::string fpsRead(unsigned long pid);
std::string fpsStop();
void fpsShutdown();
