// Screen capture sources. No injection: only OS capture APIs
// (Windows.Graphics.Capture / DXGI Desktop Duplication).
#pragma once
#include "Win.h"
#include <memory>

namespace sbfg {

struct CapturedFrame {
    ID3D11Texture2D* texture = nullptr;  // valid until Release()
    D3D11_BOX box{};                     // game client area inside texture
    double time = 0;                     // QPC seconds when the game presented it
    int frames = 1;                      // the game's frames since the last one returned, with this one
};

class ICapture {
public:
    virtual ~ICapture() = default;
    // Non-blocking. Returns true and fills `f` if a new frame is available. Safe while a frame
    // is held: it is replaced by the new one, or stays, or if it cannot stay f.texture is cleared.
    virtual bool TryGet(CapturedFrame& f) = 0;
    // Release the frame returned by TryGet (after copying it).
    virtual void Release() = 0;
    virtual const char* Name() const = 0;
};

// Client rect of hwnd in screen coordinates.
RECT ClientRectScreen(HWND hwnd);

// DXGI Desktop Duplication on the output that shows hwnd. `dev` must be on the
// adapter that drives that monitor. Our overlay is excluded from capture with
// WDA_EXCLUDEFROMCAPTURE, so it never captures its own output.
std::unique_ptr<ICapture> CreateDxgiCapture(ID3D11Device* dev, HWND hwnd);

#ifdef SBFG_WITH_WGC
// Windows.Graphics.Capture of the window. `dev` can be on any adapter; the OS
// delivers frames to it (this is the preferred path for dual GPU).
std::unique_ptr<ICapture> CreateWgcCapture(ID3D11Device* dev, HWND hwnd, bool cursor);
#endif

// Copies frames from a texture on one adapter to a texture on another
// (staging + upload). Used only when the capture device is not the frame
// generation device (DXGI capture + --gen-gpu on another adapter).
class CrossAdapterCopy {
public:
    CrossAdapterCopy(ID3D11Device* src, ID3D11DeviceContext* srcCtx, ID3D11Device* dst,
                     ID3D11DeviceContext* dstCtx);
    // Copies `box` of `tex` (src adapter); returns texture on dst adapter (box at 0,0).
    ID3D11Texture2D* Copy(ID3D11Texture2D* tex, const D3D11_BOX& box);
    double LastMs() const { return lastMs_; }

private:
    ID3D11Device* srcDev_; ID3D11DeviceContext* srcCtx_;
    ID3D11Device* dstDev_; ID3D11DeviceContext* dstCtx_;
    ComPtr<ID3D11Texture2D> staging_, upload_;
    UINT w_ = 0, h_ = 0;
    double lastMs_ = 0;
};

}  // namespace sbfg
