// Block motion search for one pyramid level, one thread per 8x8 block.
// ref: motion_pyramid() candidate selection + 3x3 refinement (before median).
// Output field: xy = integer vector (level px), z = mean abs error, w = 0.
#include "common.hlsli"

Texture2D<float>  SrcL     : register(t0);  // luma of the frame we move FROM
Texture2D<float>  DstL     : register(t1);  // luma of the frame we move TO
Texture2D<float4> Parent   : register(t2);  // field of the coarser level (finished)
Texture2D<float4> Temporal : register(t3);  // finest field of the previous pair
RWTexture2D<float4> OutField : register(u0);

float BlockCost(uint2 blk, int2 v)
{
    float s = 0;
    [loop] for (uint y = 0; y < BLOCK; ++y)
    {
        [unroll] for (uint x = 0; x < BLOCK; ++x)
        {
            int2 p = min(int2(blk * BLOCK + uint2(x, y)), int2(lvlSize) - 1);
            int2 q = p + v;
            float d;
            if (any(q < 0) || any(q >= int2(lvlSize))) d = 0.25;  // out of frame
            else d = abs(SrcL.Load(int3(p, 0)) - DstL.Load(int3(q, 0)));
            s += d;
        }
    }
    return s * (1.0 / (BLOCK * BLOCK));
}

int2 TemporalCand(uint2 blk)
{
    uint w, h;
    Temporal.GetDimensions(w, h);
    uint s = 1u << temporalShift;
    uint2 i = min(blk * s, uint2(w, h) - 1);
    return int2(round(Temporal.Load(int3(i, 0)).xy / float(s)));
}

int2 ParentCand(uint2 blk, int2 off)
{
    int2 i = clamp(int2(min(blk / 2, parentGrid - 1)) + off, int2(0, 0), int2(parentGrid) - 1);
    return int2(round(Parent.Load(int3(i, 0)).xy)) * 2;
}

void Try(uint2 blk, int2 c, int2 pred, inout int2 best, inout float bestC, inout float bestE)
{
    float e = BlockCost(blk, c);
    float cost = e + lam * float(abs(c.x - pred.x) + abs(c.y - pred.y)) +
                 zeroBias * ((c.x != 0 || c.y != 0) ? 1.0 : 0.0);
    if (cost < bestC) { bestC = cost; best = c; bestE = e; }
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint2 blk = id.xy;
    if (blk.x >= gridSize.x || blk.y >= gridSize.y) return;

    int2 best = int2(0, 0);
    float bestC = 1e30, bestE = 1e30;
    int2 pred = int2(0, 0);
    uint iters = refineIters;

    if (isCoarse != 0)
    {
        [loop] for (int dy = -radius; dy <= radius; ++dy)
            [loop] for (int dx = -radius; dx <= radius; ++dx)
                Try(blk, int2(dx, dy), pred, best, bestC, bestE);
        if (hasTemporal != 0) Try(blk, TemporalCand(blk), pred, best, bestC, bestE);
        iters = 1;
    }
    else
    {
        pred = ParentCand(blk, int2(0, 0));
        Try(blk, pred, pred, best, bestC, bestE);
        Try(blk, int2(0, 0), pred, best, bestC, bestE);
        Try(blk, ParentCand(blk, int2(0, -1)), pred, best, bestC, bestE);
        Try(blk, ParentCand(blk, int2(0, 1)), pred, best, bestC, bestE);
        Try(blk, ParentCand(blk, int2(-1, 0)), pred, best, bestC, bestE);
        Try(blk, ParentCand(blk, int2(1, 0)), pred, best, bestC, bestE);
        if (hasTemporal != 0) Try(blk, TemporalCand(blk), pred, best, bestC, bestE);
    }

    [loop] for (uint it = 0; it < iters; ++it)
    {
        int2 center = best;
        int2 nb = center;
        float nc = 1e30, ne = 1e30;
        [loop] for (int dy = -1; dy <= 1; ++dy)
            [loop] for (int dx = -1; dx <= 1; ++dx)
                Try(blk, center + int2(dx, dy), pred, nb, nc, ne);
        best = nb; bestC = nc; bestE = ne;
    }
    OutField[blk] = float4(float2(best), bestE, 0);
}
