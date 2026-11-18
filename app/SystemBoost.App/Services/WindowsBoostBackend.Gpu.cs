// The GPU features through IGpuVendorBackend (Services/Gpu).
//   NVIDIA  NvidiaGpuBackend below: a thin adapter that calls the code that was here before, unchanged
//           (ApplyDriverAsync, ApplyGpuTuningAsync, KeepGpuAsync, RestoreGpuAsync, WatchGpuAsync, "nv.restore").
//   AMD     AmdGpuBackend (ADLX in the elevated helper). Its game profile is global, so the "드라이버 게임 프로필"
//           switch is kept in Enabled and the settings are set when a game starts and put back when it ends.
// Records go back by their kind whatever card is picked now: oc and nv through the NVIDIA code, amdoc and amd3d
// through the AMD code.
// Before 2027-01-01 KST a build with ADLX sends no amd.* command until the consent to Article 12-2 of the licence
// agreement is on record (AmdTerms, AmdHost.AmdTermsOk). Until then the AMD card is found through the list of graphics
// adapters instead of ADLX, and every AMD feature shows AmdProtocol.TermsText.
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
        Consented = OcConsent.Valid,
        AmdTermsOk = () => AmdTerms.Ok(DateTime.UtcNow),
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
    // Whether the consent to Article 12-2 was in order when _gpuPick was decided: the pick is made again when that changes.
    private bool _gpuPickTermsOk;

    /// Which card the GPU features are for (GpuSelection), decided once per run; again while AMD could not be read, and
    /// when the consent to Article 12-2 is given or withdrawn (or 2027-01-01 KST comes). On a PC whose NVIDIA card NVML
    /// reads, nothing else is looked at. Without that consent ADLX is not asked: the list of graphics adapters
    /// (FrameGenEngine.ListGpusAsync, DXGI) says whether an AMD card is there.
    public async Task<GpuVendor> GpuVendorAsync()
    {
        bool termsOk = !Amd.WaitsForTerms;
        if (_gpuPick is { } known && _gpuPickTermsOk == termsOk) return known.Vendor;
        bool nvidiaReads = Nvml.Read() != null;
        bool nvidiaDriver = File.Exists(System32("nvapi64.dll"));
        IReadOnlyList<AmdGpuInfo> amd = Array.Empty<AmdGpuInfo>();
        bool settled = true, amdOff = false;
        var waiting = AmdSeen.None;
        List<GpuItem>? adapters = null;
        if (!nvidiaReads && AmdDriverPresent)
        {
            if (!termsOk)
            {
                adapters = await AdaptersAsync();
                waiting = GpuSelection.SeenInAdapters(adapters.Select(g => (g.Name, g.Integrated)).ToList());
            }
            else
            {
                var info = await Amd.ReadInfoAsync();
                amd = info.Gpus;
                amdOff = info.Status == "off";   // a helper built without ADLX
                settled = info.Status is not ("no_helper" or "bad_reply");
            }
        }
        bool intel = false;
        if (!nvidiaReads && !nvidiaDriver && amd.Count == 0 && !amdOff && waiting == AmdSeen.None)
            intel = (adapters ?? await AdaptersAsync()).Any(g => g.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase));
        var pick = GpuSelection.Choose(nvidiaReads, nvidiaDriver, amd, intel, amdOff, waiting);
        if (pick.Vendor == GpuVendor.Amd) Amd.Select(pick.AmdIndex);
        if (settled) (_gpuPick, _gpuPickTermsOk) = (pick, termsOk);
        return pick.Vendor;
    }

    // The graphics adapters the frame generation engine lists, without its "화면 GPU (기본)" entry.
    private static async Task<List<GpuItem>> AdaptersAsync() => (await FrameGenEngine.ListGpusAsync()).Where(g => g.Value != "same").ToList();

    private async Task<IGpuVendorBackend> GpuAsync() => await GpuVendorAsync() == GpuVendor.Amd ? Amd : Nvidia;

    public async Task<GpuCapabilities> GpuCapabilitiesAsync() => await GpuVendorAsync() switch
    {
        GpuVendor.Amd => Amd.GetCapabilities(),
        GpuVendor.Intel => GpuCapabilities.Intel,
        _ => GpuCapabilities.NvidiaAsBefore,
    };

    private string? _otherGpuName;

    /// For the dashboard: the card's name only, and what can be read of it (free on every plan).
    public async Task<(GpuVendor vendor, string name, GpuTelemetry? reading)> GpuTileAsync()
    {
        var vendor = await GpuVendorAsync();
        if (vendor == GpuVendor.Amd && Amd.Name.Length > 0) return (vendor, Amd.Name, await Amd.ReadTelemetryAsync());
        if (vendor == GpuVendor.Nvidia && await Task.Run(Nvml.Read) is { } r) return (vendor, r.Name, GpuTelemetry.Empty with { TempC = r.TempC });
        if (_otherGpuName == null)   // the engine's list of adapters, once (also an AMD card when ADLX gave no name)
        {
            var names = await FrameGenEngine.ListGpusAsync();
            var first = names.FirstOrDefault(g => g.Value != "same" && !g.Integrated) ?? names.FirstOrDefault(g => g.Value != "same");
            _otherGpuName = first?.Name ?? "";
        }
        return (vendor, _otherGpuName, null);
    }

    /// The card a tuning would write to, named as consents.json keeps it (OcConsent). Null when this PC has no card
    /// that SystemBoost can tune (Intel, a laptop or APU, no driver): then nothing is asked and the tuning says why itself.
    public async Task<string?> TuningGpuNameAsync()
    {
        var vendor = await GpuVendorAsync();
        if (vendor == GpuVendor.Amd)
        {
            if (Amd.Gpu == null) await Amd.ReadInfoAsync();
            return Amd.Gpu is { } g && g.Name.Length > 0 && Amd.GetCapabilities().AnyTuning ? g.Name : null;
        }
        if (vendor == GpuVendor.Intel) return null;
        return await Task.Run(Nvml.Read) is { } r && r.Name.Length > 0 ? r.Name : null;   // NVIDIA, as the tuning reads it
    }

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

    // ------------------------------------------------------------ AMD, for the GPU page
    public AmdGpuInfo? AmdGpu => Amd.Gpu;
    /// Why no AMD card can be used, null when one can.
    public string? AmdProblem => AmdProtocol.InfoProblem(Amd.Info ?? AmdInfo.Of("failed"));
    public Task<AmdInfo> ReadAmdAsync() => Amd.ReadInfoAsync();
    public Task<GpuTelemetry?> ReadAmdTelemetryAsync() => Amd.ReadTelemetryAsync();
    public Task<AmdProfileState?> ReadAmdProfileAsync() => Amd.ReadProfileAsync();
    public bool AmdLaptop => PcForm.IsLaptop();
    public AmdGpuChoice AmdSettings => _s.AmdGpu;
    public bool AmdTuned => Amd.Tuned;
    public int AmdTempC => Amd.TempC;
    public int? AmdConfirmLeft => Amd.ConfirmBy is DateTime t ? Math.Max(0, (int)Math.Ceiling((t - DateTime.UtcNow).TotalSeconds)) : null;
    public void SetAmdChoice(int? powerPct, int? gpuMaxMHz, int? vramMaxMHz, int tempLimitC)
    {
        var c = _s.AmdGpu;
        (c.PowerPct, c.GpuMaxMHz, c.VramMaxMHz) = (powerPct, gpuMaxMHz, vramMaxMHz);
        c.TempLimitC = Math.Clamp(tempLimitC, AmdGpuChoice.MinTempLimitC, AmdGpuChoice.MaxTempLimitC);
        Save();
    }

    public Task<string> KeepAmdTuningAsync() => GpuGatedAsync(() => Amd.KeepTuningAsync());

    public Task<string> AmdAutoTuneAsync(GpuAutoTuning kind) =>
        _measuring ? Task.FromResult(BusyMeasuring) : GpuGatedAsync(() => Amd.ApplyAutoTuningAsync(kind));

    /// Every plan: putting the card back is never locked.
    public Task<string> AmdResetAsync() => GpuGatedAsync(() => Amd.ResetToFactoryAsync());

    /// '동의 철회', after the withdrawal is on record (every plan): gpu.oc goes off through its journal the way the switch
    /// turns it off, then the card goes to its factory values: AMD with amd.tuning.reset (AmdResetAsync), NVIDIA with
    /// oc.restore of the driver's default power limit and no clock offsets (NvidiaFactoryAsync). The line to show.
    public async Task<string> WithdrawOcAsync()
    {
        var parts = new List<string>();
        if (Has("gpu.oc"))
        {
            parts.Add("오버클럭 / 전력 제한: " + await RevertAsync("gpu.oc"));
            if (Has("gpu.oc"))   // still on (the administrator prompt was declined): the helper puts it back when the app ends
                return string.Join(" · ", parts.Append("켜져 있는 동안은 공장 초기값으로 돌리지 않아요. 앱을 끄면 원래 값으로 돌아가요"));
        }
        var vendor = await GpuVendorAsync();
        string factory;
        if (vendor == GpuVendor.Amd)
        {
            await Amd.ReadInfoAsync();
            factory = Amd.Gpu is { AtFactory: true } ? "이미 공장 초기값이에요" : await AmdResetAsync();
            if (!factory.Contains("공장 초기값")) factory = "공장 초기값으로 돌리지 못했어요: " + factory;
        }
        else if (vendor == GpuVendor.Intel) factory = "이 그래픽카드는 SystemBoost가 전력과 클럭을 바꾸지 않아요";
        else factory = await NvidiaFactoryAsync();
        parts.Add(factory);
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------ AMD, the consent to Article 12-2 (AmdTerms)
    /// No amd.* command is sent because the consent to Article 12-2 is missing (before 2027-01-01 KST, ADLX builds only).
    /// Says nothing about the card: the GPU page shows the button only where the AMD card's TermsText shows.
    public bool AmdWaitsForTerms => Amd.WaitsForTerms;

    /// After the consent to Article 12-2 was recorded: the card is looked at again, now with ADLX, so the GPU page shows
    /// its features without a restart. A record an earlier run left in boost-state.json, kept while the consent was
    /// missing, goes back the way the next start puts it back (RecoverTuningAsync, RecoverProfileAsync).
    public async Task AmdTermsAgreedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _gpuPick = null;
            if (await GpuVendorAsync() == GpuVendor.Amd && !Amd.WaitsForTerms)
            {
                await Amd.RecoverTuningAsync();
                await Amd.RecoverProfileAsync();
            }
            Save();
        }
        catch (Exception e) { Log("AMD 그래픽카드를 다시 확인하지 못했어요: " + e.Message); }
        finally { _gate.Release(); Publish(); }
    }

    /// '동의 철회' of the consent to Article 12-2. What the AMD features have on goes back first, through the switches'
    /// own paths: gpu.oc through its journal (as turning the switch off does), then the game profile switch
    /// (GameProfileOffAsync). Only when nothing of theirs is left is the withdrawal recorded (record, which throws when it
    /// could not be written); from then on no amd.* command is sent. When something could not go back, nothing is
    /// recorded and the features stay usable. The line to show.
    public async Task<string> WithdrawAmdTermsAsync(Action record)
    {
        var parts = new List<string>();
        bool Tuned() => _s.Backups.Any(b => b.Kind == AmdProtocol.TuneKind);
        bool Profile() => _s.Enabled.Contains(GameProfileId) || _s.Backups.Any(b => b.Kind == AmdProtocol.ProfileKind);
        string Kept() => string.Join(" · ", parts.Append("켜져 있는 AMD 기능을 원래대로 돌리지 못해서 동의를 철회하지 않았어요"));
        if (Tuned())
        {
            parts.Add($"{BoostCatalog.All.First(f => f.Id == "gpu.oc").Title}: " + await RevertAsync("gpu.oc"));
            if (Tuned()) return Kept();
        }
        if (Profile())
        {
            parts.Add($"{BoostCatalog.All.First(f => f.Id == GameProfileId).Title}: " + await RevertAsync(GameProfileId));
            if (Profile()) return Kept();
        }
        try { record(); }
        catch (Exception e) { return string.Join(" · ", parts.Append("철회 기록을 저장하지 못했어요: " + e.Message)); }
        _gpuPick = null;
        Publish();
        return string.Join(" · ", parts.Prepend("AMD 구성 요소 조건 동의를 철회했어요. 라데온 드라이버 기능을 껐어요"));
    }

    /// AMD Fluid Motion Frames is on in the driver. SystemBoost never turns it off; frame generation says so instead.
    public async Task<bool> AfmfOnAsync()
    {
        if (!AmdDriverPresent) return false;
        try { return await GpuVendorAsync() == GpuVendor.Amd && (await Amd.ReadProfileAsync())?.Afmf == '1'; }
        catch (Exception) { return false; }
    }

    private async Task<string> GpuGatedAsync(Func<Task<string>> work)
    {
        await _gate.WaitAsync();
        try { var r = await work(); Save(); return r; }
        catch (Exception e) { return "실패했어요: " + e.Message; }
        finally { _gate.Release(); Publish(); }
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
