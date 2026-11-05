// Test window with a steady frame rate. Windows only; it stands in for a game, so it
// presents through a flip-model swap chain the way a game does.
//   cl /nologo /std:c++20 /O2 /EHsc /utf-8 tests\fps_window.cpp /Fe:build\tests\ /Fo:build\tests\ user32.lib d3d11.lib dxgi.lib
//   fps_window.exe --fps 40 --width 1280 --height 720 --seconds 30 --title SB-TestWindow
// Prints {"hwnd":...,"clientX":...} when ready and one {"click":...} line per click,
// then {"frames":...,"seconds":...} on exit. Frames are scheduled on an absolute
// timeline, so the rate does not drift with timer error.
#define UNICODE
#include <d3d11.h>
#include <dxgi1_2.h>
#include <windows.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
using Microsoft::WRL::ComPtr;

static int g_w = 1280, g_h = 720;
static int g_clickX = -1, g_clickY = -1;

static double Now() {
    static LARGE_INTEGER f = [] { LARGE_INTEGER v; QueryPerformanceFrequency(&v); return v; }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return double(c.QuadPart) / double(f.QuadPart);
}

static LRESULT CALLBACK Proc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_LBUTTONDOWN:
        g_clickX = short(LOWORD(l));
        g_clickY = short(HIWORD(l));
        std::printf("{\"click\":[%d,%d]}\n", g_clickX, g_clickY);
        std::fflush(stdout);
        return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    case WM_ERASEBKGND: return 1;
    }
    return DefWindowProcW(h, m, w, l);
}

// Fine grid and diagonal detail for the upscale comparison.
static std::vector<uint32_t> Background() {
    std::vector<uint32_t> px(size_t(g_w) * g_h);
    for (int y = 0; y < g_h; ++y)
        for (int x = 0; x < g_w; ++x) {
            uint32_t c = ((x / 8 + y / 8) & 1) ? 0x202024 : 0x34343A;
            if ((x + y) % 64 == 0) c = 0xE0E0E0;
            if (x % 160 == 0 || y % 90 == 0) c = 0xFF7A1A;
            px[size_t(y) * g_w + x] = c;
        }
    return px;
}

// Bars moving at a fixed speed in the middle band so every frame differs, and a cross
// at the last click.
static void Draw(uint32_t* px, const std::vector<uint32_t>& bg, int frame) {
    std::memcpy(px, bg.data(), bg.size() * sizeof(uint32_t));
    const int top = g_h / 2 - 60, bottom = g_h / 2 + 60, step = 16;
    for (int bar = 0; bar < 6; ++bar) {
        const int left = (frame * step + bar * g_w / 6) % g_w;
        for (int y = top; y < bottom; ++y)
            for (int x = left; x < left + 40; ++x) px[size_t(y) * g_w + (x % g_w)] = 0xFFFFFF;
    }
    if (g_clickX >= 0) {
        for (int i = -20; i <= 20; ++i) {
            const int cx = g_clickX + i, cy = g_clickY + i;
            if (cx >= 0 && cx < g_w) px[size_t(g_clickY) * g_w + cx] = 0x00FF00;
            if (cy >= 0 && cy < g_h) px[size_t(cy) * g_w + g_clickX] = 0x00FF00;
        }
    }
}

int wmain(int argc, wchar_t** argv) {
    double fps = 40, seconds = 30;
    std::wstring title = L"SB-TestWindow";
    int x = 80, y = 60;
    for (int i = 1; i + 1 < argc; i += 2) {
        const std::wstring k = argv[i];
        if (k == L"--fps") fps = _wtof(argv[i + 1]);
        else if (k == L"--width") g_w = _wtoi(argv[i + 1]);
        else if (k == L"--height") g_h = _wtoi(argv[i + 1]);
        else if (k == L"--seconds") seconds = _wtof(argv[i + 1]);
        else if (k == L"--title") title = argv[i + 1];
        else if (k == L"--x") x = _wtoi(argv[i + 1]);
        else if (k == L"--y") y = _wtoi(argv[i + 1]);
    }
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    WNDCLASSW wc{};
    wc.lpfnWndProc = Proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"SBTestWindow";
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    RegisterClassW(&wc);
    RECT r{0, 0, g_w, g_h};
    const DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    AdjustWindowRect(&r, style, FALSE);
    HWND hwnd = CreateWindowExW(WS_EX_TOPMOST, wc.lpszClassName, title.c_str(), style, x, y, r.right - r.left,
                                r.bottom - r.top, nullptr, nullptr, wc.hInstance, nullptr);
    ShowWindow(hwnd, SW_SHOW);

    DXGI_SWAP_CHAIN_DESC sd{};
    sd.BufferDesc.Width = UINT(g_w);
    sd.BufferDesc.Height = UINT(g_h);
    sd.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    sd.SampleDesc.Count = 1;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.BufferCount = 2;
    sd.OutputWindow = hwnd;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    ComPtr<IDXGISwapChain> swap;
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    if (FAILED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
                                             D3D11_SDK_VERSION, &sd, &swap, &dev, nullptr, &ctx))) {
        std::puts("{\"error\":\"swap chain\"}");
        return 1;
    }
    ComPtr<ID3D11Texture2D> back;
    swap->GetBuffer(0, IID_PPV_ARGS(&back));
    const std::vector<uint32_t> bg = Background();
    std::vector<uint32_t> px(bg.size());

    POINT org{0, 0};
    ClientToScreen(hwnd, &org);
    std::printf("{\"hwnd\":%llu,\"clientX\":%ld,\"clientY\":%ld,\"width\":%d,\"height\":%d}\n",
                (unsigned long long)(uintptr_t)hwnd, org.x, org.y, g_w, g_h);
    std::fflush(stdout);

    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    const double start = Now(), period = 1.0 / fps;
    int frame = 0;
    bool running = true;
    while (running) {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) running = false;
            DispatchMessageW(&msg);
        }
        const double now = Now();
        if (now - start >= seconds) break;
        const double due = start + frame * period;
        if (now < due) {
            const double wait = due - now;
            if (wait > 0.002) {
                LARGE_INTEGER t;
                t.QuadPart = -LONGLONG((wait - 0.001) * 1e7);
                SetWaitableTimer(timer, &t, 0, nullptr, nullptr, FALSE);
                WaitForSingleObject(timer, 50);
            }
            continue;
        }
        Draw(px.data(), bg, frame);
        ctx->UpdateSubresource(back.Get(), 0, nullptr, px.data(), UINT(g_w) * 4, 0);
        swap->Present(0, 0);
        ++frame;
    }
    std::printf("{\"frames\":%d,\"seconds\":%.3f}\n", frame, Now() - start);
    std::fflush(stdout);
    return 0;
}
