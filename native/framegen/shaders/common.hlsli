// SystemBoost FrameGen v0.6 - shared constants (mirror of PARAMS in ref/fg_ref.py
// and FgConstants in src/FrameGenGpu.h). Keep the three in sync.
#ifndef SBFG_COMMON_HLSLI
#define SBFG_COMMON_HLSLI

#define BLOCK 8

cbuffer FgConstants : register(b0)
{
    uint2 capSize;        // capture (game client) size
    uint2 procSize;       // processing size (finest pyramid level)
    uint2 lvlSize;        // size of the level being processed
    uint2 gridSize;       // block grid of this level
    uint2 parentGrid;     // block grid of the parent (coarser) level
    uint  isCoarse;       // 1 = coarsest level (full search)
    uint  isFinest;       // 1 = finest level (sub-pixel + stats)
    int   radius;         // full-search radius at the coarsest level
    uint  refineIters;    // 3x3 refinement rounds
    float lam;            // smoothness penalty
    float zeroBias;       // zero-vector bias
    uint  hasTemporal;    // temporal candidate available
    uint  temporalShift;  // level index (temporal vectors are divided by 2^level)
    uint  accumStats;     // 1 = add errors to scene stats (forward, finest)
    float t;              // interpolation position 0..1
    uint  fpIters;        // fixed-point iterations
    float fbSigma;
    float fbSigmaK;
    float staticThr;
    float cutErr;
    float cutRatio;
    uint2 outSize;        // presentation size (after upscaling)
    float sharpness;      // 0..1
    uint  cursorOn;       // upscaling: draw cursor
    int2  cursorPos;      // top-left of the cursor in output pixels
    uint2 cursorSize;     // drawn size in output pixels
    float2 _pad0;
};

SamplerState LinearClamp : register(s0);

static const float3 LUMA = float3(0.299, 0.587, 0.114);

#endif
