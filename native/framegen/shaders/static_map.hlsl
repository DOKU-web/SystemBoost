// Static map: 1 where A and B are identical at the same position (HUD, UI,
// subtitles, black bars). ref: smap in FrameGen.interpolate()
#include "common.hlsli"

Texture2D<float4> FrameA : register(t0);
Texture2D<float4> FrameB : register(t1);
RWTexture2D<float> StaticMap : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= capSize.x || id.y >= capSize.y) return;
    float3 d = abs(FrameA.Load(int3(id.xy, 0)).rgb - FrameB.Load(int3(id.xy, 0)).rgb);
    StaticMap[id.xy] = max(d.r, max(d.g, d.b)) < staticThr ? 1.0 : 0.0;
}
