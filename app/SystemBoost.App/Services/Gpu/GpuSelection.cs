// Which graphics card the GPU features are for when a PC has more than one. Plain logic, checked by the GPU tests.
using System.Collections.Generic;
using System.Linq;

namespace SystemBoost.App.Services;

/// AmdIndex: the card's index in ADLX's list when Vendor is Amd, otherwise -1.
public sealed record GpuPick(GpuVendor Vendor, int AmdIndex);

public static class GpuSelection
{
    /// nvidiaReads: NVML reads an NVIDIA card. nvidiaDriver: nvapi64.dll is in System32. amd: the cards amd.info lists.
    /// amdOff: the AMD driver is there (amdadlx64.dll) but the helper was built without ADLX (amd.info status "off").
    /// intelSeen: a card named Intel is in the list of graphics adapters.
    /// 1. An NVIDIA card NVML can read: NVIDIA, as before (NVIDIA cards in PCs are discrete).
    /// 2. A discrete AMD card: AMD (a discrete card before an integrated one).
    /// 3. An NVIDIA driver NVML cannot read (an older driver): NVIDIA, as before; an AMD APU beside it stays second.
    /// 4. Only an AMD APU: AMD (telemetry and the game profile; tuning stays off on integrated graphics).
    /// 5. The AMD driver with a helper built without ADLX: AMD, every feature switched off with the reason.
    /// 6. Only Intel graphics: Intel, whose driver features are not supported yet.
    /// 7. Nothing recognised: the NVIDIA path, which says what it needs, as before.
    public static GpuPick Choose(bool nvidiaReads, bool nvidiaDriver, IReadOnlyList<AmdGpuInfo> amd, bool intelSeen, bool amdOff = false)
    {
        if (nvidiaReads) return new GpuPick(GpuVendor.Nvidia, -1);
        if (amd.FirstOrDefault(g => !g.Integrated) is { } discrete) return new GpuPick(GpuVendor.Amd, discrete.Index);
        if (nvidiaDriver) return new GpuPick(GpuVendor.Nvidia, -1);
        if (amd.Count > 0) return new GpuPick(GpuVendor.Amd, amd[0].Index);
        if (amdOff) return new GpuPick(GpuVendor.Amd, -1);
        if (intelSeen) return new GpuPick(GpuVendor.Intel, -1);
        return new GpuPick(GpuVendor.None, -1);
    }
}
