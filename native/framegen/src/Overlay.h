// Click-through topmost output window (DirectComposition + flip-model swap chain).
// It never takes focus or input; all mouse/keyboard input goes to the game.
#pragma once
#include "Win.h"

struct IDCompositionDevice;
struct IDCompositionTarget;
struct IDCompositionVisual;

namespace sbfg {

class Overlay {
public:
    Overlay(ID3D11Device* dev, ID3D11DeviceContext* ctx, bool showInCapture = false);
    ~Overlay();

    // Place the window over `r` (screen coords) with a back buffer of that size.
    void SetRect(const RECT& r);
    const RECT& Rect() const { return rect_; }

    // Copy `tex` (R8G8B8A8) to (x,y) of the back buffer and present on the next vblank.
    // If `clear` is true the rest of the back buffer is black (letterbox).
    // Throws HrError on device removed/reset (the caller exits and hides the overlay).
    // noVsync: for rates above the refresh rate. The newest frame replaces any not shown yet.
    void Present(ID3D11Texture2D* tex, UINT x, UINT y, bool clear, bool noVsync);

    void Show(bool on);
    bool Visible() const { return visible_; }
    // Running count of images that reached the screen; false if the OS does not report it.
    bool ShownCount(UINT& count);
    HWND Hwnd() const { return hwnd_; }
    HANDLE FrameLatencyWaitable() const { return waitable_; }

    // Time inside Present() since the last call, for the stats: where an output frame waits.
    struct Timing { double waitMs = 0, presentMs = 0, maxMs = 0; int count = 0; };
    Timing TakeTiming();

private:
    void CreateSwapChain(UINT w, UINT h);

    ID3D11Device* dev_;
    ID3D11DeviceContext* ctx_;
    HWND hwnd_ = nullptr;
    RECT rect_{};
    bool visible_ = false;
    ComPtr<IDXGISwapChain1> swap_;
    ComPtr<ID3D11RenderTargetView> rtv_;
    HANDLE waitable_ = nullptr;
    Timing timing_;
    IDCompositionDevice* dcomp_ = nullptr;
    IDCompositionTarget* target_ = nullptr;
    IDCompositionVisual* visual_ = nullptr;
};

}  // namespace sbfg
