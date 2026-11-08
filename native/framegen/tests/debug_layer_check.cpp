// The frame generation pipeline on WARP, Microsoft's software GPU, with the D3D11 debug layer on:
// every warning and error the layer raises is printed. The development PC has only an NVIDIA card,
// so this is what checks that nothing leans on one driver tolerating a misuse. Also prints, per
// adapter, whether it shares the computer's memory (what the engine calls integrated).
// No window, no capture, small sizes. Needs the Graphics Tools optional feature (d3d11_3SDKLayers.dll).
//   debug_layer_check.exe <shader folder>      exit 0 = no warnings or errors
// Build (Developer prompt, from native/framegen): cl /std:c++20 /O2 /EHsc /utf-8 /DUNICODE /D_UNICODE
//   /DNOMINMAX /DWIN32_LEAN_AND_MEAN /Isrc tests/debug_layer_check.cpp src/FrameGenGpu.cpp
//   /Fe:build/tests/debug_layer_check.exe /link d3d11.lib dxgi.lib d3dcompiler.lib user32.lib
#include "FrameGenGpu.h"
#include <d3d11_2.h>
#include <d3d11sdklayers.h>
#include <cmath>
#include <cstdio>
#include <vector>

using namespace sbfg;

static ComPtr<ID3D11Texture2D> Picture(ID3D11Device* dev, UINT w, UINT h, int shift) {
    std::vector<uint32_t> px(size_t(w) * h);
    for (UINT y = 0; y < h; ++y)
        for (UINT x = 0; x < w; ++x) {
            const double u = double(x) + shift, v = double(y) + shift * 0.4;
            const int c = 128 + int(50 * std::sin(u / 37) + 40 * std::cos(v / 23) + 30 * std::sin((u + v) / 7));
            const uint32_t g = uint32_t(c < 0 ? 0 : c > 255 ? 255 : c);
            px[size_t(y) * w + x] = 0xFF000000u | g | (g << 8) | ((255 - g) << 16);
        }
    D3D11_TEXTURE2D_DESC d{};
    d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1; d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA init{px.data(), w * 4, 0};
    ComPtr<ID3D11Texture2D> t;
    Check(dev->CreateTexture2D(&d, &init, &t), "picture");
    return t;
}

static int Drain(ID3D11InfoQueue* q, const char* step) {
    int bad = 0;
    const UINT64 n = q->GetNumStoredMessages();
    for (UINT64 i = 0; i < n; ++i) {
        SIZE_T len = 0;
        q->GetMessage(i, nullptr, &len);
        std::vector<char> buf(len);
        auto* m = reinterpret_cast<D3D11_MESSAGE*>(buf.data());
        q->GetMessage(i, m, &len);
        static const char* const sev[] = {"CORRUPTION", "ERROR", "WARNING", "INFO", "MESSAGE"};
        if (m->Severity <= D3D11_MESSAGE_SEVERITY_WARNING) ++bad;
        if (m->Severity <= D3D11_MESSAGE_SEVERITY_WARNING || m->Severity == D3D11_MESSAGE_SEVERITY_INFO)
            std::printf("  [%s] %s: %.*s\n", sev[m->Severity], step, int(m->DescriptionByteLength), m->pDescription);
    }
    q->ClearStoredMessages();
    return bad;
}

static int Main(int argc, wchar_t** argv) {
    if (argc < 2) return 2;

    ComPtr<IDXGIFactory4> f;
    Check(CreateDXGIFactory1(IID_PPV_ARGS(&f)), "factory");
    ComPtr<IDXGIAdapter1> a;
    for (UINT i = 0; f->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; ++i, a.Reset()) {
        DXGI_ADAPTER_DESC1 d{};
        a->GetDesc1(&d);
        ComPtr<ID3D11Device> dev;
        D3D11_FEATURE_DATA_D3D11_OPTIONS2 o{};
        HRESULT hr = D3D11CreateDevice(a.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, nullptr, nullptr);
        if (SUCCEEDED(hr)) hr = dev->CheckFeatureSupport(D3D11_FEATURE_D3D11_OPTIONS2, &o, sizeof o);
        std::printf("adapter %u: %ls vendor %04X dedicated %llu MB shared %llu MB software %d uma %s\n", i, d.Description,
                    d.VendorId, (unsigned long long)(d.DedicatedVideoMemory >> 20), (unsigned long long)(d.SharedSystemMemory >> 20),
                    (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) ? 1 : 0, SUCCEEDED(hr) ? (o.UnifiedMemoryArchitecture ? "yes" : "no") : "unknown");
    }

    ComPtr<IDXGIAdapter1> warp;
    Check(f->EnumWarpAdapter(IID_PPV_ARGS(&warp)), "warp");
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    Check(D3D11CreateDevice(warp.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_DEBUG, levels, 2,
                            D3D11_SDK_VERSION, &dev, nullptr, &ctx), "warp device with the debug layer");
    ComPtr<ID3D11InfoQueue> q;
    Check(dev.As(&q), "info queue");
    q->SetMessageCountLimit(4096);
    {
        D3D11_FEATURE_DATA_D3D11_OPTIONS2 o{};
        dev->CheckFeatureSupport(D3D11_FEATURE_D3D11_OPTIONS2, &o, sizeof o);
        std::printf("warp uma %s (should be yes)\n", o.UnifiedMemoryArchitecture ? "yes" : "no");
    }

    const UINT capW = 320, capH = 180;
    std::vector<ComPtr<ID3D11Texture2D>> pics;
    for (int i = 0; i < 4; ++i) pics.push_back(Picture(dev.Get(), capW + 8, capH + 8, i * 6));
    // a cursor image as the app makes one: RGBA16F
    ComPtr<ID3D11ShaderResourceView> cursor;
    {
        std::vector<uint16_t> px(32 * 32 * 4, 0x3C00);  // 1.0 in half
        D3D11_TEXTURE2D_DESC d{};
        d.Width = 32; d.Height = 32; d.MipLevels = 1; d.ArraySize = 1; d.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA init{px.data(), 32 * 8, 0};
        ComPtr<ID3D11Texture2D> t;
        Check(dev->CreateTexture2D(&d, &init, &t), "cursor");
        Check(dev->CreateShaderResourceView(t.Get(), nullptr, &cursor), "cursor view");
    }
    int bad = Drain(q.Get(), "setup");

    FrameGenGpu fg(dev.Get(), ctx.Get(), argv[1]);
    bad += Drain(q.Get(), "shaders");
    static const char* const names[] = {"low", "performance", "balanced", "quality"};
    for (bool up : {false, true})
        for (Quality qu : {Quality::Low, Quality::Performance, Quality::Balanced, Quality::Quality}) {
            Settings s;
            s.quality = qu;
            s.upscale = up;
            // an odd capture size inside the picture, as a window client area can be
            const UINT w = up ? 301 : capW, h = up ? 171 : capH;
            fg.Resize(w, h, up ? 481 : w, up ? 271 : h, s);
            fg.ResetHistory();
            fg.SetCursor(up ? cursor.Get() : nullptr, 40, 30, 32, 32);
            const D3D11_BOX box{3, 2, 0, 3 + w, 2 + h, 1};
            for (int i = 0; i < 6; ++i) {
                fg.PushFrame(pics[i % pics.size()].Get(), box);
                if (fg.HasPair())
                    for (float t : {0.25f, 0.5f, 0.75f}) fg.Generate(t);
            }
            ctx->Flush();
            char step[64];
            std::snprintf(step, sizeof step, "%s%s", names[int(qu)], up ? " upscale" : "");
            const int n = Drain(q.Get(), step);
            bad += n;
            std::printf("%-20s %s\n", step, n ? "MESSAGES" : "clean");
        }
    fg.SetCursor(nullptr, 0, 0, 0, 0);
    std::printf(bad ? "debug layer: %d warning(s) or error(s)\n" : "debug layer: no warnings or errors\n", bad);
    return bad ? 1 : 0;
}

int wmain(int argc, wchar_t** argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    try { return Main(argc, argv); }
    catch (const HrError& e) { std::printf("failed: %s (0x%08lX)\n", e.what, (unsigned long)e.hr); return 3; }
    catch (const std::exception& e) { std::printf("failed: %s\n", e.what()); return 3; }
}
