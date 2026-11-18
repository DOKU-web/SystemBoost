// Which graphics card the GPU features are for when a PC has more than one. Plain logic, checked by the GPU tests.
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemBoost.App.Services;

/// AmdIndex: the card's index in ADLX's list when Vendor is Amd, otherwise -1.
public sealed record GpuPick(GpuVendor Vendor, int AmdIndex);

/// An AMD card as the list of graphics adapters (DXGI, through the frame generation engine) shows it, for when ADLX
/// may not be asked. Integrated also stands for "the AMD driver is there but the list could not be read".
public enum AmdSeen { None, Integrated, Discrete }

public static class GpuSelection
{
    /// nvidiaReads: NVML reads an NVIDIA card. nvidiaDriver: nvapi64.dll is in System32. amd: the cards amd.info lists.
    /// amdOff: the AMD driver is there (amdadlx64.dll) but the helper was built without ADLX (amd.info status "off").
    /// intelSeen: a card named Intel is in the list of graphics adapters.
    /// amdWaiting: the AMD driver is there but ADLX was not asked because the consent to Article 12-2 is missing
    /// (AmdTerms); what the list of graphics adapters shows of AMD instead.
    /// 1. An NVIDIA card NVML can read: NVIDIA, as before (NVIDIA cards in PCs are discrete).
    /// 2. A discrete AMD card: AMD (a discrete card before an integrated one), also one only the adapter list shows.
    /// 3. An NVIDIA driver NVML cannot read (an older driver): NVIDIA, as before; an AMD APU beside it stays second.
    /// 4. Only an AMD APU: AMD (telemetry and the game profile; tuning stays off on integrated graphics).
    /// 5. The AMD driver with a helper built without ADLX, or without the consent: AMD, every feature switched off with the reason.
    /// 6. Only Intel graphics: Intel, whose driver features are not supported yet.
    /// 7. Nothing recognised: the NVIDIA path, which says what it needs, as before.
    public static GpuPick Choose(bool nvidiaReads, bool nvidiaDriver, IReadOnlyList<AmdGpuInfo> amd, bool intelSeen, bool amdOff = false,
                                 AmdSeen amdWaiting = AmdSeen.None)
    {
        if (nvidiaReads) return new GpuPick(GpuVendor.Nvidia, -1);
        if (amd.FirstOrDefault(g => !g.Integrated) is { } discrete) return new GpuPick(GpuVendor.Amd, discrete.Index);
        if (amdWaiting == AmdSeen.Discrete) return new GpuPick(GpuVendor.Amd, -1);
        if (nvidiaDriver) return new GpuPick(GpuVendor.Nvidia, -1);
        if (amd.Count > 0) return new GpuPick(GpuVendor.Amd, amd[0].Index);
        if (amdWaiting == AmdSeen.Integrated) return new GpuPick(GpuVendor.Amd, -1);
        if (amdOff) return new GpuPick(GpuVendor.Amd, -1);
        if (intelSeen) return new GpuPick(GpuVendor.Intel, -1);
        return new GpuPick(GpuVendor.None, -1);
    }

    /// A graphics adapter's name is an AMD one ("AMD Radeon RX 7800 XT", "AMD Radeon(TM) Graphics", "Radeon RX 580 Series").
    public static bool IsAmdName(string name) =>
        name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);

    /// What the adapter list shows of AMD while amdadlx64.dll is there. An empty list (the engine could not list
    /// them) leaves only the driver file to go by: Integrated, so an NVIDIA driver still comes first.
    public static AmdSeen SeenInAdapters(IReadOnlyCollection<(string Name, bool Integrated)> adapters)
    {
        if (adapters.Count == 0) return AmdSeen.Integrated;
        if (adapters.Any(a => IsAmdName(a.Name) && !a.Integrated)) return AmdSeen.Discrete;
        return adapters.Any(a => IsAmdName(a.Name)) ? AmdSeen.Integrated : AmdSeen.None;
    }
}
