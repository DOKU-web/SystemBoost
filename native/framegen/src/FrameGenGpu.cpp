#include "FrameGenGpu.h"
#include <d3dcompiler.h>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <iterator>

namespace sbfg {

static UINT Groups(UINT n) { return (n + 7) / 8; }

FrameGenGpu::FrameGenGpu(ID3D11Device* dev, ID3D11DeviceContext* ctx, const std::wstring& dir)
    : dev_(dev), ctx_(ctx), dir_(dir) {
    sLuma_ = Load(L"luma_down");
    sPyr_ = Load(L"pyr_down");
    sSearch_ = Load(L"motion_search");
    sMedian_ = Load(L"motion_median");
    sStatic_ = Load(L"static_map");
    sInterp_ = Load(L"interpolate");
    sUp_ = Load(L"upscale");
    sSharp_ = Load(L"sharpen");

    D3D11_BUFFER_DESC bd{};
    bd.ByteWidth = sizeof(FgConstants);
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    Check(dev_->CreateBuffer(&bd, nullptr, &cb_), "constant buffer");

    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    Check(dev_->CreateSamplerState(&sd, &linear_), "sampler");

    // scene statistics: 3 uints, UAV (motion_median) + SRV (interpolate)
    D3D11_BUFFER_DESC sb{};
    sb.ByteWidth = 16;
    sb.Usage = D3D11_USAGE_DEFAULT;
    sb.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    sb.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    sb.StructureByteStride = 4;
    Check(dev_->CreateBuffer(&sb, nullptr, &stats_), "stats buffer");
    D3D11_UNORDERED_ACCESS_VIEW_DESC ud{};
    ud.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    ud.Buffer.NumElements = 4;
    Check(dev_->CreateUnorderedAccessView(stats_.Get(), &ud, &statsUav_), "stats uav");
    D3D11_SHADER_RESOURCE_VIEW_DESC vd{};
    vd.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
    vd.Buffer.NumElements = 4;
    Check(dev_->CreateShaderResourceView(stats_.Get(), &vd, &statsSrv_), "stats srv");

    for (auto& t : timers_) {
        D3D11_QUERY_DESC q{D3D11_QUERY_TIMESTAMP_DISJOINT, 0};
        dev_->CreateQuery(&q, &t.disjoint);
        q.Query = D3D11_QUERY_TIMESTAMP;
        dev_->CreateQuery(&q, &t.begin);
        dev_->CreateQuery(&q, &t.end);
    }
    for (auto& f : fences_) {
        D3D11_QUERY_DESC q{D3D11_QUERY_EVENT, 0};
        dev_->CreateQuery(&q, &f.done);
    }
}

FrameGenGpu::Shader FrameGenGpu::Load(const wchar_t* name) {
    Shader s;
    // 1) precompiled .cso (release builds, compiled by CMake with fxc)
    std::wstring cso = dir_ + L"\\" + name + L".cso";
    std::ifstream f(std::filesystem::path(cso), std::ios::binary);
    if (f) {
        std::vector<char> b((std::istreambuf_iterator<char>(f)), {});
        Check(dev_->CreateComputeShader(b.data(), b.size(), nullptr, &s.cs), "CreateComputeShader(cso)");
        return s;
    }
    // 2) development fallback: compile the .hlsl at startup
    std::wstring src = dir_ + L"\\" + name + L".hlsl";
    ComPtr<ID3DBlob> code, err;
    HRESULT hr = D3DCompileFromFile(src.c_str(), nullptr, D3D_COMPILE_STANDARD_FILE_INCLUDE, "main",
                                    "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &err);
    if (FAILED(hr)) {
        if (err) OutputDebugStringA(static_cast<const char*>(err->GetBufferPointer()));
        throw HrError{hr, "shader compile"};
    }
    Check(dev_->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &s.cs),
          "CreateComputeShader(hlsl)");
    return s;
}

Tex FrameGenGpu::MakeTex(UINT w, UINT h, DXGI_FORMAT fmt, bool uav, bool srv) {
    Tex t;
    t.w = w; t.h = h;
    D3D11_TEXTURE2D_DESC d{};
    d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1; d.Format = fmt;
    d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = (srv ? D3D11_BIND_SHADER_RESOURCE : 0) | (uav ? D3D11_BIND_UNORDERED_ACCESS : 0);
    Check(dev_->CreateTexture2D(&d, nullptr, &t.tex), "CreateTexture2D");
    if (srv) Check(dev_->CreateShaderResourceView(t.tex.Get(), nullptr, &t.srv), "SRV");
    if (uav) Check(dev_->CreateUnorderedAccessView(t.tex.Get(), nullptr, &t.uav), "UAV");
    return t;
}

void FrameGenGpu::Resize(UINT capW, UINT capH, UINT outW, UINT outH, const Settings& s) {
    procFactor_ = s.procFactor();
    refine_ = s.refineIters();
    radius_ = s.coarseRadius();
    sharpness_ = s.sharpness;
    capW_ = capW; capH_ = capH; outW_ = outW; outH_ = outH;
    procW_ = std::max<UINT>(16, capW / procFactor_);
    procH_ = std::max<UINT>(16, capH / procFactor_);

    for (auto& f : frame_) f = MakeTex(capW, capH, DXGI_FORMAT_B8G8R8A8_UNORM, false);
    // pyramid: proc size, halve while width/2 >= 64 (ref: min_level_w)
    std::vector<std::pair<UINT, UINT>> sizes{{procW_, procH_}};
    while (sizes.back().first / 2 >= 64) sizes.push_back({sizes.back().first / 2, sizes.back().second / 2});
    for (auto& p : pyr_) {
        p.clear();
        for (auto& sz : sizes) p.push_back(MakeTex(sz.first, sz.second, DXGI_FORMAT_R16_FLOAT, true));
    }
    auto fields = [&](std::vector<Tex>& v) {
        v.clear();
        for (auto& sz : sizes) v.push_back(MakeTex((sz.first + 7) / 8, (sz.second + 7) / 8, DXGI_FORMAT_R16G16B16A16_FLOAT, true));
    };
    fields(fwdTmp_); fields(fwd_); fields(bwdTmp_); fields(bwd_);
    prevFwd_ = MakeTex(fwd_[0].w, fwd_[0].h, DXGI_FORMAT_R16G16B16A16_FLOAT, false);
    havePrevFwd_ = false;
    staticMap_ = MakeTex(capW, capH, DXGI_FORMAT_R8_UNORM, true);
    gen_ = MakeTex(capW, capH, DXGI_FORMAT_R8G8B8A8_UNORM, true);
    if (s.upscale || outW != capW || outH != capH) {
        upTmp_ = MakeTex(outW, outH, DXGI_FORMAT_R16G16B16A16_FLOAT, true);
        out_ = MakeTex(outW, outH, DXGI_FORMAT_R8G8B8A8_UNORM, true);
    } else {
        upTmp_ = {};
        out_ = {};
    }
    frames_ = 0;
}

FgConstants FrameGenGpu::Base() const {
    FgConstants c{};
    c.capSize[0] = capW_; c.capSize[1] = capH_;
    c.procSize[0] = procW_; c.procSize[1] = procH_;
    c.radius = radius_;
    c.refineIters = UINT(refine_);
    c.lam = 0.004f; c.zeroBias = 0.002f;
    c.fpIters = 2; c.fbSigma = 1.5f; c.fbSigmaK = 0.10f;
    c.staticThr = 0.02f; c.cutErr = 0.07f; c.cutRatio = 0.60f;
    c.outSize[0] = outW_; c.outSize[1] = outH_;
    c.sharpness = sharpness_;
    c.cursorOn = cursorSrv_ != nullptr;
    c.cursorPos[0] = cursorX_; c.cursorPos[1] = cursorY_;
    c.cursorSize[0] = cursorW_; c.cursorSize[1] = cursorH_;
    return c;
}

void FrameGenGpu::SetConstants(const FgConstants& c) {
    D3D11_MAPPED_SUBRESOURCE m{};
    Check(ctx_->Map(cb_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &m), "Map cb");
    memcpy(m.pData, &c, sizeof c);
    ctx_->Unmap(cb_.Get(), 0);
}

void FrameGenGpu::Run(const Shader& s, std::initializer_list<ID3D11ShaderResourceView*> srvs,
                      std::initializer_list<ID3D11UnorderedAccessView*> uavs, UINT gx, UINT gy) {
    ID3D11ShaderResourceView* sv[8] = {};
    ID3D11UnorderedAccessView* uv[4] = {};
    UINT ns = 0, nu = 0;
    for (auto* p : srvs) sv[ns++] = p;
    for (auto* p : uavs) uv[nu++] = p;
    ctx_->CSSetShader(s.cs.Get(), nullptr, 0);
    ctx_->CSSetConstantBuffers(0, 1, cb_.GetAddressOf());
    ctx_->CSSetSamplers(0, 1, linear_.GetAddressOf());
    ctx_->CSSetShaderResources(0, ns, sv);
    ctx_->CSSetUnorderedAccessViews(0, nu, uv, nullptr);
    ctx_->Dispatch(gx, gy, 1);
    // unbind so the same resources can be used with the other view type next
    ID3D11ShaderResourceView* nsv[8] = {};
    ID3D11UnorderedAccessView* nuv[4] = {};
    ctx_->CSSetShaderResources(0, 8, nsv);
    ctx_->CSSetUnorderedAccessViews(0, 4, nuv, nullptr);
}

void FrameGenGpu::BuildPyramid(int slot) {
    auto& p = pyr_[slot];
    FgConstants c = Base();
    SetConstants(c);
    Run(sLuma_, {frame_[slot].srv.Get()}, {p[0].uav.Get()}, Groups(procW_), Groups(procH_));
    for (size_t l = 1; l < p.size(); ++l) {
        c.lvlSize[0] = p[l].w; c.lvlSize[1] = p[l].h;
        SetConstants(c);
        Run(sPyr_, {p[l - 1].srv.Get()}, {p[l].uav.Get()}, Groups(p[l].w), Groups(p[l].h));
    }
}

void FrameGenGpu::Motion(int from, int to, std::vector<Tex>& tmp, std::vector<Tex>& out,
                         const Tex* temporal, bool stats) {
    const auto& ps = pyr_[from];
    const auto& pd = pyr_[to];
    const int L = int(ps.size());
    for (int l = L - 1; l >= 0; --l) {
        FgConstants c = Base();
        c.lvlSize[0] = ps[l].w; c.lvlSize[1] = ps[l].h;
        c.gridSize[0] = out[l].w; c.gridSize[1] = out[l].h;
        c.isCoarse = l == L - 1;
        c.isFinest = l == 0;
        if (l < L - 1) { c.parentGrid[0] = out[l + 1].w; c.parentGrid[1] = out[l + 1].h; }
        c.hasTemporal = temporal != nullptr;
        c.temporalShift = UINT(l);
        c.accumStats = stats && l == 0;
        SetConstants(c);
        ID3D11ShaderResourceView* parent = l < L - 1 ? out[l + 1].srv.Get() : nullptr;
        ID3D11ShaderResourceView* tsrv = temporal ? temporal->srv.Get() : nullptr;
        Run(sSearch_, {ps[l].srv.Get(), pd[l].srv.Get(), parent, tsrv}, {tmp[l].uav.Get()},
            Groups(out[l].w), Groups(out[l].h));
        Run(sMedian_, {ps[l].srv.Get(), pd[l].srv.Get(), tmp[l].srv.Get()},
            {out[l].uav.Get(), statsUav_.Get()}, Groups(out[l].w), Groups(out[l].h));
    }
}

void FrameGenGpu::PushFrame(ID3D11Texture2D* src, const D3D11_BOX& box) {
    TimerPoll();
    const int slot = frames_ == 0 ? 0 : 1 - cur_;
    ctx_->CopySubresourceRegion(frame_[slot].tex.Get(), 0, 0, 0, 0, src, 0, &box);
    cur_ = slot;
    ++frames_;
    TimerBegin(false);
    BuildPyramid(slot);  // previous frame's pyramid is reused as A
    if (frames_ >= 2) {
        const int a = 1 - cur_, b = cur_;
        const UINT zero[4] = {};
        ctx_->ClearUnorderedAccessViewUint(statsUav_.Get(), zero);
        Motion(a, b, fwdTmp_, fwd_, havePrevFwd_ ? &prevFwd_ : nullptr, true);
        // backward pass uses the negated previous forward field implicitly via its own search;
        // the temporal candidate is only used forward (cheap and enough for stability)
        Motion(b, a, bwdTmp_, bwd_, nullptr, false);
        ctx_->CopyResource(prevFwd_.tex.Get(), fwd_[0].tex.Get());
        havePrevFwd_ = true;
        FgConstants c = Base();
        SetConstants(c);
        Run(sStatic_, {frame_[a].srv.Get(), frame_[b].srv.Get()}, {staticMap_.uav.Get()},
            Groups(capW_), Groups(capH_));
    }
    TimerEnd();
}

ID3D11Texture2D* FrameGenGpu::Generate(float t) {
    if (frames_ < 2) return nullptr;
    TimerPoll();
    TimerBegin(true);
    const int a = 1 - cur_, b = cur_;
    FgConstants c = Base();
    c.t = std::clamp(t, 0.f, 1.f);
    SetConstants(c);
    Run(sInterp_, {frame_[a].srv.Get(), frame_[b].srv.Get(), fwd_[0].srv.Get(), bwd_[0].srv.Get(),
                   staticMap_.srv.Get(), statsSrv_.Get()},
        {gen_.uav.Get()}, Groups(capW_), Groups(capH_));
    ID3D11Texture2D* result = gen_.tex.Get();
    if (out_.tex) {
        Run(sUp_, {gen_.srv.Get()}, {upTmp_.uav.Get()}, Groups(outW_), Groups(outH_));
        Run(sSharp_, {upTmp_.srv.Get(), cursorSrv_.Get()}, {out_.uav.Get()}, Groups(outW_), Groups(outH_));
        result = out_.tex.Get();
    }
    TimerEnd();
    return result;
}

// --- work in flight ---------------------------------------------------------
void FrameGenGpu::MarkSubmitted(bool analysis) {
    auto& f = fences_[fencePos_];
    fencePos_ = (fencePos_ + 1) % int(fences_.size());
    if (!f.done) return;
    // a ring that is full loses its oldest piece: the caller stops well before that
    ctx_->End(f.done.Get());
    f.pending = true;
    f.analysis = analysis;
    ctx_->Flush();   // an event that is never handed to the GPU never finishes
}

void FrameGenGpu::PollFences() {
    for (auto& f : fences_) {
        if (!f.pending) continue;
        BOOL done = FALSE;
        if (ctx_->GetData(f.done.Get(), &done, sizeof done, D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK) f.pending = false;
    }
}

bool FrameGenGpu::AnalysisPending() {
    PollFences();
    for (auto& f : fences_) if (f.pending && f.analysis) return true;
    return false;
}

int FrameGenGpu::OutputsPending() {
    PollFences();
    int n = 0;
    for (auto& f : fences_) if (f.pending && !f.analysis) ++n;
    return n;
}

// --- timing -----------------------------------------------------------------
void FrameGenGpu::TimerBegin(bool gen) {
    auto& t = timers_[timerPos_];
    if (t.pending) return;  // ring full; skip this measurement
    t.gen = gen;
    ctx_->Begin(t.disjoint.Get());
    ctx_->End(t.begin.Get());
}

void FrameGenGpu::TimerEnd() {
    auto& t = timers_[timerPos_];
    if (t.pending) return;
    ctx_->End(t.end.Get());
    ctx_->End(t.disjoint.Get());
    t.pending = true;
    timerPos_ = (timerPos_ + 1) % int(timers_.size());
}

void FrameGenGpu::TimerPoll() {
    for (auto& t : timers_) {
        if (!t.pending) continue;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj{};
        if (ctx_->GetData(t.disjoint.Get(), &dj, sizeof dj, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        UINT64 b = 0, e = 0;
        if (ctx_->GetData(t.begin.Get(), &b, sizeof b, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        if (ctx_->GetData(t.end.Get(), &e, sizeof e, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        t.pending = false;
        if (dj.Disjoint || dj.Frequency == 0) continue;
        double ms = double(e - b) * 1000.0 / double(dj.Frequency);
        double& dst = t.gen ? genMs_ : analyzeMs_;
        dst = dst == 0 ? ms : dst * 0.9 + ms * 0.1;
    }
}

}  // namespace sbfg
