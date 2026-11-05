// Generate the frame at time t between A (t=0) and B (t=1).
// ref: FrameGen.interpolate()
#include "common.hlsli"

Texture2D<float4> FrameA    : register(t0);
Texture2D<float4> FrameB    : register(t1);
Texture2D<float4> FieldF    : register(t2);  // A -> B, finest block grid (proc px)
Texture2D<float4> FieldB    : register(t3);  // B -> A
Texture2D<float>  StaticMap : register(t4);
StructuredBuffer<uint> SceneStats : register(t5);
RWTexture2D<unorm float4> Out : register(u0);

// vector at capture-pixel position p, in capture pixels
float2 Field(Texture2D<float4> f, float2 p)
{
    float2 s = float2(procSize) / float2(capSize);
    float2 g = ((p + 0.5) * s) / BLOCK - 0.5;          // block-grid coordinates
    uint gw, gh;
    f.GetDimensions(gw, gh);
    float2 uv = (g + 0.5) / float2(gw, gh);
    return f.SampleLevel(LinearClamp, uv, 0).xy / s;
}

float4 Sample(Texture2D<float4> tex, float2 p, out float inside)
{
    inside = (p.x >= 0 && p.y >= 0 && p.x <= float(capSize.x - 1u) && p.y <= float(capSize.y - 1u)) ? 1.0 : 0.0;
    return tex.SampleLevel(LinearClamp, (p + 0.5) / float2(capSize), 0);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= capSize.x || id.y >= capSize.y) return;
    float2 x = float2(id.xy);
    float4 a0 = FrameA.Load(int3(id.xy, 0));
    float4 b0 = FrameB.Load(int3(id.xy, 0));

    // scene cut: show the nearer real frame
    float n = max(1.0, float(SceneStats[2]));
    float meanBest = float(SceneStats[0]) / 65536.0 / n;
    float meanZero = float(SceneStats[1]) / 65536.0 / n;
    if (meanBest > cutErr && meanBest > cutRatio * meanZero)
    {
        Out[id.xy] = t < 0.5 ? a0 : b0;
        return;
    }

    // fixed-point lookup of the vector passing through x at time t
    float2 f = Field(FieldF, x);
    float2 g = Field(FieldB, x);
    [loop] for (uint i = 0; i < fpIters; ++i)
    {
        f = Field(FieldF, x - t * f);
        g = Field(FieldB, x - (1 - t) * g);
    }
    float2 pa = x - t * f;
    float2 pb = x - (1 - t) * g;
    float inA, inB;
    float4 sa = Sample(FrameA, pa, inA);
    float4 sb = Sample(FrameB, pb, inB);

    // forward/backward consistency -> occlusion-aware confidence
    float2 bk = Field(FieldB, pa + f);
    float2 fw = Field(FieldF, pb + g);
    float errA = length(f + bk), errB = length(g + fw);
    float sigA = fbSigma + fbSigmaK * length(f);
    float sigB = fbSigma + fbSigmaK * length(g);
    float cA = exp(-(errA / sigA) * (errA / sigA)) * inA;
    float cB = exp(-(errB / sigB) * (errB / sigB)) * inB;

    // do not drag static pixels (HUD) with the moving background
    float sA = StaticMap.SampleLevel(LinearClamp, (pa + 0.5) / float2(capSize), 0);
    float sB = StaticMap.SampleLevel(LinearClamp, (pb + 0.5) / float2(capSize), 0);
    cA *= 1 - sA * min(length(f) * t, 1.0);
    cB *= 1 - sB * min(length(g) * (1 - t), 1.0);

    float wA = (1 - t) * cA + 1e-3 * (1 - t);
    float wB = t * cB + 1e-3 * t;
    float4 o = (sa * wA + sb * wB) / (wA + wB);

    if (StaticMap.Load(int3(id.xy, 0)) > 0.5)
        o = lerp(a0, b0, t);
    Out[id.xy] = saturate(float4(o.rgb, 1));
}
