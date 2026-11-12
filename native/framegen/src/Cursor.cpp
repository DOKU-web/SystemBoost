#include "Cursor.h"
#include <atomic>
#include <cstdint>

namespace sbfg {

static const UINT kSystemCursors[] = {32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644,
                                      32645, 32646, 32648, 32649, 32650, 32651};
// OCR_NORMAL, IBEAM, WAIT, CROSS, UP, SIZENWSE, SIZENESW, SIZEWE, SIZENS,
// SIZEALL, NO, HAND, APPSTARTING, HELP

static std::atomic<bool> g_cursorsReplaced{false};

static std::atomic<bool> g_clipped{false};

static void RestoreSystemCursors() {
    if (g_cursorsReplaced.exchange(false)) SystemParametersInfoW(SPI_SETCURSORS, 0, nullptr, 0);
    if (g_clipped.exchange(false)) ClipCursor(nullptr);
}

static BOOL WINAPI ConsoleHandler(DWORD) { RestoreSystemCursors(); return FALSE; }
static LONG WINAPI CrashFilter(EXCEPTION_POINTERS*) { RestoreSystemCursors(); return EXCEPTION_CONTINUE_SEARCH; }

void InstallCursorSafety() {
    SetConsoleCtrlHandler(ConsoleHandler, TRUE);
    SetUnhandledExceptionFilter(CrashFilter);
}

void RestoreCursorState() { RestoreSystemCursors(); }

static HCURSOR BlankCursor() {
    BYTE andMask[32 * 4];
    BYTE xorMask[32 * 4];
    memset(andMask, 0xFF, sizeof andMask);
    memset(xorMask, 0x00, sizeof xorMask);
    return CreateCursor(GetModuleHandleW(nullptr), 0, 0, 32, 32, andMask, xorMask);
}

void CursorManager::Activate(const RECT* clip) {
    if (clip) UpdateClip(*clip);
    if (active_) return;
    active_ = true;
    original_.clear();
    for (UINT id : kSystemCursors) {
        HCURSOR sys = LoadCursorW(nullptr, MAKEINTRESOURCEW(id));
        HCURSOR copy = (HCURSOR)CopyIcon(sys);
        if (!copy) continue;
        HCURSOR blank = BlankCursor();
        if (blank && SetSystemCursor(blank, id)) {  // takes ownership of the blank
            g_cursorsReplaced = true;
            // the shared handle for this id now shows the blank image
            original_[LoadCursorW(nullptr, MAKEINTRESOURCEW(id))] = copy;
        } else {
            if (blank) DestroyCursor(blank);
            DestroyCursor(copy);
        }
    }
}

void CursorManager::UpdateClip(const RECT& clip) {
    ClipCursor(&clip);
    clipped_ = true;
    g_clipped = true;
}

void CursorManager::Restore() {
    if (clipped_) { clipped_ = false; g_clipped = false; ClipCursor(nullptr); }
    if (!active_) return;
    active_ = false;
    RestoreSystemCursors();
    for (auto& kv : original_) DestroyCursor(kv.second);
    original_.clear();
    cache_.clear();
}

bool CursorManager::Current(POINT& pos, const CursorImage*& img) {
    CURSORINFO ci{};
    ci.cbSize = sizeof ci;
    if (!GetCursorInfo(&ci) || !(ci.flags & CURSOR_SHOWING) || !ci.hCursor) return false;
    pos = ci.ptScreenPos;
    HCURSOR h = ci.hCursor;
    auto it = original_.find(h);
    if (it != original_.end()) h = it->second;  // draw the real shape, not the blank
    img = ImageFor(h);
    return img != nullptr;
}

const CursorImage* CursorManager::ImageFor(HCURSOR h) {
    auto it = cache_.find(h);
    if (it != cache_.end()) return &it->second;
    if (cache_.size() > 32) cache_.clear();  // games that create cursors dynamically

    ICONINFO ii{};
    if (!GetIconInfo(h, &ii)) return nullptr;
    BITMAP bm{};
    GetObjectW(ii.hbmMask, sizeof bm, &bm);
    const bool mono = ii.hbmColor == nullptr;
    const int w = bm.bmWidth, h2 = mono ? bm.bmHeight / 2 : bm.bmHeight;
    HDC dc = GetDC(nullptr);
    auto read = [&](HBITMAP b, int height, std::vector<uint32_t>& px) {
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof bi.bmiHeader;
        bi.bmiHeader.biWidth = w;
        bi.bmiHeader.biHeight = -height;  // top-down
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        px.assign(size_t(w) * height, 0);
        GetDIBits(dc, b, 0, UINT(height), px.data(), &bi, DIB_RGB_COLORS);
    };
    std::vector<uint32_t> mask, color;
    read(ii.hbmMask, mono ? h2 * 2 : h2, mask);
    if (!mono) read(ii.hbmColor, h2, color);
    ReleaseDC(nullptr, dc);

    bool anyAlpha = false;
    for (uint32_t c : color) if (c >> 24) { anyAlpha = true; break; }

    std::vector<uint16_t> half(size_t(w) * h2 * 4);
    auto toHalf = [](float f) -> uint16_t {  // positive/negative small values only
        uint32_t x; memcpy(&x, &f, 4);
        uint32_t s = (x >> 16) & 0x8000, e = ((x >> 23) & 0xFF), m = x & 0x7FFFFF;
        if (e == 0) return uint16_t(s);
        int ee = int(e) - 127 + 15;
        if (ee <= 0) return uint16_t(s);
        if (ee >= 31) return uint16_t(s | 0x7C00);
        return uint16_t(s | (ee << 10) | (m >> 13));
    };
    for (int y = 0; y < h2; ++y)
        for (int x = 0; x < w; ++x) {
            size_t i = size_t(y) * w + x;
            float r, g, b, a;
            bool andBit = (mask[i] & 0xFFFFFF) != 0;
            if (mono) {
                bool xorBit = (mask[size_t(y + h2) * w + x] & 0xFFFFFF) != 0;
                if (!andBit) { r = g = b = xorBit ? 1.f : 0.f; a = 1; }
                else if (xorBit) { r = g = b = 0; a = -1; }  // invert screen
                else { r = g = b = 0; a = 0; }
            } else {
                uint32_t c = color[i];
                b = (c & 0xFF) / 255.f; g = ((c >> 8) & 0xFF) / 255.f; r = ((c >> 16) & 0xFF) / 255.f;
                a = anyAlpha ? (c >> 24) / 255.f : (andBit ? 0.f : 1.f);
                if (!anyAlpha && andBit && (c & 0xFFFFFF)) { r = g = b = 0; a = -1; }
            }
            half[i * 4 + 0] = toHalf(r); half[i * 4 + 1] = toHalf(g);
            half[i * 4 + 2] = toHalf(b); half[i * 4 + 3] = toHalf(a);
        }
    if (ii.hbmMask) DeleteObject(ii.hbmMask);
    if (ii.hbmColor) DeleteObject(ii.hbmColor);

    CursorImage img;
    img.w = UINT(w); img.h = UINT(h2);
    img.hotX = int(ii.xHotspot); img.hotY = int(ii.yHotspot);
    D3D11_TEXTURE2D_DESC d{};
    d.Width = img.w; d.Height = img.h; d.MipLevels = 1; d.ArraySize = 1;
    d.Format = DXGI_FORMAT_R16G16B16A16_FLOAT; d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_IMMUTABLE; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA sd{half.data(), img.w * 8, 0};
    if (FAILED(dev_->CreateTexture2D(&d, &sd, &img.tex))) return nullptr;
    if (FAILED(dev_->CreateShaderResourceView(img.tex.Get(), nullptr, &img.srv))) return nullptr;
    return &(cache_[h] = img);
}

}  // namespace sbfg
