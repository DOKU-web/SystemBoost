// Frame timing of a running game from Windows event tracing, the same source PresentMon
// uses. Windows reports every Present call, so the game is never opened or touched.
#include "commands.h"
#include <windows.h>
#include <evntcons.h>
#include <evntrace.h>
#include <algorithm>
#include <cstddef>
#include <cstdio>
#include <initializer_list>
#include <map>
#include <mutex>
#include <thread>
#include <vector>

namespace {

const wchar_t kSession[] = L"SystemBoostFps";
const GUID kDxgi = {0xCA11C036, 0x0102, 0x4A2D, {0xA6, 0xAD, 0xF0, 0x3C, 0xFE, 0xD5, 0xD3, 0xC9}};
const GUID kD3d9 = {0x783ACA0A, 0x790E, 0x4D7F, {0x84, 0x51, 0xAA, 0x85, 0x05, 0x11, 0xC6, 0xB9}};
const GUID kDxgKrnl = {0x802EC45A, 0x1E99, 0x4B83, {0x99, 0x20, 0x87, 0xC9, 0x82, 0x77, 0xBA, 0x9D}};
const USHORT kDxgiPresent = 42, kDxgiPresentOverlay = 55, kD3d9Present = 1, kKernelPresent = 184;
const size_t kSources = 3;                // preferred first: one frame shows up in several of them
const size_t kMaxFrames = 200000;
const size_t kMinFrames = 10;
const double kTicksPerSecond = 1e7;

struct Frames { std::vector<LONGLONG> bySource[kSources]; };

struct Properties {
    EVENT_TRACE_PROPERTIES p;
    wchar_t name[64];
};

std::mutex gLock;
std::map<DWORD, Frames> gFrames;
TRACEHANDLE gSessionHandle = 0;
TRACEHANDLE gConsumer = INVALID_PROCESSTRACE_HANDLE;
std::thread gThread;

Properties makeProperties() {
    Properties x{};
    x.p.Wnode.BufferSize = sizeof x;
    x.p.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
    x.p.Wnode.ClientContext = 1;
    x.p.LogFileMode = EVENT_TRACE_REAL_TIME_MODE;
    x.p.LoggerNameOffset = offsetof(Properties, name);
    x.p.FlushTimer = 1;
    return x;
}

void WINAPI onEvent(PEVENT_RECORD e) {
    const GUID& provider = e->EventHeader.ProviderId;
    USHORT id = e->EventHeader.EventDescriptor.Id;
    int source = -1;
    if (IsEqualGUID(provider, kDxgi) && (id == kDxgiPresent || id == kDxgiPresentOverlay)) source = 0;
    else if (IsEqualGUID(provider, kD3d9) && id == kD3d9Present) source = 1;
    else if (IsEqualGUID(provider, kDxgKrnl) && id == kKernelPresent) source = 2;
    if (source < 0) return;
    std::lock_guard<std::mutex> hold(gLock);
    auto& list = gFrames[e->EventHeader.ProcessId].bySource[source];
    if (list.size() < kMaxFrames) list.push_back(e->EventHeader.TimeStamp.QuadPart);
}

bool enable(const GUID& provider, std::initializer_list<USHORT> ids) {
    BYTE buffer[offsetof(EVENT_FILTER_EVENT_ID, Events) + 8 * sizeof(USHORT)] = {};
    auto* filter = (EVENT_FILTER_EVENT_ID*)buffer;
    filter->FilterIn = TRUE;
    filter->Count = (USHORT)ids.size();
    std::copy(ids.begin(), ids.end(), filter->Events);
    EVENT_FILTER_DESCRIPTOR d{};
    d.Ptr = (ULONGLONG)filter;
    d.Size = (ULONG)(offsetof(EVENT_FILTER_EVENT_ID, Events) + ids.size() * sizeof(USHORT));
    d.Type = EVENT_FILTER_TYPE_EVENT_ID;
    ENABLE_TRACE_PARAMETERS p{};
    p.Version = ENABLE_TRACE_PARAMETERS_VERSION_2;
    p.EnableFilterDesc = &d;
    p.FilterDescCount = 1;
    return EnableTraceEx2(gSessionHandle, &provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION,
                          ~0ULL, 0, 0, &p) == ERROR_SUCCESS;
}

}  // namespace

std::string fpsStop() {
    Properties x = makeProperties();
    ControlTraceW(0, kSession, &x.p, EVENT_TRACE_CONTROL_STOP);   // also clears one left by a killed helper
    gSessionHandle = 0;
    if (gConsumer != INVALID_PROCESSTRACE_HANDLE) { CloseTrace(gConsumer); gConsumer = INVALID_PROCESSTRACE_HANDLE; }
    if (gThread.joinable()) gThread.join();
    std::lock_guard<std::mutex> hold(gLock);
    gFrames.clear();
    return "ok\t";
}

std::string fpsStart() {
    fpsStop();
    Properties x = makeProperties();
    ULONG r = StartTraceW(&gSessionHandle, kSession, &x.p);
    if (r != ERROR_SUCCESS) return "err\tsession " + std::to_string(r);
    bool any = enable(kDxgi, {kDxgiPresent, kDxgiPresentOverlay});
    any = enable(kD3d9, {kD3d9Present}) || any;
    any = enable(kDxgKrnl, {kKernelPresent}) || any;
    if (!any) { fpsStop(); return "err\tproviders"; }
    EVENT_TRACE_LOGFILEW log{};
    log.LoggerName = (LPWSTR)kSession;
    log.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
    log.EventRecordCallback = onEvent;
    gConsumer = OpenTraceW(&log);
    if (gConsumer == INVALID_PROCESSTRACE_HANDLE) { fpsStop(); return "err\tconsumer " + std::to_string(GetLastError()); }
    gThread = std::thread([] { ProcessTrace(&gConsumer, 1, nullptr, nullptr); });
    return "ok\t";
}

std::string fpsMark() {
    std::lock_guard<std::mutex> hold(gLock);
    gFrames.clear();
    return "ok\t";
}

/// frames;seconds;average fps;1% low fps since the last mark.
std::string fpsRead(DWORD pid) {
    std::vector<LONGLONG> t;
    {
        std::lock_guard<std::mutex> hold(gLock);
        auto it = gFrames.find(pid);
        if (it == gFrames.end()) return "ok\t0;0;0;0";
        for (auto& list : it->second.bySource) if (!list.empty()) { t = list; break; }
    }
    if (t.size() < kMinFrames) return "ok\t0;0;0;0";
    std::sort(t.begin(), t.end());
    double seconds = (t.back() - t.front()) / kTicksPerSecond;
    std::vector<LONGLONG> gaps;
    for (size_t i = 1; i < t.size(); i++) gaps.push_back(t[i] - t[i - 1]);
    std::sort(gaps.rbegin(), gaps.rend());
    size_t worst = (std::max)((size_t)1, gaps.size() / 100);
    double total = 0;
    for (size_t i = 0; i < worst; i++) total += (double)gaps[i];
    char b[96];
    snprintf(b, sizeof b, "ok\t%zu;%.2f;%.2f;%.2f", t.size(), seconds, (t.size() - 1) / seconds,
             worst * kTicksPerSecond / total);
    return b;
}

void fpsShutdown() {
    if (gSessionHandle != 0) fpsStop();
}
