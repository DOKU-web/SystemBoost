#include "Overlay.h"
#include <dcomp.h>

#ifndef WDA_EXCLUDEFROMCAPTURE
#define WDA_EXCLUDEFROMCAPTURE 0x00000011
#endif

namespace sbfg {

static LRESULT CALLBACK OverlayProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_NCHITTEST: return HTTRANSPARENT;          // never take mouse input
    case WM_MOUSEACTIVATE: return MA_NOACTIVATE;       // never take focus
    case WM_ERASEBKGND: return 1;
    default: return DefWindowProcW(h, m, w, l);
    }
}

Overlay::Overlay(ID3D11Device* dev, ID3D11DeviceContext* ctx, bool showInCapture) : dev_(dev), ctx_(ctx) {
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof wc;
    wc.lpfnWndProc = OverlayProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"SystemBoostFrameGenOverlay";
    wc.hCursor = nullptr;
    RegisterClassExW(&wc);
    hwnd_ = CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT |
                                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                            wc.lpszClassName, L"SystemBoost FrameGen", WS_POPUP, 0, 0, 16, 16, nullptr,
                            nullptr, wc.hInstance, nullptr);
    if (!hwnd_) throw HrError{HRESULT_FROM_WIN32(GetLastError()), "CreateWindowEx"};
    SetLayeredWindowAttributes(hwnd_, 0, 255, LWA_ALPHA);
    // keep our own output out of every capture (no feedback loop with DXGI capture,
    // and recording software sees the game, not the generated frames)
    if (!showInCapture) SetWindowDisplayAffinity(hwnd_, WDA_EXCLUDEFROMCAPTURE);

    ComPtr<IDXGIDevice> dxgi;
    Check(dev_->QueryInterface(IID_PPV_ARGS(&dxgi)), "IDXGIDevice");
    Check(DCompositionCreateDevice(dxgi.Get(), __uuidof(IDCompositionDevice), reinterpret_cast<void**>(&dcomp_)),
          "DCompositionCreateDevice");
    Check(dcomp_->CreateTargetForHwnd(hwnd_, TRUE, &target_), "CreateTargetForHwnd");
    Check(dcomp_->CreateVisual(&visual_), "CreateVisual");
}

Overlay::~Overlay() {
    if (waitable_) CloseHandle(waitable_);
    if (visual_) visual_->Release();
    if (target_) target_->Release();
    if (dcomp_) dcomp_->Release();
    rtv_.Reset();
    swap_.Reset();
    if (hwnd_) DestroyWindow(hwnd_);
}

void Overlay::CreateSwapChain(UINT w, UINT h) {
    rtv_.Reset();
    if (swap_) {
        Check(swap_->ResizeBuffers(0, w, h, DXGI_FORMAT_UNKNOWN, DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT),
              "ResizeBuffers");
    } else {
        ComPtr<IDXGIDevice> dxgi;
        Check(dev_->QueryInterface(IID_PPV_ARGS(&dxgi)), "IDXGIDevice");
        ComPtr<IDXGIAdapter> ad;
        Check(dxgi->GetAdapter(&ad), "GetAdapter");
        ComPtr<IDXGIFactory2> f;
        Check(ad->GetParent(IID_PPV_ARGS(&f)), "IDXGIFactory2");
        DXGI_SWAP_CHAIN_DESC1 d{};
        d.Width = w; d.Height = h;
        d.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        d.SampleDesc.Count = 1;
        d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        d.BufferCount = 4;  // a rate above the refresh rate needs some free while one is on screen
        d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        d.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        Check(f->CreateSwapChainForComposition(dev_, &d, nullptr, &swap_), "CreateSwapChainForComposition");
        ComPtr<IDXGISwapChain2> s2;
        if (SUCCEEDED(swap_.As(&s2))) {
            s2->SetMaximumFrameLatency(1);
            waitable_ = s2->GetFrameLatencyWaitableObject();
        }
        Check(visual_->SetContent(swap_.Get()), "SetContent");
        Check(target_->SetRoot(visual_), "SetRoot");
        Check(dcomp_->Commit(), "Commit");
    }
    ComPtr<ID3D11Texture2D> bb;
    Check(swap_->GetBuffer(0, IID_PPV_ARGS(&bb)), "GetBuffer");
    Check(dev_->CreateRenderTargetView(bb.Get(), nullptr, &rtv_), "RTV");
}

void Overlay::SetRect(const RECT& r) {
    const UINT w = UINT(r.right - r.left), h = UINT(r.bottom - r.top);
    const bool resize = !swap_ || w != UINT(rect_.right - rect_.left) || h != UINT(rect_.bottom - rect_.top);
    rect_ = r;
    SetWindowPos(hwnd_, HWND_TOPMOST, r.left, r.top, int(w), int(h), SWP_NOACTIVATE);
    if (resize && w > 0 && h > 0) CreateSwapChain(w, h);
}

void Overlay::Present(ID3D11Texture2D* tex, UINT x, UINT y, bool clear, bool noVsync) {
    if (!swap_ || !tex) return;
    if (waitable_ && !noVsync) WaitForSingleObjectEx(waitable_, 20, FALSE);  // at most 1 frame queued
    ComPtr<ID3D11Texture2D> bb;
    if (FAILED(swap_->GetBuffer(0, IID_PPV_ARGS(&bb)))) return;
    if (clear) {
        const float black[4] = {0, 0, 0, 1};
        ctx_->ClearRenderTargetView(rtv_.Get(), black);
    }
    ctx_->CopySubresourceRegion(bb.Get(), 0, x, y, 0, tex, 0, nullptr);
    // vsync: the pacer already targets vblanks. Above the refresh rate the monitor shows what it
    // can and the rest is dropped here, or the output would wait for it and fall back to its rate.
    HRESULT hr = swap_->Present(noVsync ? 0 : 1, noVsync ? DXGI_PRESENT_DO_NOT_WAIT : 0);
    if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET) {
        Show(false);
        throw HrError{hr, "GPU device removed/reset"};
    }
}

bool Overlay::ShownCount(UINT& count) {
    DXGI_FRAME_STATISTICS st{};
    if (!swap_ || FAILED(swap_->GetFrameStatistics(&st))) return false;
    count = st.PresentCount;
    return true;
}

void Overlay::Show(bool on) {
    if (on == visible_) return;
    visible_ = on;
    ShowWindow(hwnd_, on ? SW_SHOWNOACTIVATE : SW_HIDE);
}

}  // namespace sbfg
