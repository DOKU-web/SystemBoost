// Constant buffer shared by all compute shaders (no Windows dependencies, also
// used by tests/gpu_parity). Must match cbuffer FgConstants in shaders/common.hlsli.
#pragma once
#include <cstdint>

namespace sbfg {

// Must match cbuffer FgConstants in shaders/common.hlsli (16-byte rows).
struct FgConstants {
    uint32_t capSize[2], procSize[2];
    uint32_t lvlSize[2], gridSize[2];
    uint32_t parentGrid[2], isCoarse, isFinest;
    int32_t radius; uint32_t refineIters; float lam, zeroBias;
    uint32_t hasTemporal, temporalShift, accumStats; float t;
    uint32_t fpIters; float fbSigma, fbSigmaK, staticThr;
    float cutErr, cutRatio; uint32_t outSize[2];
    float sharpness; uint32_t cursorOn; int32_t cursorPos[2];
    uint32_t cursorSize[2]; float pad[2];
};
static_assert(sizeof(FgConstants) == 144, "FgConstants layout");

}  // namespace sbfg
