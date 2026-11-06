// SystemBoost FrameGen v0.6 - entry point.
//
// Runs as a separate process next to the game. It never opens, injects into,
// or reads the memory of the game process: input is the OS screen capture of
// the game window, output is a click-through topmost window.
//
// Protocol with the WPF app (one JSON object per line):
//   stdout: {"type":"ready"|"stats"|"warning"|"error"|"exit", ...}
//   stdin : "mode auto|x2|x3|x4|x6|x8|off", "sharp 0..1", "enable", "disable", "quit"
#include "Capture.h"
#include "Cursor.h"
#include "FrameGenGpu.h"
#include "FrameMeter.h"
#include "Gpu.h"
#include "GpuFit.h"
#include "Overlay.h"
#include "Pacer.h"
#include "Settings.h"
#include <dwmapi.h>
#include <shellapi.h>
#include <atomic>
#include <cstdio>
#include <iostream>
#include <mutex>
#include <string>
#include <thread>

using namespace sbfg;

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

namespace {

void RestoreCursorsOnError() {
    SystemParametersInfoW(SPI_SETCURSORS, 0, nullptr, 0);  // harmless if nothing was replaced
}

std::mutex g_outMutex;
void Emit(const std::string& json) {
    std::lock_guard<std::mutex> l(g_outMutex);
    std::fwrite(json.data(), 1, json.size(), stdout);
    std::fputc('\n', stdout);
    std::fflush(stdout);
}
void EmitMsg(const char* type, const std::string& msg) {
    Emit(std::string("{\"type\":\"") + type + "\",\"msg\":\"" + JsonEscape(msg) + "\"}");
}

// commands from stdin (applied on the main thread)
struct Commands {
    std::mutex m;
    bool hasMode = false; MulMode mode = MulMode::Auto;
    bool hasSharp = false; float sharp = 0.5f;
    std::atomic<bool> enabled{true};
    std::atomic<bool> quit{false};
} g_cmd;

void StdinThread() {
    std::string line;
    while (std::getline(std::cin, line)) {
        if (line == "quit") break;
        if (line == "enable") g_cmd.enabled = true;
        else if (line == "disable") g_cmd.enabled = false;
        else if (line.rfind("mode ", 0) == 0) {
            std::vector<std::string> a{"--hwnd", "1", "--mode", line.substr(5)};
            Settings tmp;
            if (parseArgs(a, tmp).empty()) {
                std::lock_guard<std::mutex> l(g_cmd.m);
                g_cmd.hasMode = true; g_cmd.mode = tmp.mode;
            } else EmitMsg("warning", "bad command: " + line);
        } else if (line.rfind("sharp ", 0) == 0) {
            std::lock_guard<std::mutex> l(g_cmd.m);
            g_cmd.hasSharp = true;
            g_cmd.sharp = std::clamp(float(std::atof(line.c_str() + 6)), 0.f, 1.f);
        } else if (!line.empty()) EmitMsg("warning", "unknown command: " + line);
    }
    g_cmd.quit = true;  // stdin closed (parent exited) -> stop
}

// DWM only reports the primary monitor: next to a 144 Hz primary, the vblank of a 120 Hz
// monitor came out 7 to 8 ms off, different on each read. Each output is asked for its own.
std::atomic<HMONITOR> g_vblankMonitor{nullptr};
std::atomic<double> g_vblankTime{0};
constexpr DWORD kVBlankSampleMs = 500;

ComPtr<IDXGIOutput> OutputOf(HMONITOR mon) {
    ComPtr<IDXGIFactory1> f;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&f)))) return nullptr;
    ComPtr<IDXGIAdapter1> a;
    for (UINT i = 0; f->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; ++i, a.Reset()) {
        ComPtr<IDXGIOutput> o;
        for (UINT j = 0; a->EnumOutputs(j, &o) != DXGI_ERROR_NOT_FOUND; ++j, o.Reset()) {
            DXGI_OUTPUT_DESC d{};
            if (SUCCEEDED(o->GetDesc(&d)) && d.Monitor == mon) return o;
        }
    }
    return nullptr;
}

// Detached: WaitForVBlank can block while a monitor is off, and must not hold up exit.
void WatchVBlank() {
    HMONITOR cur = nullptr;
    ComPtr<IDXGIOutput> out;
    for (;;) {
        const HMONITOR want = g_vblankMonitor.load();
        if (want != cur) {
            cur = want;
            g_vblankTime = 0;
            out = want ? OutputOf(want) : nullptr;
        }
        if (out) {
            if (SUCCEEDED(out->WaitForVBlank())) {
                g_vblankTime = QpcNow();
            } else {    // the display changed: find the output again
                cur = nullptr;
                out.Reset();
                g_vblankTime = 0;
            }
        }
        Sleep(kVBlankSampleMs);
    }
}

struct DisplayTiming { double hz = 60; double phase = 0; };
DisplayTiming QueryDisplay(HWND game) {
    DisplayTiming d;
    DWM_TIMING_INFO ti{};
    ti.cbSize = sizeof ti;
    if (SUCCEEDED(DwmGetCompositionTimingInfo(nullptr, &ti)) && ti.rateRefresh.uiDenominator) {
        d.hz = double(ti.rateRefresh.uiNumerator) / ti.rateRefresh.uiDenominator;
        d.phase = QpcToSec(LONGLONG(ti.qpcVBlank));
    }
    // DWM reports the composition rate; if the game's monitor runs at another
    // rate (multi-monitor), use that monitor's mode instead.
    const HMONITOR mon = MonitorFromWindow(game, MONITOR_DEFAULTTONEAREST);
    MONITORINFOEXW mi{};
    mi.cbSize = sizeof mi;
    if (GetMonitorInfoW(mon, &mi)) {
        const bool primary = mi.dwFlags & MONITORINFOF_PRIMARY;
        g_vblankMonitor = primary ? nullptr : mon;    // DWM is exact there, a wake-up is not
        if (const double vblank = g_vblankTime; !primary && vblank > 0) d.phase = vblank;
        DEVMODEW dm{};
        dm.dmSize = sizeof dm;
        if (EnumDisplaySettingsW(mi.szDevice, ENUM_CURRENT_SETTINGS, &dm) && dm.dmDisplayFrequency > 1 &&
            std::fabs(double(dm.dmDisplayFrequency) - d.hz) > 2.0)
            d.hz = dm.dmDisplayFrequency;
    }
    return d;
}

struct Layout {
    RECT client{};      // game client area (screen)
    RECT overlay{};     // overlay window (screen)
    UINT capW = 0, capH = 0, outW = 0, outH = 0;
    UINT offX = 0, offY = 0;  // output position inside the overlay back buffer
    bool operator!=(const Layout& o) const {
        return memcmp(&client, &o.client, sizeof client) || memcmp(&overlay, &o.overlay, sizeof overlay);
    }
};

Layout ComputeLayout(HWND game, bool upscale) {
    Layout L;
    L.client = ClientRectScreen(game);
    L.capW = UINT(std::max<LONG>(1, L.client.right - L.client.left));
    L.capH = UINT(std::max<LONG>(1, L.client.bottom - L.client.top));
    if (!upscale) {
        L.overlay = L.client;
        L.outW = L.capW; L.outH = L.capH;
        return L;
    }
    MONITORINFO mi{};
    mi.cbSize = sizeof mi;
    GetMonitorInfoW(MonitorFromWindow(game, MONITOR_DEFAULTTONEAREST), &mi);
    L.overlay = mi.rcMonitor;
    const UINT mw = UINT(mi.rcMonitor.right - mi.rcMonitor.left), mh = UINT(mi.rcMonitor.bottom - mi.rcMonitor.top);
    // fit, keep aspect ratio (letterbox / pillarbox)
    const double s = std::min(double(mw) / L.capW, double(mh) / L.capH);
    L.outW = std::min(mw, UINT(L.capW * s + 0.5));
    L.outH = std::min(mh, UINT(L.capH * s + 0.5));
    L.offX = (mw - L.outW) / 2;
    L.offY = (mh - L.outH) / 2;
    return L;
}

void PreciseWaitUntil(HANDLE timer, double t) {
    double now = QpcNow();
    if (t <= now) return;
    double dt = t - now;
    if (timer && dt > 0.0003) {
        LARGE_INTEGER due;
        due.QuadPart = -LONGLONG((dt - 0.0002) * 1e7);  // relative, 100 ns
        SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE);
        WaitForSingleObject(timer, 50);
    }
    while (QpcNow() < t) YieldProcessor();
}

// Visible top-level windows for the game picker. Reads only window titles and
// sizes (no OpenProcess on the game).
BOOL CALLBACK EnumProc(HWND h, LPARAM lp) {
    auto* out = reinterpret_cast<std::string*>(lp);
    if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) || (GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW)) return TRUE;
    BOOL cloaked = FALSE;
    DwmGetWindowAttribute(h, DWMWA_CLOAKED, &cloaked, sizeof cloaked);
    if (cloaked) return TRUE;
    RECT r = ClientRectScreen(h);
    const LONG w = r.right - r.left, ht = r.bottom - r.top;
    wchar_t title[256] = {};
    GetWindowTextW(h, title, 256);
    if (w < 200 || ht < 150 || !title[0]) return TRUE;
    char buf[640];
    std::snprintf(buf, sizeof buf, "%s{\"id\":%llu,\"app\":\"\",\"title\":\"%s\",\"w\":%ld,\"h\":%ld}",
                  out->back() == '[' ? "" : ",", (unsigned long long)reinterpret_cast<uintptr_t>(h),
                  JsonEscape(Narrow(title)).c_str(), w, ht);
    *out += buf;
    return TRUE;
}

int Run(const Settings& s) {
    if (s.listWindows) {
        std::string json = "{\"type\":\"windows\",\"list\":[";
        EnumWindows(EnumProc, reinterpret_cast<LPARAM>(&json));
        Emit(json + "]}");
        return 0;
    }
    auto gpus = EnumerateGpus();
    if (s.listGpus) { Emit(GpusJson(gpus)); return 0; }
    HWND game = reinterpret_cast<HWND>(static_cast<uintptr_t>(s.hwnd));
    if (!IsWindow(game)) { EmitMsg("error", "game window not found"); return 2; }
    HWND gameRoot = GetAncestor(game, GA_ROOT);  // launcher may pass a child render window
    if (!gameRoot) gameRoot = game;

    std::string why;
    const int genIdx = PickGenGpu(s, game, gpus, why);
    const int dispIdx = AdapterForWindow(game, gpus);
    auto nameOf = [&](int i) { for (auto& g : gpus) if (g.index == i) return g.name; return std::string("?"); };

    ComPtr<ID3D11Device> genDev, dispDev;
    ComPtr<ID3D11DeviceContext> genCtx, dispCtx;
    CreateDevice((s.genGpu == GenGpu::Warp ? WarpAdapter() : AdapterByIndex(genIdx)).Get(), genDev, genCtx);

    CaptureApi api = s.capture;
#ifndef SBFG_WITH_WGC
    if (api == CaptureApi::Wgc) {
        EmitMsg("warning", "built without Windows.Graphics.Capture, using DXGI capture");
        api = CaptureApi::Dxgi;
    }
#endif
    std::unique_ptr<ICapture> cap;
    std::unique_ptr<CrossAdapterCopy> xcopy;
#ifdef SBFG_WITH_WGC
    if (api == CaptureApi::Wgc) {
        // cursor: drawn by us in upscaling mode, otherwise optional
        cap = CreateWgcCapture(genDev.Get(), game, s.captureCursor && !s.upscale);
        if (!cap) EmitMsg("warning", "Windows.Graphics.Capture failed, using DXGI capture");
    }
#endif
    if (!cap) {
        if (dispIdx != genIdx) {
            CreateDevice(AdapterByIndex(dispIdx).Get(), dispDev, dispCtx);
            cap = CreateDxgiCapture(dispDev.Get(), game);
            xcopy = std::make_unique<CrossAdapterCopy>(dispDev.Get(), dispCtx.Get(), genDev.Get(), genCtx.Get());
        } else {
            cap = CreateDxgiCapture(genDev.Get(), game);
        }
    }

    std::wstring dir;
    if (!s.shaderDir.empty()) {
        int n = MultiByteToWideChar(CP_UTF8, 0, s.shaderDir.c_str(), -1, nullptr, 0);
        dir.resize(n > 0 ? n - 1 : 0);
        if (n > 1) MultiByteToWideChar(CP_UTF8, 0, s.shaderDir.c_str(), -1, dir.data(), n);
    } else {
        wchar_t path[MAX_PATH];
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        dir = path;
        dir = dir.substr(0, dir.find_last_of(L"\\/")) + L"\\shaders";
    }
    FrameGenGpu fg(genDev.Get(), genCtx.Get(), dir);
    Overlay overlay(genDev.Get(), genCtx.Get(), s.showInCapture);
    CursorManager cursor(genDev.Get());
    if (s.upscale) InstallCursorSafety();

    std::thread(WatchVBlank).detach();
    DisplayTiming disp = QueryDisplay(game);
    PacerConfig pc;
    pc.mode = s.mode;
    pc.maxMul = s.maxMul;
    pc.refreshHz = disp.hz;
    pc.vblankPhase = disp.phase;
    Pacer pacer(pc);
    MulMode mode = s.mode;
    Settings cur = s;
    auto setMode = [&](MulMode m) {
        mode = pc.mode = m;
        pc.refreshHz = disp.hz;
        pc.vblankPhase = disp.phase;
        pacer.setConfig(pc);
    };

    Layout layout = ComputeLayout(game, s.upscale);
    fg.Resize(layout.capW, layout.capH, layout.outW, layout.outH, s);
    overlay.SetRect(layout.overlay);

    {
        char buf[768];
        std::snprintf(buf, sizeof buf,
                      "{\"type\":\"ready\",\"capture\":\"%s\",\"genGpu\":\"%s\",\"genGpuReason\":\"%s\","
                      "\"displayGpu\":\"%s\",\"refreshHz\":%.3f,\"capture_px\":[%u,%u],\"output_px\":[%u,%u],"
                      "\"crossAdapterCopy\":%s}",
                      cap->Name(), JsonEscape(nameOf(genIdx)).c_str(), JsonEscape(why).c_str(),
                      JsonEscape(nameOf(dispIdx)).c_str(), disp.hz, layout.capW, layout.capH, layout.outW,
                      layout.outH, xcopy ? "true" : "false");
        Emit(buf);
    }

    std::thread(StdinThread).detach();
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (!timer) timer = CreateWaitableTimerW(nullptr, FALSE, nullptr);

    double lastDisplayQuery = QpcNow(), lastLayout = lastDisplayQuery;
    FrameMeter meter;
    meter.reset(lastDisplayQuery);
    GpuFit fit;
    UINT lastShown = 0;
    uint64_t totalShown = 0;
    bool wasActive = false;
    CapturedFrame f;
    bool holding = false;  // captured frame waiting for the output clock (Pacer::wantsSource)

    while (!g_cmd.quit) {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        if (!IsWindow(game)) break;
        const double now = QpcNow();

        {   // commands
            std::lock_guard<std::mutex> l(g_cmd.m);
            if (g_cmd.hasMode) {
                g_cmd.hasMode = false;
                setMode(g_cmd.mode);
            }
            if (g_cmd.hasSharp) {
                g_cmd.hasSharp = false;
                cur.sharpness = g_cmd.sharp;  // survives Resize()
                fg.SetSharpness(g_cmd.sharp);
            }
        }

        // active only while the game is in front and not minimized
        HWND fgw = GetForegroundWindow();
        const bool active = g_cmd.enabled && !IsIconic(gameRoot) &&
                            (s.alwaysActive || fgw == gameRoot || fgw == overlay.Hwnd());
        if (!active) {
            if (wasActive) {
                meter.pause(now);
                overlay.Show(false);
                cursor.Restore();
                fg.SetCursor(nullptr, 0, 0, 0, 0);
                wasActive = false;
            }
            if (holding || cap->TryGet(f)) cap->Release();  // keep the capture queue drained
            holding = false;
            Sleep(20);
            continue;
        }

        if (now - lastLayout > 0.25) {  // window moved / resized
            lastLayout = now;
            Layout nl = ComputeLayout(game, s.upscale);
            if (nl != layout) {
                const bool sizeChanged = nl.capW != layout.capW || nl.capH != layout.capH ||
                                         nl.outW != layout.outW || nl.outH != layout.outH;
                layout = nl;
                overlay.SetRect(layout.overlay);
                if (sizeChanged) { fg.Resize(layout.capW, layout.capH, layout.outW, layout.outH, cur); pacer.reset(); }
            }
        }
        if (now - lastDisplayQuery > 1.0) {
            lastDisplayQuery = now;
            disp = QueryDisplay(game);
            pacer.setDisplay(disp.hz, disp.phase);
        }
        if (!wasActive) {
            // after a pause the old pair is stale: start fresh
            pacer.reset();
            fg.ResetHistory();
            meter.reset(now);
            if (s.upscale) cursor.Activate(s.clipCursor ? &layout.client : nullptr);
            wasActive = true;
        } else if (s.upscale && s.clipCursor) {
            cursor.UpdateClip(layout.client);
        }

        // 1) new captured frame? Looked for while one waits too: capture buffers left full
        // lose the game's next frames, and it reads as a slower game than it is.
        if (cap->TryGet(f)) {
            holding = true;
            meter.onSource(f.frames);
            pacer.onArrival(f.time > 0 ? f.time : now, f.frames);
        }
        holding = holding && f.texture;
        if (holding && (mode == MulMode::Off || pacer.wantsSource())) {
            holding = false;
            const UINT w = f.box.right - f.box.left, h = f.box.bottom - f.box.top;
            if (w == layout.capW && h == layout.capH) {
                if (xcopy) {
                    ID3D11Texture2D* t = xcopy->Copy(f.texture, f.box);
                    D3D11_BOX b{0, 0, 0, w, h, 1};
                    fg.PushFrame(t, b);
                } else {
                    fg.PushFrame(f.texture, f.box);
                }
                pacer.onSource(f.time > 0 ? f.time : now, (fg.GenMs() + (xcopy ? xcopy->LastMs() : 0)) / 1000.0);
            }
            cap->Release();
            if (mode == MulMode::Off && !s.upscale) {
                overlay.Show(false);  // nothing to add: show the game itself
            } else if (mode == MulMode::Off && fg.HasPair()) {   // pass-through (upscaling only)
                ID3D11Texture2D* out = fg.Generate(1.f);
                overlay.Show(true);
                overlay.Present(out, layout.offX, layout.offY, s.upscale);
                meter.onOutput(false);
            }
        }

        // 2) cursor (upscaling): draw at the scaled position
        if (s.upscale && cursor.Active()) {
            POINT p; const CursorImage* img = nullptr;
            if (cursor.Current(p, img)) {
                const double sc = double(layout.outW) / layout.capW;
                fg.SetCursor(img->srv.Get(), int((p.x - layout.client.left - img->hotX) * sc),
                             int((p.y - layout.client.top - img->hotY) * sc), UINT(img->w * sc), UINT(img->h * sc));
            } else {
                fg.SetCursor(nullptr, 0, 0, 0, 0);
            }
        }

        // 3) output on the pacer clock
        if (mode != MulMode::Off && fg.HasPair() && pacer.active()) {
            OutputSlot slot = pacer.next();
            if (QpcNow() >= slot.presentAt) {
                const float t = pacer.tFor(slot.displayAt);
                const bool repeat = pacer.repeats(t);
                if (!repeat) {
                    ID3D11Texture2D* out = fg.Generate(t);
                    overlay.Show(true);
                    overlay.Present(out, layout.offX, layout.offY, s.upscale);
                }
                meter.onOutput(repeat);
                pacer.advance(QpcNow());
            } else {
                // sleep until the next present or a short capture poll interval
                PreciseWaitUntil(timer, std::min(slot.presentAt, QpcNow() + 0.001));
            }
        } else {
            PreciseWaitUntil(timer, QpcNow() + 0.001);
        }

        // 4) stats
        const double t2 = QpcNow();
        FrameMeter::Sample m;
        if (meter.elapsed(t2) * 1000.0 >= s.statsMs && meter.take(t2, m)) {
            UINT shown = 0;
            if (overlay.ShownCount(shown)) { totalShown += shown - lastShown; lastShown = shown; }
            char buf[900];
            std::snprintf(buf, sizeof buf,
                          "{\"type\":\"stats\",\"srcFps\":%.1f,\"outFps\":%.1f,\"multiplier\":%.2f,"
                          "\"mode\":%d,\"refreshHz\":%.2f,\"delayMs\":%.1f,\"addedLatencyMs\":%.1f,"
                          "\"genMs\":%.2f,\"analyzeMs\":%.2f,\"transferMs\":%.2f,\"late\":%llu,\"skipped\":%llu,"
                          "\"srcFrames\":%llu,\"outFrames\":%llu,\"repeatFrames\":%llu,\"shownFrames\":%llu,"
                          "\"clockSec\":%.4f,\"measuredSec\":%.3f,\"activeSec\":%.3f,\"divider\":%d,"
                          "\"intervalMs\":%.3f,\"p10Ms\":%.3f,\"p50Ms\":%.3f,\"p90Ms\":%.3f}",
                          m.srcFps(), m.outFps(), m.multiplier(), int(mode), disp.hz,
                          pacer.delayMs(), pacer.addedLatencyMs(), fg.GenMs(), fg.AnalyzeMs(),
                          xcopy ? xcopy->LastMs() : 0.0, (unsigned long long)pacer.stats.late,
                          (unsigned long long)pacer.stats.skipped, (unsigned long long)meter.totalSrc(),
                          (unsigned long long)meter.totalOut(), (unsigned long long)meter.totalRepeat(),
                          (unsigned long long)totalShown, t2, m.seconds, meter.activeSec(),
                          mode == MulMode::Auto ? pacer.divider() : 1, pacer.interval() * 1000.0,
                          pacer.percentile(0.1) * 1000.0, pacer.percentile(0.5) * 1000.0,
                          pacer.percentile(0.9) * 1000.0);
            Emit(buf);

            if (mode != MulMode::Off && fit.update(t2, pacer.stats.skipped)) {
                if (cur.quality != Quality::Low) {
                    cur.quality = Quality(int(cur.quality) - 1);
                    fg.Resize(layout.capW, layout.capH, layout.outW, layout.outH, cur);
                    pacer.reset();
                    static const char* const names[] = {"low", "performance", "balanced"};
                    Emit(std::string("{\"type\":\"fit\",\"quality\":\"") + names[int(cur.quality)] + "\"}");
                } else if (pacer.slowDown()) {
                    std::snprintf(buf, sizeof buf, "{\"type\":\"fit\",\"maxOut\":%.0f}", pacer.outputCap());
                    Emit(buf);
                } else {    // the game alone is the better picture now
                    setMode(MulMode::Off);
                    Emit("{\"type\":\"fit\",\"stopped\":true}");
                }
            }
        }
    }
    cursor.Restore();
    overlay.Show(false);
    if (timer) CloseHandle(timer);
    Emit("{\"type\":\"exit\"}");
    return 0;
}

}  // namespace

static int Main(int argc, wchar_t** argv) {
    std::vector<std::string> args;
    for (int i = 1; i < argc; ++i) args.push_back(Narrow(argv[i]));
    Settings s;
    std::string err = parseArgs(args, s);
    if (!err.empty()) { EmitMsg("error", err); return 1; }
    // A late Present is a visible stutter; above-normal (not high) keeps pacing
    // stable without taking CPU time away from the game.
    SetPriorityClass(GetCurrentProcess(), ABOVE_NORMAL_PRIORITY_CLASS);
    // physical pixels everywhere (window rects, cursor, capture textures)
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    try {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        (void)hr;
        int rc = Run(s);
        CoUninitialize();
        return rc;
    } catch (const HrError& e) {
        char buf[64];
        std::snprintf(buf, sizeof buf, " (hr=0x%08lX)", static_cast<unsigned long>(e.hr));
        EmitMsg("error", std::string(e.what) + buf);
        RestoreCursorsOnError();
        return 3;
    } catch (const std::exception& e) {
        EmitMsg("error", e.what());
        RestoreCursorsOnError();
        return 4;
    } catch (...) {  // e.g. winrt::hresult_error outside capture
        EmitMsg("error", "unexpected error");
        RestoreCursorsOnError();
        return 4;
    }
}

int wmain(int argc, wchar_t** argv) {
    int rc = Main(argc, argv);
    std::fflush(stdout);
    // the stdin reader thread may be blocked in a read; end the process directly
    // instead of running CRT shutdown that could wait on the stdin lock
    ExitProcess(UINT(rc));
}

#if defined(__MINGW32__) && !defined(_UNICODE)
// mingw without -municode: forward to wmain
int main() {
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    int rc = wmain(argc, argv);
    LocalFree(argv);
    return rc;
}
#endif
