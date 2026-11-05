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

// nvidia.cpp: per-game power management mode through NVAPI driver settings
std::string nvGet(const std::string& exe);
std::string nvSet(const std::string& exe);
std::string nvRestore(const std::string& exe, bool created, const std::string& old);
