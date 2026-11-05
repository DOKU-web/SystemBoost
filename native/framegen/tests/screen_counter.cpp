// Counts the images that really reach the screen in a rectangle, as a check that does
// not depend on the engine's own counters. Run the engine with --show-in-capture.
//   cl /nologo /std:c++20 /O2 /EHsc /utf-8 tests\screen_counter.cpp /Fe:build\tests\ /Fo:build\tests\ d3d11.lib dxgi.lib user32.lib
//   screen_counter.exe --x 80 --y 90 --w 1280 --h 720 --seconds 8 [--save frame.bmp]
// Desktop Duplication hands over each composed desktop image. A strip across the
// rectangle is hashed, and an image counts only if the strip changed.
#include <d3d11.h>
#include <dxgi1_2.h>
#include <windows.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstdio>
#include <string>
using Microsoft::WRL::ComPtr;

static double Now() {
    static LARGE_INTEGER f = [] { LARGE_INTEGER v; QueryPerformanceFrequency(&v); return v; }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return double(c.QuadPart) / double(f.QuadPart);
}

static void SaveBmp(const std::wstring& path, const uint8_t* src, UINT pitch, UINT w, UINT h) {
    BITMAPFILEHEADER fh{};
    BITMAPINFOHEADER ih{};
    ih.biSize = sizeof ih;
    ih.biWidth = LONG(w);
    ih.biHeight = -LONG(h);
    ih.biPlanes = 1;
    ih.biBitCount = 32;
    fh.bfType = 0x4D42;
    fh.bfOffBits = sizeof fh + sizeof ih;
    fh.bfSize = fh.bfOffBits + w * h * 4;
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"wb") || !f) return;
    fwrite(&fh, sizeof fh, 1, f);
    fwrite(&ih, sizeof ih, 1, f);
    for (UINT y = 0; y < h; ++y) fwrite(src + size_t(y) * pitch, 4, w, f);
    fclose(f);
}

int wmain(int argc, wchar_t** argv) {
    int x = 0, y = 0, w = 1280, h = 720;
    double seconds = 8;
    std::wstring save;
    for (int i = 1; i + 1 < argc; i += 2) {
        const std::wstring k = argv[i];
        if (k == L"--x") x = _wtoi(argv[i + 1]);
        else if (k == L"--y") y = _wtoi(argv[i + 1]);
        else if (k == L"--w") w = _wtoi(argv[i + 1]);
        else if (k == L"--h") h = _wtoi(argv[i + 1]);
        else if (k == L"--seconds") seconds = _wtof(argv[i + 1]);
        else if (k == L"--save") save = argv[i + 1];
    }
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);

    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev,
                                 nullptr, &ctx))) { std::puts("{\"error\":\"device\"}"); return 1; }
    ComPtr<IDXGIDevice> dxgi;
    dev.As(&dxgi);
    ComPtr<IDXGIAdapter> adapter;
    dxgi->GetAdapter(&adapter);
    ComPtr<IDXGIOutput> out;
    DXGI_OUTPUT_DESC desc{};
    const POINT center{x + w / 2, y + h / 2};
    for (UINT i = 0; adapter->EnumOutputs(i, out.ReleaseAndGetAddressOf()) != DXGI_ERROR_NOT_FOUND; ++i) {
        out->GetDesc(&desc);
        if (PtInRect(&desc.DesktopCoordinates, center)) break;
        out.Reset();
    }
    if (!out) { std::puts("{\"error\":\"output\"}"); return 1; }
    ComPtr<IDXGIOutput1> out1;
    out.As(&out1);
    ComPtr<IDXGIOutputDuplication> dup;
    if (FAILED(out1->DuplicateOutput(dev.Get(), &dup))) { std::puts("{\"error\":\"duplicate\"}"); return 1; }

    const int left = x - desc.DesktopCoordinates.left, top = y - desc.DesktopCoordinates.top;
    const int stripH = 8, stripY = top + h / 2 - stripH / 2;

    D3D11_TEXTURE2D_DESC td{};
    td.Width = UINT(w);
    td.Height = UINT(stripH);
    td.MipLevels = td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_STAGING;
    td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> strip;
    dev->CreateTexture2D(&td, nullptr, &strip);

    uint64_t last = 0;
    int distinct = 0, composed = 0, accumulated = 0;
    const double start = Now();
    while (Now() - start < seconds) {
        DXGI_OUTDUPL_FRAME_INFO info{};
        ComPtr<IDXGIResource> res;
        const HRESULT hr = dup->AcquireNextFrame(100, &info, &res);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) continue;
        if (FAILED(hr)) { std::printf("{\"error\":\"acquire 0x%08lX\"}\n", (unsigned long)hr); return 1; }
        if (info.LastPresentTime.QuadPart != 0) {
            ++composed;
            accumulated += int(info.AccumulatedFrames);
            ComPtr<ID3D11Texture2D> tex;
            res.As(&tex);
            D3D11_BOX box{UINT(left), UINT(stripY), 0, UINT(left + w), UINT(stripY + stripH), 1};
            ctx->CopySubresourceRegion(strip.Get(), 0, 0, 0, 0, tex.Get(), 0, &box);
            D3D11_MAPPED_SUBRESOURCE m{};
            if (SUCCEEDED(ctx->Map(strip.Get(), 0, D3D11_MAP_READ, 0, &m))) {
                uint64_t hash = 1469598103934665603ull;
                for (int row = 0; row < stripH; ++row) {
                    const auto* p = static_cast<const uint8_t*>(m.pData) + size_t(row) * m.RowPitch;
                    for (int i = 0; i < w * 4; ++i) { hash ^= p[i]; hash *= 1099511628211ull; }
                }
                ctx->Unmap(strip.Get(), 0);
                if (hash != last) { last = hash; ++distinct; }
            }
        }
        dup->ReleaseFrame();
    }
    const double dt = Now() - start;
    if (!save.empty()) {
        for (int i = 0; i < 40; ++i) {
            DXGI_OUTDUPL_FRAME_INFO info{};
            ComPtr<IDXGIResource> res;
            if (dup->AcquireNextFrame(100, &info, &res) != S_OK) continue;
            ComPtr<ID3D11Texture2D> tex;
            res.As(&tex);
            D3D11_TEXTURE2D_DESC fd = td;
            fd.Height = UINT(h);
            ComPtr<ID3D11Texture2D> full;
            dev->CreateTexture2D(&fd, nullptr, &full);
            D3D11_BOX box{UINT(left), UINT(top), 0, UINT(left + w), UINT(top + h), 1};
            ctx->CopySubresourceRegion(full.Get(), 0, 0, 0, 0, tex.Get(), 0, &box);
            D3D11_MAPPED_SUBRESOURCE m{};
            if (SUCCEEDED(ctx->Map(full.Get(), 0, D3D11_MAP_READ, 0, &m))) {
                SaveBmp(save, static_cast<const uint8_t*>(m.pData), m.RowPitch, UINT(w), UINT(h));
                ctx->Unmap(full.Get(), 0);
            }
            dup->ReleaseFrame();
            break;
        }
    }
    std::printf("{\"seconds\":%.3f,\"distinct\":%d,\"composed\":%d,\"accumulated\":%d,\"fps\":%.2f}\n", dt, distinct,
                composed, accumulated, distinct / dt);
    return 0;
}
