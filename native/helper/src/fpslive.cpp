// Live frame rate for the on-screen FPS display. Same source as fpsmeter.cpp and PresentMon:
// Windows reports every Present call through event tracing, so the game is never opened or
// touched. It has its own tracing session, so a measurement can start and stop beside it, and
// it keeps only the last few seconds of each process.
#include "commands.h"
#include <windows.h>
#include <evntcons.h>
#include <evntrace.h>
#include <algorithm>
#include <cstddef>
#include <cstdio>
#include <deque>
#include <initializer_list>
#include <map>
#include <mutex>
#include <thread>
#include <vector>

namespace {

const wchar_t kSession[] = L"SystemBoostFpsLive";
const GUID kDxgi = {0xCA11C036, 0x0102, 0x4A2D, {0xA6, 0xAD, 0xF0, 0x3C, 0xFE, 0xD5, 0xD3, 0xC9}};
const GUID kD3d9 = {0x783ACA0A, 0x790E, 0x4D7F, {0x84, 0x51, 0xAA, 0x85, 0x05, 0x11, 0xC6, 0xB9}};
const GUID kDxgKrnl = {0x802EC45A, 0x1E99, 0x4B83, {0x99, 0x20, 0x87, 0xC9, 0x82, 0x77, 0xBA, 0x9D}};
const USHORT kDxgiPresent = 42, kDxgiPresentOverlay = 55, kD3d9Present = 1, kKernelPresent = 184;
const size_t kSources = 3;          // preferred first: one frame shows up in several of them
const double kKeepSeconds = 10;     // the 1% low covers this much
const double kStaleSeconds = 2;     // events arrive up to about a second late (flush timer)
const double kBreakSeconds = 1;     // a longer gap ends a run of drawing

struct Recent {
    std::deque<LONGLONG> bySource[kSources];
    LONGLONG last = 0;
};

struct Properties {
    EVENT_TRACE_PROPERTIES p;
    wchar_t name[64];
};

std::mutex gLock;
std::map<DWORD, Recent> gRecent;
TRACEHANDLE gSessionHandle = 0;
TRACEHANDLE gConsumer = INVALID_PROCESSTRACE_HANDLE;
std::thread gThread;
LONGLONG gFreq = 10000000;   // QueryPerformanceCounter ticks a second; set again at start

Properties makeProperties() {
    Properties x{};
    x.p.Wnode.BufferSize = sizeof x;
    x.p.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
    x.p.Wnode.ClientContext = 1;            // QueryPerformanceCounter time stamps
    x.p.LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING;
    x.p.BufferSize = 8;                     // KB: small buffers fill, and so arrive, sooner
    x.p.MinimumBuffers = 4;
    x.p.MaximumBuffers = 64;
    x.p.LoggerNameOffset = offsetof(Properties, name);
    x.p.FlushTimer = 1;                     // seconds, the shortest there is
    return x;
}

LONGLONG ticks(double seconds) { return (LONGLONG)(seconds * (double)gFreq); }

void WINAPI onEvent(PEVENT_RECORD e) {
    const GUID& provider = e->EventHeader.ProviderId;
    USHORT id = e->EventHeader.EventDescriptor.Id;
    int source = -1;
    if (IsEqualGUID(provider, kDxgi) && (id == kDxgiPresent || id == kDxgiPresentOverlay)) source = 0;
    else if (IsEqualGUID(provider, kD3d9) && id == kD3d9Present) source = 1;
    else if (IsEqualGUID(provider, kDxgKrnl) && id == kKernelPresent) source = 2;
    if (source < 0) return;
    LONGLONG t = e->EventHeader.TimeStamp.QuadPart;
    std::lock_guard<std::mutex> hold(gLock);
    Recent& r = gRecent[e->EventHeader.ProcessId];
    auto& list = r.bySource[source];
    list.push_back(t);
    while (!list.empty() && list.front() < t - ticks(kKeepSeconds)) list.pop_front();
    r.last = (std::max)(r.last, t);
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

/// From the frame times of one process (sorted, QueryPerformanceCounter ticks): frames a second over
/// the second before the newest frame, the 1% low, and how long it has drawn without a pause.
FpsLive fpsLiveRate(const std::vector<long long>& t, long long freq) {
    FpsLive r{};
    if (t.empty() || freq <= 0) return r;
    size_t first = t.size() - 1;   // start of the run of drawing that reaches the newest frame
    while (first > 0 && t[first] - t[first - 1] <= (long long)(kBreakSeconds * freq)) first--;
    r.steady = double(t.back() - t[first]) / freq;
    size_t n = 0, from = t.size();
    while (from > first && t[from - 1] > t.back() - freq) { from--; n++; }
    double span = double(t.back() - t[from]) / freq;
    r.fps = span >= 0.5 ? double(n - 1) / span : double(n);
    std::vector<long long> gaps;
    for (size_t i = first + 1; i < t.size(); i++) gaps.push_back(t[i] - t[i - 1]);
    if (gaps.empty()) return r;
    std::sort(gaps.rbegin(), gaps.rend());
    size_t worst = (std::max)((size_t)1, gaps.size() / 100);
    double total = 0;
    for (size_t i = 0; i < worst; i++) total += (double)gaps[i];
    r.low = total > 0 ? worst * double(freq) / total : 0;
    return r;
}

std::string fpsLiveStop() {
    Properties x = makeProperties();
    ControlTraceW(0, kSession, &x.p, EVENT_TRACE_CONTROL_STOP);   // also clears one left by a killed helper
    gSessionHandle = 0;
    if (gConsumer != INVALID_PROCESSTRACE_HANDLE) { CloseTrace(gConsumer); gConsumer = INVALID_PROCESSTRACE_HANDLE; }
    if (gThread.joinable()) gThread.join();
    std::lock_guard<std::mutex> hold(gLock);
    gRecent.clear();
    return "ok\t";
}

std::string fpsLiveStart() {
    fpsLiveStop();
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    gFreq = f.QuadPart;
    Properties x = makeProperties();
    ULONG r = StartTraceW(&gSessionHandle, kSession, &x.p);
    if (r != ERROR_SUCCESS) { gSessionHandle = 0; return "err\tsession " + std::to_string(r); }
    bool any = enable(kDxgi, {kDxgiPresent, kDxgiPresentOverlay});
    any = enable(kD3d9, {kD3d9Present}) || any;
    any = enable(kDxgKrnl, {kKernelPresent}) || any;
    if (!any) { fpsLiveStop(); return "err\tproviders"; }
    EVENT_TRACE_LOGFILEW log{};
    log.LoggerName = (LPWSTR)kSession;
    log.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
    log.EventRecordCallback = onEvent;
    gConsumer = OpenTraceW(&log);
    if (gConsumer == INVALID_PROCESSTRACE_HANDLE) { fpsLiveStop(); return "err\tconsumer " + std::to_string(GetLastError()); }
    gThread = std::thread([] { ProcessTrace(&gConsumer, 1, nullptr, nullptr); });
    return "ok\t";
}

/// fps;1% low;seconds drawn without a pause. 0;0;0 when the process has not drawn lately.
std::string fpsLiveRead(DWORD pid) {
    if (gSessionHandle == 0) return "err\tnot started";
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    std::vector<long long> t;
    {
        std::lock_guard<std::mutex> hold(gLock);
        for (auto it = gRecent.begin(); it != gRecent.end();)   // processes that stopped drawing
            if (it->second.last < now.QuadPart - ticks(kKeepSeconds)) it = gRecent.erase(it); else ++it;
        auto it = gRecent.find(pid);
        if (it == gRecent.end() || it->second.last < now.QuadPart - ticks(kStaleSeconds)) return "ok\t0;0;0";
        for (auto& list : it->second.bySource)
            if (!list.empty() && list.back() >= now.QuadPart - ticks(kStaleSeconds)) { t.assign(list.begin(), list.end()); break; }
    }
    if (t.empty()) return "ok\t0;0;0";
    std::sort(t.begin(), t.end());
    FpsLive r = fpsLiveRate(t, gFreq);
    char b[96];
    snprintf(b, sizeof b, "ok\t%.1f;%.1f;%.1f", r.fps, r.low, r.steady);
    return b;
}

void fpsLiveShutdown() {
    if (gSessionHandle != 0) fpsLiveStop();
}
