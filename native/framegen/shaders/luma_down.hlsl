// Capture (RGBA) -> luma at processing resolution (box filter of the covered texels).
// ref: resize_area(to_luma(img), proc_scale)
#include "common.hlsli"

Texture2D<float4> Src : register(t0);
RWTexture2D<float> Dst : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= procSize.x || id.y >= procSize.y) return;
    uint fx = capSize.x / procSize.x;
    uint fy = capSize.y / procSize.y;
    float s = 0;
    for (uint y = 0; y < fy; ++y)
        for (uint x = 0; x < fx; ++x)
            s += dot(Src.Load(int3(id.x * fx + x, id.y * fy + y, 0)).rgb, LUMA);
    Dst[id.xy] = s / float(fx * fy);
}
