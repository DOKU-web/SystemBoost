// 2x box downsample of a luma level. ref: down2()
#include "common.hlsli"

Texture2D<float> Src : register(t0);
RWTexture2D<float> Dst : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= lvlSize.x || id.y >= lvlSize.y) return;  // lvlSize = destination size
    int2 p = int2(id.xy) * 2;
    Dst[id.xy] = 0.25 * (Src.Load(int3(p, 0)) + Src.Load(int3(p + int2(1, 0), 0)) +
                         Src.Load(int3(p + int2(0, 1), 0)) + Src.Load(int3(p + int2(1, 1), 0)));
}
