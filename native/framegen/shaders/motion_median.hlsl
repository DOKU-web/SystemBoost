// 3x3 component median of the block field, error recompute and, at the finest
// level, parabolic sub-pixel refinement + scene statistics.
// ref: median3_field(), block_cost() after median, sub-pixel step, scene-cut sums.
#include "common.hlsli"

Texture2D<float>  SrcL   : register(t0);
Texture2D<float>  DstL   : register(t1);
Texture2D<float4> InField : register(t2);
RWTexture2D<float4> OutField : register(u0);
RWStructuredBuffer<uint> SceneStats : register(u1);  // [0]=sum best err, [1]=sum zero err, [2]=blocks (x65536 fixed point)

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
            if (any(q < 0) || any(q >= int2(lvlSize))) d = 0.25;
            else d = abs(SrcL.Load(int3(p, 0)) - DstL.Load(int3(q, 0)));
            s += d;
        }
    }
    return s * (1.0 / (BLOCK * BLOCK));
}

float Median9(float v[9])
{
    // partial selection sort up to the middle element
    [unroll] for (int i = 0; i <= 4; ++i)
    {
        int m = i;
        [unroll] for (int j = i + 1; j < 9; ++j)
            if (v[j] < v[m]) m = j;
        float tmp = v[i]; v[i] = v[m]; v[m] = tmp;
    }
    return v[4];
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint2 blk = id.xy;
    if (blk.x >= gridSize.x || blk.y >= gridSize.y) return;

    float vx[9], vy[9];
    int k = 0;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
        [unroll] for (int dx = -1; dx <= 1; ++dx)
        {
            int2 i = clamp(int2(blk) + int2(dx, dy), int2(0, 0), int2(gridSize) - 1);
            float2 v = InField.Load(int3(i, 0)).xy;
            vx[k] = v.x; vy[k] = v.y; ++k;
        }
    int2 vi = int2(round(float2(Median9(vx), Median9(vy))));
    float c0 = BlockCost(blk, vi);
    float2 v = float2(vi);
    float zeroErr = 0;

    if (isFinest != 0)
    {
        // parabolic sub-pixel fit per axis
        float cm = BlockCost(blk, vi - int2(1, 0)), cp = BlockCost(blk, vi + int2(1, 0));
        float den = cm - 2 * c0 + cp;
        if (den > 1e-6) v.x += clamp(0.5 * (cm - cp) / den, -0.5, 0.5);
        cm = BlockCost(blk, vi - int2(0, 1)); cp = BlockCost(blk, vi + int2(0, 1));
        den = cm - 2 * c0 + cp;
        if (den > 1e-6) v.y += clamp(0.5 * (cm - cp) / den, -0.5, 0.5);

        if (accumStats != 0)
        {
            zeroErr = BlockCost(blk, int2(0, 0));
            uint dummy;
            InterlockedAdd(SceneStats[0], uint(c0 * 65536.0), dummy);
            InterlockedAdd(SceneStats[1], uint(zeroErr * 65536.0), dummy);
            InterlockedAdd(SceneStats[2], 1u, dummy);
        }
    }
    OutField[blk] = float4(v, c0, zeroErr);
}
