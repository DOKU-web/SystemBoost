// GPU frame generation pipeline (D3D11 compute). Mirrors ref/fg_ref.py.
#pragma once
#include "Win.h"
#include "Settings.h"
#include "FgConstants.h"
#include <array>
#include <vector>

namespace sbfg {



struct Tex {
    ComPtr<ID3D11Texture2D> tex;
    ComPtr<ID3D11ShaderResourceView> srv;
    ComPtr<ID3D11UnorderedAccessView> uav;
    UINT w = 0, h = 0;
};

class FrameGenGpu {
public:
    FrameGenGpu(ID3D11Device* dev, ID3D11DeviceContext* ctx, const std::wstring& shaderDir);

    // (Re)allocate for a capture size and a presentation size. With upscale=true
    // the resample/sharpen/cursor pass always runs (also when out == cap).
    void Resize(UINT capW, UINT capH, UINT outW, UINT outH, const Settings& s);
    // Forget the frame pair (after a pause / alt-tab).
    void ResetHistory() { frames_ = 0; havePrevFwd_ = false; }
    UINT CapW() const { return capW_; }
    UINT CapH() const { return capH_; }

    // Copy a captured frame (region `box` of src, BGRA8) and analyse the new pair.
    void PushFrame(ID3D11Texture2D* src, const D3D11_BOX& box);
    bool HasPair() const { return frames_ >= 2; }

    // Render the frame at t (0..1) between the last two frames.
    // Returns the output texture (R8G8B8A8, outW x outH).
    ID3D11Texture2D* Generate(float t);

    // Last measured GPU time of one Generate() call and of the analysis (ms).
    double GenMs() const { return genMs_; }
    double AnalyzeMs() const { return analyzeMs_; }

    void SetSharpness(float s) { sharpness_ = s; }
    // Cursor drawn on top of the upscaled image (nullptr = none). Output pixels.
    void SetCursor(ID3D11ShaderResourceView* img, int x, int y, UINT w, UINT h) {
        cursorSrv_ = img;  // ComPtr: keeps the view alive even if the cursor cache is cleared
        cursorX_ = x; cursorY_ = y; cursorW_ = w; cursorH_ = h;
    }
    UINT OutW() const { return outW_; }
    UINT OutH() const { return outH_; }

private:
    struct Shader { ComPtr<ID3D11ComputeShader> cs; };
    Shader Load(const wchar_t* name);
    Tex MakeTex(UINT w, UINT h, DXGI_FORMAT fmt, bool uav, bool srv = true);
    void SetConstants(const FgConstants& c);
    void Run(const Shader& s, std::initializer_list<ID3D11ShaderResourceView*> srvs,
             std::initializer_list<ID3D11UnorderedAccessView*> uavs, UINT gx, UINT gy);
    FgConstants Base() const;
    void BuildPyramid(int slot);
    void Motion(int from, int to, std::vector<Tex>& tmp, std::vector<Tex>& out,
                const Tex* temporal, bool stats);

    ID3D11Device* dev_;
    ID3D11DeviceContext* ctx_;
    std::wstring dir_;
    Shader sLuma_, sPyr_, sSearch_, sMedian_, sStatic_, sInterp_, sUp_, sSharp_;
    ComPtr<ID3D11Buffer> cb_;
    ComPtr<ID3D11SamplerState> linear_;

    UINT capW_ = 0, capH_ = 0, outW_ = 0, outH_ = 0, procW_ = 0, procH_ = 0;
    int procFactor_ = 2, refine_ = 1, radius_ = 6;
    float sharpness_ = 0.5f;
    ComPtr<ID3D11ShaderResourceView> cursorSrv_;
    int cursorX_ = 0, cursorY_ = 0;
    UINT cursorW_ = 0, cursorH_ = 0;
    std::array<Tex, 2> frame_;                  // ring of captured frames
    std::array<std::vector<Tex>, 2> pyr_;       // luma pyramid per frame slot
    std::vector<Tex> fwdTmp_, fwd_, bwdTmp_, bwd_;
    Tex prevFwd_;                               // temporal candidate
    bool havePrevFwd_ = false;
    Tex staticMap_, gen_, upTmp_, out_;
    ComPtr<ID3D11Buffer> stats_;
    ComPtr<ID3D11UnorderedAccessView> statsUav_;
    ComPtr<ID3D11ShaderResourceView> statsSrv_;
    int cur_ = 0;   // slot of the newest frame (B)
    int frames_ = 0;

    // GPU timing (non-blocking timestamp queries)
    struct TimerSet { ComPtr<ID3D11Query> disjoint, begin, end; bool pending = false; bool gen = false; };
    std::array<TimerSet, 8> timers_;
    int timerPos_ = 0;
    void TimerBegin(bool gen);
    void TimerEnd();
    void TimerPoll();
    double genMs_ = 0, analyzeMs_ = 0;
};

}  // namespace sbfg
