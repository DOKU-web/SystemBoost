// Display driver faults as Windows records them in the System log: "display driver
// stopped responding" (Display 4101) and NVIDIA's own engine errors (nvlddmkm 13, 14, 153).
#include "commands.h"
#include <windows.h>
#include <winevt.h>
#include <cstdio>

std::string utcNow() {
    SYSTEMTIME t;
    GetSystemTime(&t);
    char b[32];
    snprintf(b, sizeof b, "%04d-%02d-%02dT%02d:%02d:%02dZ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return b;
}

bool driverFaultSince(const std::string& utc) {
    std::wstring query =
        L"*[System[Provider[@Name='Display' or @Name='nvlddmkm'] and "
        L"(EventID=4101 or EventID=13 or EventID=14 or EventID=153) and "
        L"TimeCreated[@SystemTime>='" + widen(utc) + L"']]]";
    EVT_HANDLE results = EvtQuery(nullptr, L"System", query.c_str(), EvtQueryChannelPath | EvtQueryForwardDirection);
    if (!results) return false;
    EVT_HANDLE event = nullptr;
    DWORD got = 0;
    bool found = EvtNext(results, 1, &event, 0, 0, &got) && got == 1;
    if (event) EvtClose(event);
    EvtClose(results);
    return found;
}
