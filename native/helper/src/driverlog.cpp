// Display driver faults as Windows records them in the System log: "display driver
// stopped responding" (Display 4101, for every vendor's driver) and NVIDIA's own engine errors
// (nvlddmkm 13, 14, 153).
// AMD (amdDriverFaultSince, for amd.cpp's watch only, so an AMD iGPU never trips the NVIDIA watch):
// Display 4101, and errors and critical events of AMD's kernel display driver under its service
// names (amdkmdag for current Radeon drivers, amdkmdap and amdwddmg for older and APU drivers).
// UNVERIFIED: AMD does not document its event IDs and this has not been seen on a real Radeon PC,
// so it takes every Error/Critical event of those providers instead of chosen IDs. Both queries were
// checked on Windows 11 with wevtutil (accepted; the NVIDIA one finds the same events as before).
#include "commands.h"
#include <windows.h>
#include <winevt.h>
#include <cstdio>

namespace {

bool anySince(const wchar_t* filter, const std::string& utc) {
    std::wstring query = L"*[System[(" + std::wstring(filter) + L") and TimeCreated[@SystemTime>='" + widen(utc) + L"']]]";
    EVT_HANDLE results = EvtQuery(nullptr, L"System", query.c_str(), EvtQueryChannelPath | EvtQueryForwardDirection);
    if (!results) return false;
    EVT_HANDLE event = nullptr;
    DWORD got = 0;
    bool found = EvtNext(results, 1, &event, 0, 0, &got) && got == 1;
    if (event) EvtClose(event);
    EvtClose(results);
    return found;
}

}  // namespace

std::string utcNow() {
    SYSTEMTIME t;
    GetSystemTime(&t);
    char b[32];
    snprintf(b, sizeof b, "%04d-%02d-%02dT%02d:%02d:%02dZ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return b;
}

bool driverFaultSince(const std::string& utc) {
    return anySince(L"Provider[@Name='Display' or @Name='nvlddmkm'] and "
                    L"(EventID=4101 or EventID=13 or EventID=14 or EventID=153)", utc);
}

bool amdDriverFaultSince(const std::string& utc) {
    return anySince(L"(Provider[@Name='Display'] and EventID=4101) or "
                    L"(Provider[@Name='amdkmdag' or @Name='amdkmdap' or @Name='amdwddmg'] and (Level=1 or Level=2))", utc);
}
