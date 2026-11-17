// PC 맞춤 추천: the rules from specs and measurements to recommendations. Owner: the advisor part (CONTRACT.md).
// Order on the page: tips that need the user (refresh rate, memory, battery), frame generation, boost items, GPU power.
//   Frame generation  from sb_framegen --bench at the display's resolution: output R = refresh rate, multiplier m in
//                     2, 3, 4, 6, 8; the game must keep R/m >= 30 fps (a 59.94 Hz screen reads 59 Hz, so 0.5 fps of
//                     slack) and the GPU cost analyzeMs*R/m + genMs*R*(1-1/m) <= 200 ms a second (20 %). Auto, the
//                     largest multiplier that fits with any quality, the best quality that fits at it. Nothing fits:
//                     off, with the measured cost. Integrated graphics only: said in the reason.
//   Boost items       Windows-state and spec rules; already on (PcNow.BoostsOn): listed, not applicable. cpu.power only
//                     on a measured gain of 3 % and twice the noise. boost.profiles points to 게임별 측정, not selected.
//   GPU power         a power-limit test without a fault, 3 % and twice the noise, under 80 °C; never selected by default.
//   Tips              never applied: refresh below the screen's best, memory below its rated speed, one stick, battery.
// Every text is Korean in the app's 해요 style and says "측정" (with the numbers), "벤치마크 기준" or "사양 기준";
// docs/pc-check/lang-advisor.json holds the English of each (AdvisorTests checks that every produced text is covered).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SystemBoost.App.Services.PcCheck;

public static class PcAdvisor
{
    public const double FgBudgetMsPerSecond = 200;          // 20 % of the GPU's time
    public const double FgMinSourceFps = 30;                // below it the added delay grows (113 ms under 20 fps)
    internal const double FgSourceSlackFps = 0.5;
    public static readonly IReadOnlyList<int> FgMultipliers = new[] { 2, 3, 4, 6, 8 };
    public static readonly IReadOnlyList<string> FgQualities = new[] { "quality", "balanced", "performance", "low" };   // best first
    public const double MinGainPercent = 3, NoiseFactor = 2;
    public const int GpuMaxTempC = 80;
    public const long SmallRamMB = 16 * 1024 + 512;        // "16 GB or less", with room for what the firmware keeps
    public const double RefreshTipHz = 5;                   // 59 against 60 Hz is the same screen
    public const double SlowRamRatio = 0.95;

    /// GPU milliseconds a second that frame generation takes at output rate outputHz and this multiplier.
    public static double FgCostMs(double analyzeMs, double genMs, double outputHz, int multiplier) =>
        analyzeMs * outputHz / multiplier + genMs * outputHz * (1 - 1.0 / multiplier);

    /// Every recommendation and tip for this PC, in the order the page shows them. Never throws.
    public static IReadOnlyList<Recommendation> Recommend(PcSpecs specs, BenchResults bench, PcNow now)
    {
        var list = new List<Recommendation>();
        if (specs == null) return list;
        bench ??= new BenchResults(null, Array.Empty<FgBench>(), null);
        var on = new HashSet<string>(now?.BoostsOn ?? Array.Empty<string>(), StringComparer.Ordinal);
        var fg = now?.FrameGen ?? new FgChoice("off", 4, "balanced");
        Section(() => Tips(specs, list));
        Section(() => FrameGen(specs, bench, fg, list));
        Section(() => Boosts(specs, bench, on, list));
        Section(() => GpuPower(specs, bench, list));
        return list;
    }

    private static void Section(Action body)
    {
        try { body(); }
        catch (Exception) { }   // one broken part of the input must not hide the rest
    }

    // ------------------------------------------------------------ tips
    private static void Tips(PcSpecs s, List<Recommendation> list)
    {
        var d = s.Display;
        if (d != null && d.RefreshHz > 0 && d.MaxRefreshHz >= d.RefreshHz + RefreshTipHz)
            list.Add(Tip("tip:refresh", K.TitleRefresh, Hz(d.RefreshHz), Hz(d.MaxRefreshHz),
                         Fill(K.RefreshReason, Res(d.Width, d.Height), N0(d.MaxRefreshHz), N0(d.RefreshHz))));
        if (s.RamConfiguredMTs > 0 && s.RamRatedMTs > 0 && s.RamConfiguredMTs < s.RamRatedMTs * SlowRamRatio)
            list.Add(Tip("tip:ram-speed", K.TitleRamSpeed, Fill(K.MTs, s.RamConfiguredMTs), Fill(K.MTsRated, s.RamRatedMTs),
                         Fill(K.RamSpeedReason, s.RamRatedMTs, s.RamConfiguredMTs)));
        if (s.RamSticks == 1)
            list.Add(Tip("tip:ram-sticks", K.TitleRamSticks, K.OneStick, K.TwoSticks, s.Laptop ? K.OneStickLaptopReason : K.OneStickReason));
        if (s.Laptop && s.OnBattery)
            list.Add(Tip("tip:battery", K.TitleBattery, K.Battery, K.Plug, K.BatteryReason));
    }

    private static Recommendation Tip(string id, string title, string current, string proposed, string reason) =>
        new(id, RecKind.Tip, title, current, proposed, reason, Applicable: false, SelectedByDefault: false);

    // ------------------------------------------------------------ frame generation
    private static void FrameGen(PcSpecs s, BenchResults bench, FgChoice now, List<Recommendation> list)
    {
        string current = FgText(now);
        Recommendation Info(string reason) => new("fg", RecKind.FrameGen, K.TitleFg, current, K.NoChange, reason, false, false);
        var entries = (bench.FrameGen ?? Array.Empty<FgBench>()).Where(Valid).ToList();
        if (entries.Count == 0)
        {
            list.Add(Info(string.IsNullOrWhiteSpace(bench.FgError) ? K.FgNotMeasured : Fill(K.FgNotMeasuredWhy, bench.FgError.Trim())));
            return;
        }
        double hz = s.Display?.RefreshHz ?? 0;
        if (!(hz > 0) || !double.IsFinite(hz)) { list.Add(Info(K.FgNoRefresh)); return; }

        var measured = new List<(string Quality, FgBench Bench)>();
        foreach (var q in FgQualities)
            if (Pick(entries, q, s.Display) is { } b) measured.Add((q, b));
        if (measured.Count == 0) { list.Add(Info(K.FgNotMeasured)); return; }

        var gpus = s.Gpus ?? Array.Empty<GpuSpec>();
        bool integratedOnly = gpus.Count > 0 && gpus.All(g => g.Integrated);
        string WithGpu(string reason) => integratedOnly ? Fill(K.FgIntegratedOnly, reason) : reason;
        var off = new FgChoice("off", now.MaxMul, now.Quality);
        Recommendation Off(string reason)
        {
            bool change = !Same(now, off);
            return new("fg", RecKind.FrameGen, K.TitleFg, current, FgText(off), reason, change, change, FrameGen: off);
        }

        var fits = FgMultipliers.Where(m => hz / m >= FgMinSourceFps - FgSourceSlackFps).OrderByDescending(m => m).ToList();
        if (fits.Count == 0) { list.Add(Off(Fill(K.FgRefreshLow, N0(hz), Fps(hz / 2)))); return; }

        foreach (var m in fits)
            foreach (var (q, b) in measured)   // best quality first
            {
                double cost = FgCostMs(b.AnalyzeMs, b.GenMs, hz, m);
                if (cost > FgBudgetMsPerSecond) continue;
                var choice = new FgChoice("auto", m, q);
                bool change = !Same(now, choice);
                list.Add(new("fg", RecKind.FrameGen, K.TitleFg, current, FgText(choice),
                             WithGpu(Fill(K.FgFits, Res(b, s.Display), QualityName(q), N2(b.AnalyzeMs), N2(b.GenMs), N0(hz), m,
                                          Fps(hz / m), N0(cost / 10))),
                             change, change, FrameGen: choice));
                return;
            }

        var cheapest = fits.SelectMany(m => measured.Select(x => (m, x.Quality, x.Bench, Cost: FgCostMs(x.Bench.AnalyzeMs, x.Bench.GenMs, hz, m))))
                           .OrderBy(x => x.Cost).First();
        list.Add(Off(WithGpu(Fill(K.FgTooCostly, Res(cheapest.Bench, s.Display), QualityName(cheapest.Quality), cheapest.m, N0(hz),
                                  N0(cheapest.Cost / 10), N2(cheapest.Bench.AnalyzeMs), N2(cheapest.Bench.GenMs)))));
    }

    private static bool Valid(FgBench b) =>
        b != null && b.Quality != null && double.IsFinite(b.AnalyzeMs) && double.IsFinite(b.GenMs) &&
        b.AnalyzeMs >= 0 && b.GenMs >= 0 && b.AnalyzeMs + b.GenMs > 0;

    /// The quality's run at the display's resolution, otherwise its first run.
    private static FgBench? Pick(List<FgBench> entries, string quality, DisplaySpec? d)
    {
        var mine = entries.Where(b => string.Equals(b.Quality.Trim(), quality, StringComparison.OrdinalIgnoreCase)).ToList();
        return mine.FirstOrDefault(b => d != null && b.Width == d.Width && b.Height == d.Height) ?? mine.FirstOrDefault();
    }

    private static bool Same(FgChoice a, FgChoice b)
    {
        bool mode = string.Equals(a.Mode, b.Mode, StringComparison.OrdinalIgnoreCase);
        if (mode && string.Equals(a.Mode, "off", StringComparison.OrdinalIgnoreCase)) return true;
        return mode && a.MaxMul == b.MaxMul && string.Equals(a.Quality, b.Quality, StringComparison.OrdinalIgnoreCase);
    }

    /// How the FrameGen page would show the choice.
    public static string FgText(FgChoice c)
    {
        var mode = (c.Mode ?? "").Trim().ToLowerInvariant();
        if (mode == "off") return K.Off;
        if (mode == "auto") return Fill(K.FgAuto, c.MaxMul, QualityName(c.Quality));
        if (mode.Length > 1 && mode[0] == 'x' && int.TryParse(mode[1..], NumberStyles.None, Inv, out var m)) return Fill(K.FgFixed, m, QualityName(c.Quality));
        return c.Mode ?? "";
    }

    /// The FrameGen page's names of the qualities.
    public static string QualityName(string? quality) => (quality ?? "").Trim().ToLowerInvariant() switch
    {
        "low" => K.QualityLow,
        "performance" => K.QualityPerformance,
        "balanced" => K.QualityBalanced,
        "quality" => K.QualityQuality,
        _ => quality ?? "",
    };

    // ------------------------------------------------------------ boost items
    private static void Boosts(PcSpecs s, BenchResults bench, HashSet<string> on, List<Recommendation> list)
    {
        var w = s.Windows;
        var gpus = s.Gpus ?? Array.Empty<GpuSpec>();

        if (on.Contains("sys.gamemode")) list.Add(AlreadyOn("sys.gamemode", K.TitleGameMode));
        else if (w?.GameMode != true)
            list.Add(Boost("sys.gamemode", K.TitleGameMode, w?.GameMode == false ? K.Off : K.Unknown, K.On,
                           w?.GameMode == false ? K.GameModeReason : K.GameModeUnknownReason));

        if (on.Contains("sys.dvr")) list.Add(AlreadyOn("sys.dvr", K.TitleDvr));
        else if (w?.XboxDvr != false)
            list.Add(Boost("sys.dvr", K.TitleDvr, w?.XboxDvr == true ? K.On : K.Unknown, K.Off,
                           w?.XboxDvr == true ? K.DvrReason : K.DvrUnknownReason));

        if (on.Contains("sys.focus")) list.Add(AlreadyOn("sys.focus", K.TitleFocus));
        else list.Add(Boost("sys.focus", K.TitleFocus, K.Off, K.On, K.FocusReason));

        var integrated = gpus.FirstOrDefault(g => g.Integrated);
        var discrete = gpus.FirstOrDefault(g => !g.Integrated);
        bool both = integrated != null && discrete != null;
        if (both || (s.Laptop && (gpus.Count == 0 || discrete != null)))
        {
            if (on.Contains("gpu.highperf")) list.Add(AlreadyOn("gpu.highperf", K.TitleHighPerf));
            else list.Add(Boost("gpu.highperf", K.TitleHighPerf, K.WindowsDecides, K.HighPerfGpu,
                                both ? Fill(K.HighPerfTwoReason, integrated!.Name.Trim(), discrete!.Name.Trim()) : K.HighPerfLaptopReason));
        }

        if (s.ECores > 0)
        {
            int p = s.PCores > 0 ? s.PCores : Math.Max(0, s.Cores - s.ECores);
            if (on.Contains("cpu.ecores")) list.Add(AlreadyOn("cpu.ecores", K.TitleECores));
            else list.Add(Boost("cpu.ecores", K.TitleECores, K.Off, K.On, Fill(K.ECoresReason, p, s.ECores)));
        }

        if (s.RamMB > 0 && s.RamMB <= SmallRamMB)
        {
            if (on.Contains("ram.standby")) list.Add(AlreadyOn("ram.standby", K.TitleStandby));
            else list.Add(Boost("ram.standby", K.TitleStandby, K.Off, K.On, Fill(K.StandbyReason, N0(s.RamMB / 1024.0))));
        }

        if (on.Contains("cpu.power")) list.Add(AlreadyOn("cpu.power", K.TitlePower));
        else if (w != null && w.HighPerfPlanExists && !w.HighPerfPlanActive && bench.Cpu is { HighPlanMultiScore: double high } cpu
                 && cpu.MultiScore > 0 && double.IsFinite(high) && double.IsFinite(cpu.MultiScore))
        {
            double gain = (high / cpu.MultiScore - 1) * 100;
            double noise = Math.Max(Finite(cpu.NoisePercent), Finite(cpu.HighPlanNoisePercent ?? 0));
            if (Passes(gain, noise))
                list.Add(Boost("cpu.power", K.TitlePower, K.Off, K.OnDuringGames,
                               Fill(K.PowerPlanReason, N1(gain), N0(cpu.MultiScore), N0(high), N1(noise))));
            else
                list.Add(new("boost:cpu.power", RecKind.Boost, K.TitlePower, K.Off, K.NoChange,
                             Fill(K.PowerPlanSmallReason, Signed1(gain), N0(cpu.MultiScore), N0(high), N1(noise)), false, false, "cpu.power"));
        }

        if (on.Contains("boost.profiles")) list.Add(AlreadyOn("boost.profiles", K.TitleProfiles));
        else list.Add(Boost("boost.profiles", K.TitleProfiles, K.Off, K.On, K.ProfilesReason, selected: false));
    }

    private static Recommendation Boost(string id, string title, string current, string proposed, string reason, bool selected = true) =>
        new("boost:" + id, RecKind.Boost, title, current, proposed, reason, true, selected, BoostFeatureId: id);

    private static Recommendation AlreadyOn(string id, string title) =>
        new("boost:" + id, RecKind.Boost, title, K.On, K.KeepOn, K.AlreadyOn, false, false, BoostFeatureId: id);

    private static bool Passes(double gainPercent, double noisePercent) =>
        double.IsFinite(gainPercent) && gainPercent >= MinGainPercent && gainPercent >= NoiseFactor * noisePercent;

    // ------------------------------------------------------------ GPU power limit
    private static void GpuPower(PcSpecs s, BenchResults bench, List<Recommendation> list)
    {
        if (s.Laptop) return;   // a laptop maker sets power for its case and cooling
        Recommendation Info(string current, string reason) => new("gpu:power", RecKind.Gpu, K.TitleGpuPower, current, K.NoChange, reason, false, false);
        var t = bench.Power;
        if (t == null)
        {
            if (!string.IsNullOrWhiteSpace(bench.PowerError)) list.Add(Info("—", Fill(K.GpuNotTested, bench.PowerError.Trim())));
            return;
        }
        string current = Fill(K.WDefault, t.DefaultW);
        if (!string.IsNullOrWhiteSpace(t.Fault)) { list.Add(Info(current, FaultText(t.Fault.Trim()))); return; }
        if (t.TestedW <= t.DefaultW || !(t.DefaultFps > 0) || !double.IsFinite(t.TestedFps)) return;
        double gain = (t.TestedFps / t.DefaultFps - 1) * 100, noise = Finite(t.NoisePercent);
        if (t.MaxTempC >= GpuMaxTempC)
            list.Add(Info(current, Fill(K.GpuHotReason, t.MaxTempC, t.DefaultW, N0(t.DefaultFps), t.TestedW, N0(t.TestedFps))));
        else if (!Passes(gain, noise))
            list.Add(Info(current, Fill(K.GpuSmallReason, t.DefaultW, N0(t.DefaultFps), t.TestedW, N0(t.TestedFps), Signed1(gain), N1(noise))));
        else
            list.Add(new("gpu:power", RecKind.Gpu, K.TitleGpuPower, current, Fill(K.W, t.TestedW),
                         Fill(K.GpuPowerReason, t.DefaultW, N0(t.DefaultFps), t.TestedW, N0(t.TestedFps), N1(gain), N1(noise), t.MaxTempC),
                         Applicable: true, SelectedByDefault: false, PowerW: t.TestedW));   // overclocking is opt-in
    }

    private static string FaultText(string fault)
    {
        if (fault.Equals("driver", StringComparison.OrdinalIgnoreCase)) return K.GpuFaultDriver;
        if (fault.Equals("helper", StringComparison.OrdinalIgnoreCase)) return K.GpuFaultHelper;
        if (fault.StartsWith("temp:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(fault[5..], NumberStyles.Integer, Inv, out var c)) return Fill(K.GpuFaultTemp, c);
        return Fill(K.GpuFaultOther, fault);
    }

    // ------------------------------------------------------------ formatting
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N0(double v) => v.ToString("0", Inv);
    private static string N1(double v) => v.ToString("0.0", Inv);
    private static string N2(double v) => v.ToString("0.00", Inv);
    private static string Signed1(double v) => v.ToString("+0.0;-0.0;0.0", Inv);
    private static string Fps(double v) => v.ToString("0.#", Inv);
    private static string Hz(double v) => Fill(K.Hz, N0(v));
    private static double Finite(double v) => double.IsFinite(v) && v > 0 ? v : 0;
    private static string Res(int w, int h) => w > 0 && h > 0 ? $"{w}×{h}" : "—";
    private static string Res(FgBench b, DisplaySpec? d) => b.Width > 0 && b.Height > 0 ? Res(b.Width, b.Height) : Res(d?.Width ?? 0, d?.Height ?? 0);
    private static string Fill(string template, params object[] values) =>
        string.Format(Inv, template, values.Select(v => v is IFormattable f ? f.ToString(null, Inv) : v?.ToString() ?? "").ToArray<object>());

    /// The Korean texts. Each one with Hangul has its English in docs/pc-check/lang-advisor.json ({0} slots as Lang.cs).
    internal static class K
    {
        // shared
        public const string Off = "꺼짐", On = "켜짐", Unknown = "확인하지 못했어요", NoChange = "바꾸지 않아요", KeepOn = "그대로 두세요";
        public const string AlreadyOn = "SystemBoost에서 이미 켜 둔 항목이에요.";
        public const string Hz = "{0} Hz", MTs = "{0} MT/s", W = "{0} W";

        // tips
        public const string TitleRefresh = "모니터 주사율";
        public const string RefreshReason = "사양 기준: 이 해상도({0})에서 모니터가 {1} Hz까지 지원하는데 지금은 {2} Hz로 설정돼 있어요. Windows 설정 > 시스템 > 디스플레이 > 고급 디스플레이에서 바꿀 수 있어요. 앱은 이 설정을 바꾸지 않아요.";
        public const string TitleRamSpeed = "메모리 속도";
        public const string MTsRated = "{0} MT/s (정격)";
        public const string RamSpeedReason = "사양 기준: 메모리가 정격 {0} MT/s보다 낮은 {1} MT/s로 돌고 있어요. 메인보드 BIOS에서 XMP·EXPO 프로필이나 메모리 속도 설정을 확인해 보세요 (메인보드와 CPU가 그 속도를 지원해야 해요). 앱은 BIOS를 바꾸지 않아요. 효과는 게임마다 달라요.";
        public const string TitleRamSticks = "메모리 구성";
        public const string OneStick = "메모리 1개", TwoSticks = "같은 규격 2개 (듀얼 채널)";
        public const string OneStickReason = "사양 기준: 메모리가 1개만 꽂혀 있어서 싱글 채널로 돌아요. 같은 규격 2개로 듀얼 채널을 쓰면 이론상 메모리 대역폭이 두 배가 돼요. 효과는 게임마다 달라요.";
        public const string OneStickLaptopReason = "사양 기준: 메모리가 1개만 꽂혀 있어서 싱글 채널로 돌아요. 같은 규격 2개로 듀얼 채널을 쓰면 이론상 메모리 대역폭이 두 배가 돼요. 효과는 게임마다 달라요. 노트북은 메모리 슬롯이 하나뿐인 모델도 있어요.";
        public const string TitleBattery = "노트북 전원";
        public const string Battery = "배터리", Plug = "전원 어댑터 연결";
        public const string BatteryReason = "사양 기준: 노트북이 배터리로 돌고 있어요. 배터리로는 CPU와 GPU 성능을 낮추는 노트북이 많아요. 게임할 때는 전원 어댑터를 연결해 주세요. 이번 측정값도 배터리 상태에서 잰 값이에요.";

        // frame generation
        public const string TitleFg = "프레임 생성";
        public const string FgAuto = "자동 · 최대 {0}배 · 품질 '{1}'";
        public const string FgFixed = "{0}배 고정 · 품질 '{1}'";
        public const string QualityLow = "저사양", QualityPerformance = "성능", QualityBalanced = "균형", QualityQuality = "품질";
        public const string FgFits = "측정: {0}에서 '{1}' 품질은 원본 프레임마다 분석 {2} ms, 만든 프레임마다 생성 {3} ms가 걸려요. {4} Hz를 {5}배로 채우면 원본은 {6} fps, 프레임 생성은 GPU 시간의 {7}%를 써요 (기준: 원본 30 fps 이상, GPU 20% 이하). 게임 없이 잰 값이라 효과는 게임마다 달라요.";
        public const string FgTooCostly = "측정: {0}에서 가장 가벼운 설정('{1}' 품질, {2}배)도 {3} Hz에서 GPU 시간의 {4}%를 써요 (분석 {5} ms, 생성 {6} ms). 기준인 20%를 넘어서 게임이 느려질 수 있어요. 프레임 생성은 꺼 두는 게 좋아요.";
        public const string FgRefreshLow = "사양 기준: 모니터가 {0} Hz라서 2배로 채워도 원본이 {1} fps예요. 원본이 30 fps보다 낮으면 늘어나는 지연이 커져서 프레임 생성을 추천하지 않아요.";
        public const string FgIntegratedOnly = "{0} 내장 GPU만 있어서 프레임 생성이 게임과 같은 GPU를 나눠 써요.";
        public const string FgNotMeasured = "프레임 생성을 측정하지 못해서 추천하지 않아요.";
        public const string FgNotMeasuredWhy = "프레임 생성을 측정하지 못해서 추천하지 않아요: {0}";
        public const string FgNoRefresh = "모니터 주사율을 읽지 못해서 프레임 생성을 추천하지 않아요.";

        // boost items: the titles of BoostCatalog
        public const string TitleGameMode = "Windows 게임 모드", TitleDvr = "Xbox 백그라운드 녹화 끄기", TitleFocus = "게임 중 알림·업데이트 일시정지";
        public const string TitleHighPerf = "게임을 고성능 GPU로 지정", TitleECores = "백그라운드를 E코어로", TitleStandby = "대기 메모리 정리";
        public const string TitlePower = "고성능 전원 계획", TitleProfiles = "게임별 자동 최적화";
        public const string WindowsDecides = "Windows가 정해요", HighPerfGpu = "고성능 GPU", OnDuringGames = "게임 중에만 켜짐";
        public const string GameModeReason = "사양 기준: Windows 게임 모드가 꺼져 있어요. 켜면 게임 중에 Windows 업데이트가 드라이버를 설치하거나 재시작 알림을 띄우지 않아요. 효과는 게임마다 달라요.";
        public const string GameModeUnknownReason = "사양 기준: Windows 게임 모드 설정을 읽지 못했어요. 켜 두면 게임 중에 Windows 업데이트가 드라이버를 설치하거나 재시작 알림을 띄우지 않아요. 효과는 게임마다 달라요.";
        public const string DvrReason = "사양 기준: Xbox 게임 바의 녹화 기능이 켜져 있어요. 켜져 있으면 게임 중에 녹화 준비로 CPU와 GPU를 조금 쓸 수 있어요. 끄면 게임 바 녹화와 백그라운드 녹화가 꺼져요. 효과는 게임마다 달라요.";
        public const string DvrUnknownReason = "사양 기준: Xbox 녹화 설정을 읽지 못했어요. 끄면 게임 바 녹화와 백그라운드 녹화가 꺼져요. 효과는 게임마다 달라요.";
        public const string FocusReason = "사양 기준: 게임 중에만 Windows 알림을 끄고 Windows 업데이트를 최대 1일 멈춰요. 게임이 끝나면 원래대로 돌려요. 업데이트를 멈추려면 관리자 권한이 필요해요. 효과는 게임마다 달라요.";
        public const string HighPerfTwoReason = "사양 기준: 내장 GPU({0})와 외장 GPU({1})가 함께 있어요. 게임이 내장 GPU에서 돌지 않게 고성능 GPU로 지정해요. 다음 게임 실행부터 적용돼요. 효과는 게임마다 달라요.";
        public const string HighPerfLaptopReason = "사양 기준: 노트북이에요. 게임이 절전용 GPU에서 돌지 않게 고성능 GPU로 지정해요. 다음 게임 실행부터 적용돼요. 효과는 게임마다 달라요.";
        public const string ECoresReason = "사양 기준: P코어 {0}개와 E코어 {1}개가 있는 하이브리드 CPU예요. 게임 중에만 백그라운드 프로그램을 E코어로 옮겨서 P코어를 게임에 남겨요. 방송·음성 프로그램은 옮기지 않아요. 효과는 게임마다 달라요.";
        public const string StandbyReason = "사양 기준: 메모리가 {0} GB예요. 게임 중 여유 메모리가 설정값 아래로 떨어질 때만 대기 메모리를 정리해요. 관리자 권한이 필요해요. 효과는 게임마다 달라요.";
        public const string PowerPlanReason = "벤치마크 기준: 고성능 전원 계획에서 CPU 전체 코어 점수가 {0}% 높았어요 (측정: {1} → {2}, 반복 간 편차 {3}%). 게임 중에만 바꾸고, 끝나면 원래 계획으로 돌려요. 게임에서의 효과는 게임마다 달라요.";
        public const string PowerPlanSmallReason = "벤치마크 기준: 고성능 전원 계획에서 CPU 전체 코어 점수 차이가 {0}%였어요 (측정: {1} → {2}, 반복 간 편차 {3}%). 3% 이상이면서 편차의 두 배를 넘어야 추천해요.";
        public const string ProfilesReason = "게임에서 직접 재는 항목이에요. '게임별 측정'에서 게임마다 고성능 전원 계획과 E코어 배치를 껐다 켜며 FPS를 재고, 3% 이상 오르고 측정 오차보다 큰 것만 그 게임에서 켜요. 이 검사는 게임 없이 해서 이 항목은 재지 않았어요.";

        // GPU power limit
        public const string TitleGpuPower = "그래픽카드 전력 제한";
        public const string WDefault = "{0} W (기본)";
        public const string GpuPowerReason = "측정: 같은 시험 부하에서 {0} W일 때 초당 {1}프레임, {2} W일 때 {3}프레임이었어요 (+{4}%, 반복 간 편차 {5}%, 최고 {6}°C). 클럭은 그대로 두고 전력 제한만 올려서 전력을 더 쓰고 열이 더 나요. 오버클럭 안내에 동의해야 적용되고, 앱이 켜져 있는 동안만 유지돼요. 게임에서의 효과는 게임마다 달라요.";
        public const string GpuSmallReason = "측정: 같은 시험 부하에서 {0} W일 때 초당 {1}프레임, {2} W일 때 {3}프레임이었어요 ({4}%, 반복 간 편차 {5}%). 3% 이상이면서 편차의 두 배를 넘어야 추천해서 전력 제한은 추천하지 않아요.";
        public const string GpuHotReason = "측정: 시험 중 온도가 {0}°C까지 올라갔어요 ({1} W일 때 초당 {2}프레임, {3} W일 때 {4}프레임). 80°C 아래일 때만 추천해서 전력 제한은 추천하지 않아요.";
        public const string GpuFaultDriver = "측정: 시험 중 드라이버 오류가 생겨서 멈췄어요. 그래픽카드는 기본 전력 제한으로 돌려놨어요. 전력 제한은 추천하지 않아요.";
        public const string GpuFaultTemp = "측정: 시험 중 온도가 {0}°C에 닿아서 멈췄어요. 그래픽카드는 기본 전력 제한으로 돌려놨어요. 전력 제한은 추천하지 않아요.";
        public const string GpuFaultHelper = "측정: 시험 중 관리자 도우미에 문제가 생겨서 멈췄어요. 그래픽카드는 기본 전력 제한으로 돌려놨어요. 전력 제한은 추천하지 않아요.";
        public const string GpuFaultOther = "측정: 시험이 중간에 멈췄어요 ({0}). 그래픽카드는 기본 전력 제한으로 돌려놨어요. 전력 제한은 추천하지 않아요.";
        public const string GpuNotTested = "전력 제한 시험을 하지 못했어요: {0}";
    }
}
