// Pass 1: Catmull-Rom bicubic resample, clamped to the 2x2 source texels
// around the sample (no halos). ref: resample_bicubic_clamped()
#include "common.hlsli"

Texture2D<float4> Src : register(t0);       // capSize
RWTexture2D<float4> Dst : register(u0);     // outSize (RGBA16F)

void Weights(float x, out float4 w)
{
    float x2 = x * x, x3 = x2 * x;
    w.x = -0.5 * x3 + x2 - 0.5 * x;
    w.y = 1.5 * x3 - 2.5 * x2 + 1.0;
    w.z = -1.5 * x3 + 2.0 * x2 + 0.5 * x;
    w.w = 0.5 * x3 - 0.5 * x2;
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= outSize.x || id.y >= outSize.y) return;
    float2 sp = (float2(id.xy) + 0.5) * float2(capSize) / float2(outSize) - 0.5;
    int2 p0 = int2(floor(sp));
    float2 f = sp - float2(p0);
    float4 wx, wy;
    Weights(f.x, wx);
    Weights(f.y, wy);
    float wxa[4] = { wx.x, wx.y, wx.z, wx.w };
    float wya[4] = { wy.x, wy.y, wy.z, wy.w };
    float3 acc = 0, lo = 1e9, hi = -1e9;
    [unroll] for (int j = 0; j < 4; ++j)
    {
        [unroll] for (int i = 0; i < 4; ++i)
        {
            int2 q = clamp(p0 + int2(i - 1, j - 1), int2(0, 0), int2(capSize) - 1);
            float3 v = Src.Load(int3(q, 0)).rgb;
            acc += wya[j] * wxa[i] * v;
            if (i >= 1 && i <= 2 && j >= 1 && j <= 2) { lo = min(lo, v); hi = max(hi, v); }
        }
    }
    Dst[id.xy] = float4(clamp(acc, lo, hi), 1);
}
