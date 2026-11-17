// The GPU features through IGpuVendorBackend (Services/Gpu).
//   NVIDIA  NvidiaGpuBackend below: a thin adapter that calls the code that was here before, unchanged
//           (ApplyDriverAsync, ApplyGpuTuningAsync, KeepGpuAsync, RestoreGpuAsync, WatchGpuAsync, "nv.restore").
//   AMD     AmdGpuBackend (ADLX in the elevated helper). Its game profile is global, so the "드라이버 게임 프로필"
//           switch is kept in Enabled and the settings are set when a game starts and put back when it ends.
// Records go back by their kind whatever card is picked now: oc and nv through the NVIDIA code, amdoc and amd3d
// through the AMD code.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public sealed partial class WindowsBoostBackend
{
    private const string GameProfileId = "gpu.driver";

    private NvidiaGpuBackend? _nvidiaGpu;
    private AmdGpuBackend? _amdGpu;
    private GpuPick? _gpuPick;

    private NvidiaGpuBackend Nvidia => _nvidiaGpu ??= new NvidiaGpuBackend(this);
    private AmdGpuBackend Amd => _amdGpu ??= new AmdGpuBackend(_plainHelper, _helper, new StateJournal(this), new AmdHost
    {
        DllPresent = () => AmdDriverPresent,
        IsLaptop = PcForm.IsLaptop,
        TuningAllowed = () => License.Allowed(PremiumFeature.Overclock),
        TuningPlan = License.PlanOf(PremiumFeature.Overclock),
        Choice = () => _s.AmdGpu,
        RiskTerms = GpuRiskTerms,
        Log = Log,
        Notice = line => Notice?.Invoke(line),
        Changed = Publish,
    });

    private sealed class StateJournal : IGpuJournal
    {
        private readonly WindowsBoostBackend _b;
        public StateJournal(WindowsBoostBackend b) { _b = b; }
        public List<BoostBackup> Backups => _b._s.Backups;
        public void Save() => _b.Save();
    }

    private static string System32(string file) => Path.Combine(Environment.SystemDirectory, file);
    /// amdadlx64.dll comes with current AMD display drivers. Only the helper loads it, from System32 only; the app just looks.
    internal static bool AmdDriverPresent => File.Exists(System32("amdadlx64.dll"));

    // ------------------------------------------------------------ which card
    /// Which card the GPU features are for (GpuSelection), decided once per run; again while AMD could not be read.
    /// On a PC whose NVIDIA card NVML reads, nothing else is looked at.
    public async Task<GpuVendor> GpuVendorAsync()
    {
        if (_gpuPick is { } known) return known.Vendor;
        bool nvidiaReads = Nvml.Read() != null;
        bool nvidiaDriver = File.Exists(System32("nvapi64.dll"));
        IReadOnlyList<AmdGpuInfo> amd = Array.Empty<AmdGpuInfo>();
        bool settled = true, amdOff = false;
        if (!nvidiaReads && AmdDriverPresent)
        {
            var info = await Amd.ReadInfoAsync();
            amd = info.Gpus;
            amdOff = info.Status == "off";   // a helper built without ADLX
            settled = info.Status is not ("no_helper" or "bad_reply");
        }
        bool intel = false;
        if (!nvidiaReads && !nvidiaDriver && amd.Count == 0 && !amdOff)
            intel = (await FrameGenEngine.ListGpusAsync()).Any(g => g.Value != "same" && g.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase));
        var pick = GpuSelection.Choose(nvidiaReads, nvidiaDriver, amd, intel, amdOff);
        if (pick.Vendor == GpuVendor.Amd) Amd.Select(pick.AmdIndex);
        if (settled) _gpuPick = pick;
        return pick.Vendor;
    }

    private async Task<IGpuVendorBackend> GpuAsync() => await GpuVendorAsync() == GpuVendor.Amd ? Amd : Nvidia;

    public async Task<GpuCapabilities> GpuCapabilitiesAsync() => await GpuVendorAsync() switch
    {
        GpuVendor.Amd => Amd.GetCapabilities(),
        GpuVendor.Intel => GpuCapabilities.Intel,
        _ => GpuCapabilities.NvidiaAsBefore,
    };

    // ------------------------------------------------------------ switches (ApplyCoreAsync)
    private async Task<string> GpuTuningOnAsync()
    {
        var vendor = await GpuVendorAsync();
        if (vendor == GpuVendor.Intel) return GpuCapabilities.IntelNotYet + AmdProtocol.Unchanged;
        return await (await GpuAsync()).ApplyTuningAsync();   // NVIDIA: ApplyGpuTuningAsync, as before
    }

    private async Task<string> GameProfileOnAsync()
    {
        var vendor = await GpuVendorAsync();
        if (vendor == GpuVendor.Intel) return GpuCapabilities.IntelNotYet + AmdProtocol.Unchanged;
        if (vendor != GpuVendor.Amd) return await Nvidia.ApplyGameProfileAsync(null);   // every game's NVIDIA profile, as before
        var caps = Amd.GetCapabilities();
        if (!caps.GameProfile) return (caps.GameProfileNote ?? GpuCapabilities.NotOnThisCard) + AmdProtocol.Unchanged;
        // asked now, so that a game's start never waits for an administrator prompt (none comes up over a game)
        var e = await _helper.EnsureAsync();
        if (e != null) return e;
        if (!_s.Enabled.Contains(GameProfileId)) _s.Enabled.Add(GameProfileId);
        Save();
        if (_gameRunning && _gameName != null) return await Amd.ApplyGameProfileAsync(_gameName + ".exe");
        return "켰어요. 게임이 실행되면 Anti-Lag를 켜고 Radeon Chill을 꺼요. 게임이 끝나면 원래대로 돌려요"
               + (_s.Games.Count == 0 ? " · 먼저 '게임 부스트'의 내 게임에 게임을 추가하세요" : "");
    }

    /// The AMD switch is off: what a running game has of it goes back. Null when this is not the AMD switch.
    private async Task<string?> GameProfileOffAsync()
    {
        if (!_s.Enabled.Remove(GameProfileId)) return null;
        Amd.TakeRevertNote();
        var running = _s.Backups.Where(b => b.Kind == AmdProtocol.ProfileKind).ToList();
        if (running.Count == 0) return "껐어요";
        var e = await _helper.EnsureAsync();
        if (e != null) { _s.Enabled.Add(GameProfileId); return e; }
        var failed = await RestoreAsync(running);
        return failed == null ? DescribeRestore(GameProfileId) : "되돌리지 못했어요: " + failed;
    }

    // ------------------------------------------------------------ game start and end (TickAsync, EndGameTimeAsync)
    private async Task GameProfileStartAsync(string game)
    {
        if (!_s.Enabled.Contains(GameProfileId) || await GpuVendorAsync() != GpuVendor.Amd) return;
        Log($"{BoostCatalog.All.First(f => f.Id == GameProfileId).Title}: {await Amd.ApplyGameProfileAsync(game + ".exe")}");
    }

    // ADLX 3D settings are global: they go back when the game ends. The helper does it too as it exits.
    private async Task GameProfileEndAsync()
    {
        var mine = _s.Backups.Where(b => b.Kind == AmdProtocol.ProfileKind).ToList();
        if (mine.Count == 0 || !_helper.Running) return;
        var failed = await RestoreAsync(mine);
        Amd.TakeRevertNote();   // already in 최근 동작
        Log(failed == null ? $"{BoostCatalog.All.First(f => f.Id == GameProfileId).Title}: 원래대로 돌렸어요" : "되돌리지 못했어요: " + failed);
    }

    // ------------------------------------------------------------ NVIDIA
    /// The NVIDIA code as it was, behind the interface. Nothing here decides anything of its own.
    private sealed class NvidiaGpuBackend : IGpuVendorBackend
    {
        private readonly WindowsBoostBackend _b;
        public NvidiaGpuBackend(WindowsBoostBackend b) { _b = b; }

        public GpuVendor Vendor => GpuVendor.Nvidia;
        public string Name => Nvml.Read()?.Name ?? "";
        public Task<bool> DetectAsync() => Task.FromResult(Nvml.Read() != null || File.Exists(System32("nvapi64.dll")));
        public GpuCapabilities GetCapabilities() => GpuCapabilities.NvidiaAsBefore;
        public Task<GpuTelemetry?> ReadTelemetryAsync() =>
            Task.FromResult(Nvml.Read() is { } g ? GpuTelemetry.Empty with { TempC = g.TempC } : null);
        public Task<string> ApplyGameProfileAsync(string? game) => _b.ApplyDriverAsync();
        public Task<string?> RevertGameProfileAsync(BoostBackup b) => _b.HelperError("nv.restore", b.Target, b.Name, b.Value);
        public Task<string> ApplyTuningAsync() => _b.ApplyGpuTuningAsync();
        public Task<string> ApplyAutoTuningAsync(GpuAutoTuning kind) => Task.FromResult(GpuCapabilities.NotOnThisCard + AmdProtocol.Unchanged);
        public Task<string?> RevertTuningAsync(BoostBackup b) => _b.RestoreGpuAsync(b);
        public Task<string> ResetToFactoryAsync() => Task.FromResult(GpuCapabilities.NotOnThisCard + AmdProtocol.Unchanged);
        public Task<string> KeepTuningAsync() => _b.KeepGpuAsync();
        public Task WatchTuningAsync() => _b.WatchGpuAsync();
    }
}
