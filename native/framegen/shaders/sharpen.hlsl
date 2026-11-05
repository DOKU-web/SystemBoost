// Pass 2: contrast-adaptive sharpening, clamped to the 3x3 range, then the
// cursor (upscaling mode) is composited on top. ref: adaptive_sharpen()
#include "common.hlsli"

Texture2D<float4> Src : register(t0);          // outSize
Texture2D<float4> CursorImg : register(t1);    // RGBA16F, a < 0 = invert
RWTexture2D<unorm float4> Dst : register(u0);  // outSize (RGBA8)

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= outSize.x || id.y >= outSize.y) return;
    int2 p = int2(id.xy);
    int2 m = int2(outSize) - 1;
    float3 c = Src.Load(int3(p, 0)).rgb;
    float3 lo = c, hi = c, cross = 0;
    [unroll] for (int dy = -1; dy <= 1; ++dy)
        [unroll] for (int dx = -1; dx <= 1; ++dx)
        {
            float3 v = Src.Load(int3(clamp(p + int2(dx, dy), int2(0, 0), m), 0)).rgb;
            lo = min(lo, v); hi = max(hi, v);
            if ((dx == 0) != (dy == 0)) cross += v;
        }
    cross *= 0.25;
    float rng = saturate(max(hi.r - lo.r, max(hi.g - lo.g, hi.b - lo.b)));
    float k = sharpness * 1.5 * (1 - rng) * (1 - rng);
    float3 o = clamp(c + k * (c - cross), lo, hi);

    if (cursorOn != 0)
    {
        int2 q = p - cursorPos;
        if (all(q >= 0) && all(q < int2(cursorSize)))
        {
            uint cw, ch;
            CursorImg.GetDimensions(cw, ch);
            float2 uv = (float2(q) + 0.5) / float2(cursorSize);
            float4 cur = CursorImg.Load(int3(min(uint2(uv * float2(cw, ch)), uint2(cw, ch) - 1), 0));
            if (cur.a < 0) o = 1 - o;                    // XOR (invert) cursor pixel
            else o = lerp(o, cur.rgb, saturate(cur.a));
        }
    }
    Dst[id.xy] = float4(o, 1);
}
