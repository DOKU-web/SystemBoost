// SystemBoost FrameGen v0.6 - settings + command line parsing (no Windows deps).
#pragma once
#include "Pacer.h"
#include <cstdint>
#include <cstdlib>
#include <string>
#include <vector>

namespace sbfg {

enum class Quality { Low, Performance, Balanced, Quality };    // lowest first: steps down by one
enum class GenGpu { SameAsGame, Integrated, Index, Warp };
enum class CaptureApi { Wgc, Dxgi };

struct Settings {
    uint64_t hwnd = 0;                 // target game window (required unless listGpus)
    MulMode mode = MulMode::Auto;      // auto | x2 | x3 | x4 | x6 | x8 | off
    int maxMul = 4;                    // auto upper bound (2..8)
    double targetFps = 0;              // output rate the user chose, 0 = follow the monitor
    Quality quality = Quality::Balanced;
    bool upscale = false;              // scale game window to its monitor (aspect kept)
    float sharpness = 0.5f;            // 0..1, upscaling only
    GenGpu genGpu = GenGpu::SameAsGame;
    int genGpuIndex = -1;              // with GenGpu::Index
    CaptureApi capture = CaptureApi::Wgc;
    bool captureCursor = false;        // WGC: include cursor in the captured image
    bool clipCursor = true;            // with upscale: keep the real cursor inside the game
    int statsMs = 500;                 // stats line interval (stdout JSON)
    bool listGpus = false;
    bool listWindows = false;
    bool alwaysActive = false;         // tests only: keep generating when the window is not in front
    bool showInCapture = false;        // tests only: let screenshots see the overlay
    std::string shaderDir;             // default: <exe dir>\shaders

    // Pipeline parameters derived from quality (mirror ref PARAMS)
    int procFactor() const { return quality == Quality::Low ? 8 : quality == Quality::Performance ? 4 : 2; }
    int refineIters() const { return quality == Quality::Quality ? 2 : 1; }
    int coarseRadius() const { return quality == Quality::Low || quality == Quality::Performance ? 4 : 6; }
};

inline std::string parseArgs(const std::vector<std::string>& a, Settings& s) {
    auto need = [&](size_t& i) -> const std::string* {
        return i + 1 < a.size() ? &a[++i] : nullptr;
    };
    for (size_t i = 0; i < a.size(); ++i) {
        const std::string& k = a[i];
        const std::string* v = nullptr;
        if (k == "--list-gpus") { s.listGpus = true; continue; }
        if (k == "--list-windows") { s.listWindows = true; continue; }
        if (k == "--capture-cursor") { s.captureCursor = true; continue; }
        if (k == "--no-clip-cursor") { s.clipCursor = false; continue; }
        if (k == "--upscale") { s.upscale = true; continue; }
        if (k == "--always-active") { s.alwaysActive = true; continue; }
        if (k == "--show-in-capture") { s.showInCapture = true; continue; }
        if (!(v = need(i))) return "missing value for " + k;
        if (k == "--hwnd") s.hwnd = std::strtoull(v->c_str(), nullptr, 0);
        else if (k == "--mode") {
            if (*v == "auto") s.mode = MulMode::Auto;
            else if (*v == "x2") s.mode = MulMode::X2;
            else if (*v == "x3") s.mode = MulMode::X3;
            else if (*v == "x4") s.mode = MulMode::X4;
            else if (*v == "x6") s.mode = MulMode::X6;
            else if (*v == "x8") s.mode = MulMode::X8;
            else if (*v == "off") s.mode = MulMode::Off;
            else return "bad --mode " + *v;
        } else if (k == "--target-fps") {
            s.targetFps = std::atof(v->c_str());
            if (s.targetFps != 0 && (s.targetFps < 10 || s.targetFps > 1000)) return "--target-fps must be 0 or 10..1000";
        } else if (k == "--max-mul") {
            s.maxMul = std::atoi(v->c_str());
            if (s.maxMul < 2 || s.maxMul > 8) return "--max-mul must be 2..8";
        } else if (k == "--quality") {
            if (*v == "low") s.quality = Quality::Low;
            else if (*v == "performance") s.quality = Quality::Performance;
            else if (*v == "balanced") s.quality = Quality::Balanced;
            else if (*v == "quality") s.quality = Quality::Quality;
            else return "bad --quality " + *v;
        } else if (k == "--sharpness") {
            s.sharpness = float(std::atof(v->c_str()));
            if (s.sharpness < 0 || s.sharpness > 1) return "--sharpness must be 0..1";
        } else if (k == "--gen-gpu") {
            if (*v == "same") s.genGpu = GenGpu::SameAsGame;
            else if (*v == "igpu") s.genGpu = GenGpu::Integrated;
            else if (*v == "warp") s.genGpu = GenGpu::Warp;    // tests only: stands in for a slow GPU
            else { s.genGpu = GenGpu::Index; s.genGpuIndex = std::atoi(v->c_str()); }
        } else if (k == "--capture") {
            if (*v == "wgc") s.capture = CaptureApi::Wgc;
            else if (*v == "dxgi") s.capture = CaptureApi::Dxgi;
            else return "bad --capture " + *v;
        } else if (k == "--stats-ms") s.statsMs = std::max(100, std::atoi(v->c_str()));
        else if (k == "--shader-dir") s.shaderDir = *v;
        else return "unknown option " + k;
    }
    if (!s.listGpus && !s.listWindows && s.hwnd == 0) return "--hwnd is required";
    return {};
}

}  // namespace sbfg
