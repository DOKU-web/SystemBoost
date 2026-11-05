// Checks for everything that crosses the pipe. No Windows headers here, so the
// rules can be unit-tested on any platform (tests/validate_test.cpp).
#pragma once
#include <algorithm>
#include <cctype>
#include <cstddef>
#include <string>
#include <vector>

namespace sb {

inline std::vector<std::string> split(const std::string& line) {
    std::vector<std::string> out;
    size_t start = 0;
    for (;;) {
        size_t tab = line.find('\t', start);
        if (tab == std::string::npos) { out.push_back(line.substr(start)); return out; }
        out.push_back(line.substr(start, tab - start));
        start = tab + 1;
    }
}

inline std::string lower(std::string s) {
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return (char)std::tolower(c); });
    return s;
}

inline bool isHex(const std::string& s, size_t len) {
    return s.size() == len && std::all_of(s.begin(), s.end(), [](unsigned char c) {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
    });
}

/// A bare file name ending in .exe. Paths and the programs Windows depends on are refused.
inline bool isExeName(const std::string& s) {
    if (s.size() < 5 || s.size() > 180) return false;
    for (unsigned char c : s) {
        if (c < 0x20 || c == 0x7f) return false;
        if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return false;
    }
    std::string l = lower(s);
    if (l.compare(l.size() - 4, 4, ".exe") != 0 || l[0] == '.' || l[0] == ' ') return false;
    static const char* const denied[] = {
        "csrss.exe", "lsass.exe", "smss.exe", "wininit.exe", "winlogon.exe", "services.exe", "svchost.exe",
        "dwm.exe", "explorer.exe", "ntoskrnl.exe", "spoolsv.exe", "conhost.exe", "fontdrvhost.exe", "audiodg.exe",
        "systemboost.exe", "sb_helper.exe", "sb_framegen.exe",
    };
    for (const char* d : denied) if (l == d) return false;
    return true;
}

/// PerfOptions\CpuPriorityClass values: 1 idle, 2 normal, 3 high, 5 below normal, 6 above normal.
/// 4 is realtime and is never allowed.
inline bool isPriorityClass(const std::string& s) {
    return s == "1" || s == "2" || s == "3" || s == "5" || s == "6";
}

inline bool isHagsMode(const std::string& s) { return s == "1" || s == "2"; }

inline bool isWuName(const std::string& s) {
    static const char* const names[] = {
        "PauseUpdatesStartTime", "PauseUpdatesExpiryTime", "PauseFeatureUpdatesStartTime",
        "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime",
    };
    for (const char* n : names) if (s == n) return true;
    return false;
}

/// 2026-11-05T14:00:00Z
inline bool isIsoTime(const std::string& s) {
    static const char pattern[] = "dddd-dd-ddTdd:dd:ddZ";
    if (s.size() != sizeof(pattern) - 1) return false;
    for (size_t i = 0; i < s.size(); i++) {
        if (pattern[i] == 'd' ? !std::isdigit((unsigned char)s[i]) : s[i] != pattern[i]) return false;
    }
    return true;
}

inline bool isBit(const std::string& s) { return s == "0" || s == "1"; }

/// Value that was in the driver profile before: "none" or a decimal number.
inline bool isOldValue(const std::string& s) {
    if (s == "none") return true;
    return !s.empty() && s.size() <= 10 && std::all_of(s.begin(), s.end(), [](unsigned char c) { return std::isdigit(c); });
}

}  // namespace sb
