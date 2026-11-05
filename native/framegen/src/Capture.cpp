#include "Capture.h"
#include <vector>

namespace sbfg {

RECT ClientRectScreen(HWND hwnd) {
    RECT r{};
    GetClientRect(hwnd, &r);
    POINT tl{r.left, r.top}, br{r.right, r.bottom};
    ClientToScreen(hwnd, &tl);
    ClientToScreen(hwnd, &br);
    return {tl.x, tl.y, br.x, br.y};
}

// ---------------------------------------------------------------- DXGI DDA
class DxgiCapture final : public ICapture {
public:
    DxgiCapture(ID3D11Device* dev, HWND hwnd) : dev_(dev), hwnd_(hwnd) { Open(); }

    bool TryGet(CapturedFrame& f) override {
        if (held_) return false;  // one frame at a time, and nothing tells how many went by
        // game moved to another monitor -> duplicate that output instead
        if (MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST) != mon_) dup_.Reset();
        if (!dup_) {
            const double now = QpcNow();
            if (now - lastOpen_ < 0.25) return false;  // back off (secure desktop, mode change)
            lastOpen_ = now;
            Open();
            if (!dup_) return false;
        }
        DXGI_OUTDUPL_FRAME_INFO info{};
        ComPtr<IDXGIResource> res;
        HRESULT hr = dup_->AcquireNextFrame(0, &info, &res);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return false;
        if (hr == DXGI_ERROR_ACCESS_LOST || FAILED(hr)) {  // mode change, UAC desktop, etc.
            dup_.Reset();
            return false;
        }
        held_ = true;
        if (info.LastPresentTime.QuadPart == 0) {  // pointer-only update
            Release();
            return false;
        }
        ComPtr<ID3D11Texture2D> tex;
        if (FAILED(res.As(&tex))) { Release(); return false; }
        tex_ = tex;
        RECT c = ClientRectScreen(hwnd_);
        D3D11_TEXTURE2D_DESC d{};
        tex->GetDesc(&d);
        LONG l = std::max<LONG>(0, c.left - origin_.x), t = std::max<LONG>(0, c.top - origin_.y);
        LONG r = std::min<LONG>(LONG(d.Width), c.right - origin_.x);
        LONG b = std::min<LONG>(LONG(d.Height), c.bottom - origin_.y);
        if (r <= l || b <= t) { Release(); return false; }
        // ignore desktop updates that do not touch the game (e.g. other windows)
        if (info.TotalMetadataBufferSize > 0) {
            std::vector<RECT> dirty(info.TotalMetadataBufferSize / sizeof(RECT) + 1);
            UINT bytes = 0;
            if (SUCCEEDED(dup_->GetFrameDirtyRects(UINT(dirty.size() * sizeof(RECT)), dirty.data(), &bytes))) {
                bool hit = false;
                for (UINT i = 0; i < bytes / sizeof(RECT) && !hit; ++i)
                    hit = dirty[i].left < r && dirty[i].right > l && dirty[i].top < b && dirty[i].bottom > t;
                if (bytes > 0 && !hit) { Release(); return false; }
            }
        }
        f.texture = tex.Get();
        f.box = {UINT(l), UINT(t), 0, UINT(r), UINT(b), 1};
        f.time = QpcToSec(info.LastPresentTime.QuadPart);
        return true;
    }

    void Release() override {
        if (held_ && dup_) dup_->ReleaseFrame();
        held_ = false;
        tex_.Reset();
    }
    const char* Name() const override { return "dxgi"; }

private:
    void Open() {
        dup_.Reset();
        ComPtr<IDXGIDevice> dxgiDev;
        if (FAILED(dev_->QueryInterface(IID_PPV_ARGS(&dxgiDev)))) return;
        ComPtr<IDXGIAdapter> adapter;
        if (FAILED(dxgiDev->GetAdapter(&adapter))) return;
        HMONITOR mon = MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST);
        mon_ = mon;
        ComPtr<IDXGIOutput> out;
        for (UINT i = 0; adapter->EnumOutputs(i, &out) != DXGI_ERROR_NOT_FOUND; ++i, out.Reset()) {
            DXGI_OUTPUT_DESC od{};
            out->GetDesc(&od);
            if (od.Monitor != mon) continue;
            ComPtr<IDXGIOutput1> o1;
            if (FAILED(out.As(&o1))) return;
            if (SUCCEEDED(o1->DuplicateOutput(dev_, &dup_))) {
                origin_ = {od.DesktopCoordinates.left, od.DesktopCoordinates.top};
            }
            return;
        }
    }

    ID3D11Device* dev_;
    HWND hwnd_;
    ComPtr<IDXGIOutputDuplication> dup_;
    ComPtr<ID3D11Texture2D> tex_;
    POINT origin_{};
    HMONITOR mon_ = nullptr;
    double lastOpen_ = -1;
    bool held_ = false;
};

std::unique_ptr<ICapture> CreateDxgiCapture(ID3D11Device* dev, HWND hwnd) {
    return std::make_unique<DxgiCapture>(dev, hwnd);
}

// ------------------------------------------------------- cross adapter copy
CrossAdapterCopy::CrossAdapterCopy(ID3D11Device* src, ID3D11DeviceContext* srcCtx, ID3D11Device* dst,
                                   ID3D11DeviceContext* dstCtx)
    : srcDev_(src), srcCtx_(srcCtx), dstDev_(dst), dstCtx_(dstCtx) {}

ID3D11Texture2D* CrossAdapterCopy::Copy(ID3D11Texture2D* tex, const D3D11_BOX& box) {
    const double t0 = QpcNow();
    const UINT w = box.right - box.left, h = box.bottom - box.top;
    if (w != w_ || h != h_) {
        D3D11_TEXTURE2D_DESC d{};
        d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
        d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
        d.Usage = D3D11_USAGE_STAGING; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        staging_.Reset(); upload_.Reset();
        Check(srcDev_->CreateTexture2D(&d, nullptr, &staging_), "staging");
        d.Usage = D3D11_USAGE_DEFAULT; d.CPUAccessFlags = 0; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        Check(dstDev_->CreateTexture2D(&d, nullptr, &upload_), "upload");
        w_ = w; h_ = h;
    }
    srcCtx_->CopySubresourceRegion(staging_.Get(), 0, 0, 0, 0, tex, 0, &box);
    srcCtx_->Flush();
    D3D11_MAPPED_SUBRESOURCE m{};
    // poll instead of blocking inside the driver; the copy is a few hundred microseconds
    HRESULT hr;
    while ((hr = srcCtx_->Map(staging_.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &m)) ==
           DXGI_ERROR_WAS_STILL_DRAWING) {
        if (QpcNow() - t0 > 0.050) throw HrError{DXGI_ERROR_WAS_STILL_DRAWING, "cross-adapter copy timeout"};
        Sleep(0);
    }
    Check(hr, "Map staging");
    dstCtx_->UpdateSubresource(upload_.Get(), 0, nullptr, m.pData, m.RowPitch, 0);
    srcCtx_->Unmap(staging_.Get(), 0);
    lastMs_ = (QpcNow() - t0) * 1000.0;
    return upload_.Get();
}

}  // namespace sbfg
