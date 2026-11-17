// Whether this build has the AMD Radeon driver features: sb_helper built with AMD's ADLX SDK (CMake SB_ADLX=ON).
// build\windows\build.ps1 -Adlx sets both at once: -DSB_ADLX=ON for the helper and -p:SbAdlx=true for the app,
// which defines SB_ADLX here. Without it (the default) the texts that list the supported cards say NVIDIA only,
// as before the AMD work, and an AMD PC sees "AMD 드라이버 기능은 아직 준비 중이에요" (the helper's status "off").
namespace SystemBoost.App.Services;

public static class AmdBuild
{
#if SB_ADLX
    public static readonly bool DriverFeatures = true;
#else
    public static readonly bool DriverFeatures = false;
#endif
}
