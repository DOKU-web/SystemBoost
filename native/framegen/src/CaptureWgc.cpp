// Windows.Graphics.Capture source (MSVC + C++/WinRT, Windows 10 2004+).
// Built only when SBFG_WITH_WGC is defined (see CMakeLists.txt).
#ifdef SBFG_WITH_WGC
#include "Capture.h"
#include <dwmapi.h>
#include <inspectable.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

namespace wgc = winrt::Windows::Graphics::Capture;
namespace wd3d = winrt::Windows::Graphics::DirectX::Direct3D11;

namespace sbfg {

class WgcCapture final : public ICapture {
public:
    WgcCapture(ID3D11Device* dev, HWND hwnd, bool cursor) : hwnd_(hwnd) {
        ComPtr<IDXGIDevice> dxgi;
        Check(dev->QueryInterface(IID_PPV_ARGS(&dxgi)), "IDXGIDevice");
        winrt::com_ptr<::IInspectable> insp;
        Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), insp.put()), "CreateDirect3D11DeviceFromDXGIDevice");
        device_ = insp.as<wd3d::IDirect3DDevice>();

        auto interop = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        Check(interop->CreateForWindow(hwnd, winrt::guid_of<wgc::GraphicsCaptureItem>(),
                                       winrt::put_abi(item_)),
              "CreateForWindow");
        size_ = item_.Size();
        pool_ = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
            device_, winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size_);
        session_ = pool_.CreateCaptureSession(item_);
        using winrt::Windows::Foundation::Metadata::ApiInformation;
        if (ApiInformation::IsPropertyPresent(L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsCursorCaptureEnabled"))
            session_.IsCursorCaptureEnabled(cursor);
        if (ApiInformation::IsPropertyPresent(L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsBorderRequired")) {
            try { session_.IsBorderRequired(false); }  // Windows 11: no yellow border
            catch (const winrt::hresult_error&) {}       // not allowed -> keep the border
        }
        session_.StartCapture();
    }

    ~WgcCapture() override {
        Release();
        if (session_) session_.Close();
        if (pool_) pool_.Close();
    }

    bool TryGet(CapturedFrame& f) override {
        try { return TryGetImpl(f); }
        catch (const winrt::hresult_error&) { Release(); return false; }  // window closing, device lost
    }

    bool TryGetImpl(CapturedFrame& f) {
        // drain to the newest frame (older ones would only add latency)
        wgc::Direct3D11CaptureFrame newest{nullptr};
        while (auto fr = pool_.TryGetNextFrame()) newest = fr;
        if (!newest) return false;
        frame_ = newest;
        auto cs = frame_.ContentSize();
        if (cs.Width != size_.Width || cs.Height != size_.Height) {  // window resized
            size_ = cs;
            pool_.Recreate(device_, winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size_);
        }
        auto access = frame_.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        tex_.Reset();
        Check(access->GetInterface(IID_PPV_ARGS(&tex_)), "GetInterface(tex)");

        // client area inside the captured window image (which starts at the
        // DWM extended frame bounds)
        RECT frameR{};
        DwmGetWindowAttribute(hwnd_, DWMWA_EXTENDED_FRAME_BOUNDS, &frameR, sizeof frameR);
        RECT c = ClientRectScreen(hwnd_);
        D3D11_TEXTURE2D_DESC d{};
        tex_->GetDesc(&d);
        LONG l = std::max<LONG>(0, c.left - frameR.left), t = std::max<LONG>(0, c.top - frameR.top);
        LONG r = std::min<LONG>(LONG(std::min<UINT>(d.Width, UINT(cs.Width))), c.right - frameR.left);
        LONG b = std::min<LONG>(LONG(std::min<UINT>(d.Height, UINT(cs.Height))), c.bottom - frameR.top);
        if (r <= l || b <= t) { Release(); return false; }
        f.texture = tex_.Get();
        f.box = {UINT(l), UINT(t), 0, UINT(r), UINT(b), 1};
        // SystemRelativeTime = QPC converted to 100 ns ticks (same clock as QpcNow)
        f.time = double(frame_.SystemRelativeTime().count()) * 1e-7;
        return true;
    }

    void Release() override {
        tex_.Reset();
        if (frame_) { frame_.Close(); frame_ = nullptr; }
    }
    const char* Name() const override { return "wgc"; }

private:
    HWND hwnd_;
    wd3d::IDirect3DDevice device_{nullptr};
    wgc::GraphicsCaptureItem item_{nullptr};
    wgc::Direct3D11CaptureFramePool pool_{nullptr};
    wgc::GraphicsCaptureSession session_{nullptr};
    wgc::Direct3D11CaptureFrame frame_{nullptr};
    winrt::Windows::Graphics::SizeInt32 size_{};
    ComPtr<ID3D11Texture2D> tex_;
};

std::unique_ptr<ICapture> CreateWgcCapture(ID3D11Device* dev, HWND hwnd, bool cursor) {
    try {
        return std::make_unique<WgcCapture>(dev, hwnd, cursor);
    } catch (const winrt::hresult_error&) {
        return nullptr;  // caller falls back to DXGI capture
    } catch (const HrError&) {
        return nullptr;
    }
}

}  // namespace sbfg
#endif
