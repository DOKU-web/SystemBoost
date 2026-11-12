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

/// A whole decimal number from lo to hi.
inline bool isInt(const std::string& s, long long lo, long long hi) {
    size_t digits = !s.empty() && s[0] == '-' ? 1 : 0;
    if (s.size() == digits || s.size() > 11) return false;
    long long v = 0;
    for (size_t i = digits; i < s.size(); i++) {
        if (!std::isdigit((unsigned char)s[i])) return false;
        v = v * 10 + (s[i] - '0');
    }
    if (digits) v = -v;
    return v >= lo && v <= hi;
}

// What the app may ask of the GPU: much tighter than the driver's own limits.
inline bool isCoreOffset(const std::string& s) { return isInt(s, -200, 100); }
inline bool isMemOffset(const std::string& s) { return isInt(s, -500, 300); }
inline bool isTempLimit(const std::string& s) { return isInt(s, 40, 87); }
inline bool isPowerWatts(const std::string& s) { return isInt(s, 10, 1000); }
inline bool isFrameLimit(const std::string& s) { return isInt(s, 10, 300); }

/// A page file size in MB: 1 to 8 digits, at most 16 TB. No sign.
inline bool isPagefileSize(const std::string& s, long long& v) {
    if (s.empty() || s.size() > 8) return false;
    v = 0;
    for (unsigned char c : s) {
        if (!std::isdigit(c)) return false;
        v = v * 10 + (c - '0');
    }
    return v <= 16777216;
}

/// One line of PagingFiles: "?:\pagefile.sys" (Windows manages every drive), or "C:\pagefile.sys" on one drive,
/// optionally followed by " initial maximum" in MB ("0 0" = Windows sizes it there). The app checks the same rule
/// (RestoreRules.IsPagefileLine).
inline bool isPagefileLine(const std::string& s) {
    if (s.size() < 15) return false;
    unsigned char d = (unsigned char)s[0];
    bool any = d == '?';
    if (!any && !((d >= 'A' && d <= 'Z') || (d >= 'a' && d <= 'z'))) return false;
    if (lower(s.substr(1, 14)) != ":\\pagefile.sys") return false;
    std::string rest = s.substr(15);
    if (rest.empty()) return true;
    if (any || rest[0] != ' ') return false;
    size_t sp = rest.find(' ', 1);
    if (sp == std::string::npos) return false;
    long long a, b;
    if (!isPagefileSize(rest.substr(1, sp - 1), a) || !isPagefileSize(rest.substr(sp + 1), b)) return false;
    return (a == 0 && b == 0) || (a > 0 && a <= b);
}

/// The whole PagingFiles value of a pf.set request, from field `from` on: one line per drive, at most 26,
/// "?:" only on its own. "none" alone stands for an empty list (only putting such an original back sends it).
inline bool isPagefileList(const std::vector<std::string>& f, size_t from) {
    if (f.size() <= from) return false;
    size_t n = f.size() - from;
    if (n == 1 && f[from] == "none") return true;
    if (n > 26) return false;
    bool seen[27] = {};
    for (size_t i = from; i < f.size(); i++) {
        if (!isPagefileLine(f[i])) return false;
        char d = (char)std::toupper((unsigned char)f[i][0]);
        if (d == '?' && n > 1) return false;
        int slot = d == '?' ? 26 : d - 'A';
        if (seen[slot]) return false;
        seen[slot] = true;
    }
    return true;
}

/// Value that was in the driver profile before: "none" or a decimal number.
inline bool isOldValue(const std::string& s) {
    if (s == "none") return true;
    return !s.empty() && s.size() <= 10 && std::all_of(s.begin(), s.end(), [](unsigned char c) { return std::isdigit(c); });
}

}  // namespace sb
