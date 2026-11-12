// Cursor handling for upscaling mode.
// The real cursor is confined to the game's client area (so clicks land in the
// game) and the standard system cursors are swapped for blank ones while
// active; we draw the current cursor shape at the scaled position instead.
// Restore() is called on every exit path (normal exit, console close, crash
// filter). If the process is killed, the launcher must call
// SystemParametersInfo(SPI_SETCURSORS, 0, nullptr, 0) - see README.
#pragma once
#include "Win.h"
#include <map>
#include <vector>

namespace sbfg {

struct CursorImage {
    ComPtr<ID3D11Texture2D> tex;           // R16G16B16A16_FLOAT, a < 0 = invert pixel
    ComPtr<ID3D11ShaderResourceView> srv;
    UINT w = 0, h = 0;
    int hotX = 0, hotY = 0;
};

class CursorManager {
public:
    explicit CursorManager(ID3D11Device* dev) : dev_(dev) {}
    ~CursorManager() { Restore(); }

    // Hide system cursors (we draw the cursor instead); clip is optional.
    void Activate(const RECT* clipScreen);
    void UpdateClip(const RECT& clipScreen);
    void Restore();                         // idempotent; unclips only if we clipped
    bool Active() const { return active_; }

    // Current cursor (screen position + image), or false if hidden.
    bool Current(POINT& pos, const CursorImage*& img);

private:
    const CursorImage* ImageFor(HCURSOR h);
    ID3D11Device* dev_;
    bool active_ = false;
    bool clipped_ = false;
    std::map<HCURSOR, HCURSOR> original_;  // replaced system handle -> saved copy
    std::map<HCURSOR, CursorImage> cache_;
};

// Process-wide emergency restore (console close / unhandled exception).
void InstallCursorSafety();

// Puts back what this process changed (system cursor scheme, cursor clip), from any thread. For the
// watchdog in main.cpp, which ends the process when its main loop cannot.
void RestoreCursorState();

}  // namespace sbfg
