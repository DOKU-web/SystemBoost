// How much GPU time frame generation costs: no window, no capture, nothing on the screen.
// Runs batches and waits for the GPU at the end of each: a median over batches, per game frame and
// per generated frame. A wait after every single step would measure the driver's wake-up instead.
//   gen_bench.exe <shader folder> [width height]
// Build (Developer prompt, from native/framegen): cl /std:c++20 /O2 /EHsc /utf-8 /DUNICODE /D_UNICODE
//   /DNOMINMAX /DWIN32_LEAN_AND_MEAN /Isrc tests/gen_bench.cpp src/FrameGenGpu.cpp /Fe:build/tests/gen_bench.exe
//   /link d3d11.lib dxgi.lib d3dcompiler.lib user32.lib winmm.lib
#include "FrameGenGpu.h"
#include <timeapi.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <vector>

#pragma comment(lib, "winmm.lib")

using namespace sbfg;

static void Wait(ID3D11Device* dev, ID3D11DeviceContext* ctx) {
    static ComPtr<ID3D11Query> q = [&] {
        ComPtr<ID3D11Query> v;
        D3D11_QUERY_DESC d{D3D11_QUERY_EVENT, 0};
        dev->CreateQuery(&d, &v);
        return v;
    }();
    ctx->End(q.Get());
    ctx->Flush();
    BOOL done = FALSE;
    while (ctx->GetData(q.Get(), &done, sizeof done, D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_FALSE) {}
}

// A picture with detail at every scale, moved a little each frame.
static ComPtr<ID3D11Texture2D> Picture(ID3D11Device* dev, UINT w, UINT h, int shift) {
    std::vector<uint32_t> px(size_t(w) * h);
    for (UINT y = 0; y < h; ++y)
        for (UINT x = 0; x < w; ++x) {
            const double u = double(x) + shift, v = double(y) + shift * 0.4;
            const int c = 128 + int(50 * std::sin(u / 37) + 40 * std::cos(v / 23) + 30 * std::sin((u + v) / 7));
            const int d = ((int(u) / 16 + int(v) / 16) & 1) * 40;
            const uint32_t g = uint32_t(std::min(255, std::max(0, c + d)));
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

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) return 2;
    timeBeginPeriod(1);
    const UINT w = argc > 3 ? UINT(_wtoi(argv[2])) : 1920, h = argc > 3 ? UINT(_wtoi(argv[3])) : 1080;
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, nullptr, &ctx), "device");
    std::vector<ComPtr<ID3D11Texture2D>> pics;
    for (int i = 0; i < 6; ++i) pics.push_back(Picture(dev.Get(), w, h, i * 6));
    const D3D11_BOX box{0, 0, 0, w, h, 1};
    FrameGenGpu fg(dev.Get(), ctx.Get(), argv[1]);
    std::printf("%ux%u  analysis ms per game frame / ms per generated frame\n", w, h);
    for (Quality q : {Quality::Quality, Quality::Balanced, Quality::Performance, Quality::Low}) {
        Settings s;
        s.quality = q;
        fg.Resize(w, h, w, h, s);
        fg.ResetHistory();
        for (int i = 0; i < 4; ++i) fg.PushFrame(pics[i].Get(), box);
        Wait(dev.Get(), ctx.Get());
        std::vector<double> analyse, gen;
        int pic = 4;
        for (int round = 0; round < 9; ++round) {
            double t0 = QpcNow();
            for (int i = 0; i < 20; ++i) {
                fg.PushFrame(pics[pic++ % pics.size()].Get(), box);
                ctx->Flush();
            }
            Wait(dev.Get(), ctx.Get());
            analyse.push_back((QpcNow() - t0) * 1000 / 20);
            t0 = QpcNow();
            for (int i = 0; i < 60; ++i) {
                fg.Generate(0.25f + 0.25f * (i % 3));
                ctx->Flush();
            }
            Wait(dev.Get(), ctx.Get());
            gen.push_back((QpcNow() - t0) * 1000 / 60);
        }
        std::sort(analyse.begin(), analyse.end());
        std::sort(gen.begin(), gen.end());
        static const char* const names[] = {"low", "performance", "balanced", "quality"};
        std::printf("  %-12s %6.2f / %5.2f\n", names[int(q)], analyse[4], gen[4]);
    }
    return 0;
}
