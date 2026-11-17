// What the GPU features need from a graphics card maker's driver, the same for every maker.
//   NVIDIA  WindowsBoostBackend.Gpu.cs (NvidiaGpuBackend): a thin adapter over the code that was there before
//           (NVAPI driver profiles, NVML power limit and clock offsets in the elevated helper).
//   AMD     AmdGpuBackend.cs: ADLX inside the elevated helper (native/helper), over the pipe protocol.
//   Intel   nothing yet ("인텔 Arc 드라이버 기능은 아직 지원하지 않아요").
// Which card the features are for is decided in GpuSelection.cs. No Avalonia here: the GPU tests link these files.
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public enum GpuVendor { None, Nvidia, Amd, Intel }

public enum GpuAutoTuning { Undervolt, OverclockGpu, OverclockVram }

/// Live readings. -1 = the driver does not report it.
public sealed record GpuTelemetry(int TempC, int HotspotC, int CoreMHz, int MemMHz, int PowerW, int UsagePct, int VramUsedMB)
{
    public static readonly GpuTelemetry Empty = new(-1, -1, -1, -1, -1, -1, -1);
}

/// What the card and its driver allow. A feature that is not supported is shown switched off with the note, not hidden.
public sealed class GpuCapabilities
{
    public const string NotOnThisCard = "이 그래픽카드에서는 지원하지 않아요";
    public const string DesktopOnly = "데스크톱 그래픽카드 전용";
    public const string IntelNotYet = "인텔 Arc 드라이버 기능은 아직 지원하지 않아요";

    public bool Telemetry { get; init; }
    public bool GameProfile { get; init; }
    public bool PowerLimit { get; init; }
    public bool CoreClock { get; init; }
    public bool MemClock { get; init; }
    public bool AutoUndervolt { get; init; }
    public bool AutoOverclockGpu { get; init; }
    public bool AutoOverclockVram { get; init; }
    public bool ResetToFactory { get; init; }
    /// Why the game profile is off, null when it is supported.
    public string? GameProfileNote { get; init; }
    /// Why tuning (power, clocks, auto tuning, factory reset) is off, null when some of it is supported.
    public string? TuningNote { get; init; }

    public bool AnyTuning => PowerLimit || CoreClock || MemClock || AutoUndervolt || AutoOverclockGpu || AutoOverclockVram;

    public bool Auto(GpuAutoTuning kind) => kind switch
    {
        GpuAutoTuning.Undervolt => AutoUndervolt,
        GpuAutoTuning.OverclockGpu => AutoOverclockGpu,
        _ => AutoOverclockVram,
    };

    /// NVIDIA as before this interface: the switches stay usable and the existing code answers when the card
    /// or driver cannot do it. Auto tuning and the factory reset are AMD (ADLX) features.
    public static readonly GpuCapabilities NvidiaAsBefore = new()
    {
        Telemetry = true, GameProfile = true, PowerLimit = true, CoreClock = true, MemClock = true,
    };

    public static readonly GpuCapabilities Intel = new() { GameProfileNote = IntelNotYet, TuningNote = IntelNotYet };
}

/// One graphics card maker's driver features. Every write is journaled in boost-state.json before it is sent,
/// and each record kind has its own revert (WindowsBoostBackend.RestoreOneAsync).
public interface IGpuVendorBackend
{
    GpuVendor Vendor { get; }
    /// The card's name as the driver reports it ("AMD Radeon RX 7800 XT"), "" before DetectAsync found one.
    string Name { get; }
    /// True when this maker's card and driver are there. Reads only.
    Task<bool> DetectAsync();
    GpuCapabilities GetCapabilities();
    /// Null when nothing could be read.
    Task<GpuTelemetry?> ReadTelemetryAsync();

    /// NVIDIA: writes the driver profile of every game in the list (game is not used), kept until turned off.
    /// AMD: the driver's 3D settings are global, so they are set when the game starts (game = its exe)
    /// and put back by RevertGameProfileAsync when it ends.
    Task<string> ApplyGameProfileAsync(string? game);
    /// Puts back one record of this maker's game profile. Null when done.
    Task<string?> RevertGameProfileAsync(BoostBackup record);

    /// Applies the values chosen on the GPU page; they hold only after KeepTuningAsync.
    Task<string> ApplyTuningAsync();
    Task<string> ApplyAutoTuningAsync(GpuAutoTuning kind);
    /// Puts back one tuning record. Null when done.
    Task<string?> RevertTuningAsync(BoostBackup record);
    Task<string> ResetToFactoryAsync();
    /// The user saw the screen is fine: the values stay (still only while the app runs).
    Task<string> KeepTuningAsync();
    /// Every 2 seconds from the backend's watch loop: notices a revert by the helper (heat, driver error, no confirm)
    /// and puts the values back from the journal when the helper is gone.
    Task WatchTuningAsync();
}
