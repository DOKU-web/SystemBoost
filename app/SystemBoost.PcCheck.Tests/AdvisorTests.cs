// The advisor part's checks: PcAdvisor rules, CpuBenchmark, the spec parsers, and the English of every advisor text.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using SystemBoost.App.Services.PcCheck;

namespace SystemBoost.PcCheck.Tests;

internal static class AdvisorTests
{
    // ------------------------------------------------------------ inputs
    private static readonly GpuSpec Rtx = new("NVIDIA GeForce RTX 4070", "nvidia", false, 12282, "0");
    private static readonly GpuSpec RtxLaptop = new("NVIDIA GeForce RTX 4060 Laptop GPU", "nvidia", false, 8188, "0");
    private static readonly GpuSpec Uhd = new("Intel(R) UHD Graphics 770", "intel", true, 128, "1");
    private static readonly WindowsState Win = new(Hags: true, HagsSupported: true, GameMode: true, XboxDvr: false,
                                                   HighPerfPlanActive: false, HighPerfPlanExists: true);
    private static readonly PcNow Nothing = new(Array.Empty<string>(), new FgChoice("off", 4, "balanced"));

    private static PcSpecs Specs(double hz = 144, double maxHz = 0, int w = 2560, int h = 1440, long ramMB = 32768,
                                 int configured = 3200, int rated = 3200, int sticks = 2, bool laptop = false, bool battery = false,
                                 int pCores = 8, int eCores = 0, GpuSpec[]? gpus = null, WindowsState? win = null) =>
        new("Test CPU", pCores + eCores, pCores * 2 + eCores, pCores, eCores, ramMB, configured, rated, sticks,
            gpus ?? new[] { Rtx }, new DisplaySpec(w, h, hz, maxHz > 0 ? maxHz : hz), laptop, battery, win ?? Win);

    /// The four qualities at one resolution: analysis and generation ms of each (low, performance, balanced, quality).
    private static FgBench[] Fg(int w, int h, params double[] ms) => new[]
    {
        new FgBench("low", w, h, ms[0], ms[1], "Test GPU"), new FgBench("performance", w, h, ms[2], ms[3], "Test GPU"),
        new FgBench("balanced", w, h, ms[4], ms[5], "Test GPU"), new FgBench("quality", w, h, ms[6], ms[7], "Test GPU"),
    };
    private static FgBench[] Fast(int w = 2560, int h = 1440) => Fg(w, h, 0.40, 0.15, 0.60, 0.20, 0.90, 0.30, 1.40, 0.45);
    private static FgBench[] Slow(int w = 2560, int h = 1440) => Fg(w, h, 2.0, 0.8, 3.0, 1.2, 4.5, 1.8, 7.0, 2.8);
    private static FgBench[] Heavy(int w = 2560, int h = 1440) => Fg(w, h, 6.0, 3.0, 8.0, 4.0, 11.0, 5.0, 15.0, 7.0);

    private static BenchResults Bench(FgBench[]? fg = null, CpuBench? cpu = null, GpuPowerTest? power = null,
                                      string? fgError = null, string? powerError = null) =>
        new(cpu ?? new CpuBench(1000, 10000, 1.0), fg ?? Fast(), power, FgError: fgError, PowerError: powerError);

    private static PcNow On(params string[] ids) => new(ids, Nothing.FrameGen);

    // every result of every scenario, for the checks over all texts at the end
    private static readonly List<Recommendation> All = new();

    private static IReadOnlyList<Recommendation> Rec(PcSpecs specs, BenchResults? bench = null, PcNow? now = null)
    {
        var r = PcAdvisor.Recommend(specs, bench ?? Bench(), now ?? Nothing);
        All.AddRange(r);
        return r;
    }

    private static Recommendation Get(IReadOnlyList<Recommendation> r, string id) =>
        r.FirstOrDefault(x => x.Id == id) ?? throw new CheckFailed($"no recommendation {id} (have {string.Join(", ", r.Select(x => x.Id))})");

    private static bool Has(IReadOnlyList<Recommendation> r, string id) => r.Any(x => x.Id == id);

    private static void Contains(string text, string part, string what) => T.Check(text.Contains(part), $"{what}: \"{part}\" not in \"{text}\"");

    public static void Run()
    {
        FrameGeneration();
        Boosts();
        GpuPower();
        Tips();
        Robustness();
        Invariants();
        Translations();
        Benchmark();
        Parsers();
    }

    // ------------------------------------------------------------ frame generation
    private static void FrameGeneration()
    {
        T.Test("fg: the budget formula", () =>
        {
            T.Equal(90.0, PcAdvisor.FgCostMs(1, 0.5, 144, 4), "1 ms + 0.5 ms at 144 Hz, 4x");
            T.Equal(30.0 + 15.0, PcAdvisor.FgCostMs(1, 0.5, 60, 2), "60 Hz, 2x");
        });

        T.Test("fg: 60 Hz, fast GPU -> auto, 2x (the game keeps 30 fps), best quality", () =>
        {
            var fg = Get(Rec(Specs(hz: 60)), "fg");
            T.Equal(new FgChoice("auto", 2, "quality"), fg.FrameGen, "choice");
            T.Check(fg.Applicable && fg.SelectedByDefault, "applicable and selected");
            T.Equal(RecKind.FrameGen, fg.Kind, "kind");
            Contains(fg.Reason, "측정: 2560×1440", "resolution");
            Contains(fg.Reason, "분석 1.40 ms", "measured analysis");
            Contains(fg.Reason, "생성 0.45 ms", "measured generation");
            Contains(fg.Reason, "원본은 30 fps", "source fps");
            Contains(fg.Reason, "효과는 게임마다 달라요", "unmeasured in games");
            T.Equal("자동 · 최대 2배 · 품질 '품질'", fg.Proposed, "proposed");
            T.Equal("꺼짐", fg.Current, "current");
        });

        T.Test("fg: 144 Hz, fast GPU -> 4x (6x would leave 24 fps), quality", () =>
        {
            var fg = Get(Rec(Specs(hz: 144)), "fg");
            T.Equal(new FgChoice("auto", 4, "quality"), fg.FrameGen, "choice (fits at 2x too: 133 ms)");
            Contains(fg.Reason, "GPU 시간의 10%", "cost 99 ms a second");
            T.Check(!fg.Reason.Contains("2배로 도는데"), "no 2x note when it changed nothing");
        });

        T.Test("fg: 240 Hz, fast GPU -> 8x; quality fits at 8x (137 ms) but not at 2x (222 ms) -> balanced, said", () =>
        {
            var fg = Get(Rec(Specs(hz: 240)), "fg");
            T.Equal(new FgChoice("auto", 8, "balanced"), fg.FrameGen, "choice");
            Contains(fg.Reason, "2배에서는 '품질' 품질이 GPU 시간의 22%를 써서 '균형' 품질을 골랐어요", "the 2x note");
            Contains(fg.Reason, "240 Hz를 8배로 채우면 원본은 30 fps", "the chosen multiplier");
        });

        T.Test("fg: 360 Hz, fast GPU -> 8x; quality over budget at 8x, balanced over at 2x (216 ms) -> performance", () =>
        {
            var fg = Get(Rec(Specs(hz: 360)), "fg");
            T.Equal(new FgChoice("auto", 8, "performance"), fg.FrameGen, "choice");
            Contains(fg.Reason, "'균형' 품질이 GPU 시간의 22%를 써서 '성능' 품질을 골랐어요", "the note names the best quality at 8x");
        });

        T.Test("fg: 239 Hz (a 239.76 Hz screen) still gets 8x", () =>
            T.Equal(8, Get(Rec(Specs(hz: 239)), "fg").FrameGen!.MaxMul, "max multiplier"));

        T.Test("fg: 60 Hz, slow GPU -> 2x on balanced (quality would take 294 ms)", () =>
        {
            var fg = Get(Rec(Specs(hz: 60), Bench(Slow())), "fg");
            T.Equal(new FgChoice("auto", 2, "balanced"), fg.FrameGen, "choice");
            Contains(fg.Reason, "'균형' 품질", "quality named");
            T.Check(!fg.Reason.Contains("2배로 도는데"), "2x is the multiplier itself: no note");
        });

        T.Test("fg: 144 Hz, slow GPU -> low fits at 4x (158 ms) but not at 2x (201.6 ms) -> off, the 2x cost named", () =>
        {
            var fg = Get(Rec(Specs(hz: 144), Bench(Slow())), "fg");
            T.Equal("off", fg.FrameGen?.Mode, "off");
            T.Check(!fg.Applicable, "already off");
            Contains(fg.Reason, "가장 가벼운 '저사양' 품질도 144 Hz를 2배로 채울 때 GPU 시간의 20.2%를 써요", "the 2x cost");
            Contains(fg.Reason, "분석 2.00 ms, 생성 0.80 ms", "measured ms");
        });

        T.Test("fg: 240 Hz, slow GPU -> nothing fits, off with the measured cost", () =>
        {
            var fg = Get(Rec(Specs(hz: 240), Bench(Slow())), "fg");
            T.Equal("off", fg.FrameGen?.Mode, "off");
            T.Check(!fg.Applicable && !fg.SelectedByDefault, "already off: nothing to apply");
            Contains(fg.Reason, "GPU 시간의 23%", "cheapest cost (8x low, 228 ms)");
            Contains(fg.Reason, "'저사양' 품질, 8배", "cheapest setting");
            Contains(fg.Reason, "분석 2.00 ms, 생성 0.80 ms", "measured ms");
        });

        T.Test("fg: nothing fits while it is on at 4x -> turn it off (applicable)", () =>
        {
            var now = new PcNow(Array.Empty<string>(), new FgChoice("x4", 4, "balanced"));
            var fg = Get(Rec(Specs(hz: 144), Bench(Heavy()), now), "fg");
            T.Equal(new FgChoice("off", 4, "balanced"), fg.FrameGen, "off, the rest kept");
            T.Check(fg.Applicable && fg.SelectedByDefault, "applicable");
            T.Equal("4배 고정 · 품질 '균형'", fg.Current, "current");
            Contains(fg.Reason, "GPU 시간의 54%", "cheapest cost");
        });

        T.Test("fg: integrated graphics only, heavy -> off, and says so", () =>
        {
            var fg = Get(Rec(Specs(hz: 60, w: 1920, h: 1080, gpus: new[] { Uhd }), Bench(Heavy(1920, 1080))), "fg");
            T.Equal("off", fg.FrameGen?.Mode, "off");
            Contains(fg.Reason, "내장 GPU만 있어서", "integrated only");
            Contains(fg.Reason, "1920×1080", "resolution");
        });

        T.Test("fg: integrated graphics only, light enough -> recommended, and says so", () =>
        {
            var fg = Get(Rec(Specs(hz: 60, w: 1920, h: 1080, gpus: new[] { Uhd }), Bench(Fast(1920, 1080))), "fg");
            T.Equal("auto", fg.FrameGen?.Mode, "auto");
            Contains(fg.Reason, "내장 GPU만 있어서", "integrated only");
        });

        T.Test("fg: a discrete GPU next to the integrated one -> no integrated-only note", () =>
            T.Check(!Get(Rec(Specs(gpus: new[] { Uhd, Rtx })), "fg").Reason.Contains("내장 GPU만"), "no note"));

        T.Test("fg: the same choice already set -> not applicable", () =>
        {
            var now = new PcNow(Array.Empty<string>(), new FgChoice("auto", 4, "quality"));
            var fg = Get(Rec(Specs(hz: 144), Bench(), now), "fg");
            T.Check(!fg.Applicable && !fg.SelectedByDefault, "nothing to change");
            T.Equal(fg.Current, fg.Proposed, "current = proposed");
        });

        T.Test("fg: a 50 Hz screen -> 2x would leave 25 fps, off (spec based)", () =>
        {
            var fg = Get(Rec(Specs(hz: 50)), "fg");
            T.Equal("off", fg.FrameGen?.Mode, "off");
            Contains(fg.Reason, "사양 기준", "spec based");
            Contains(fg.Reason, "원본이 25 fps", "source");
        });

        T.Test("fg: not measured -> no recommendation, the error named", () =>
        {
            var fg = Get(Rec(Specs(), Bench(Array.Empty<FgBench>(), fgError: "sb_framegen exit 3")), "fg");
            T.Check(!fg.Applicable && fg.FrameGen == null, "nothing to apply");
            T.Equal("프레임 생성을 측정하지 못해서 추천하지 않아요: sb_framegen exit 3", fg.Reason, "reason");
            T.Equal("프레임 생성을 측정하지 못해서 추천하지 않아요.", Get(Rec(Specs(), Bench(Array.Empty<FgBench>())), "fg").Reason, "without an error");
        });

        T.Test("fg: refresh rate unknown -> no recommendation", () =>
            T.Check(!Get(Rec(Specs(hz: 0)), "fg").Applicable, "not applicable"));

        T.Test("fg: only some qualities measured -> the best of those", () =>
        {
            var bench = Bench(new[] { new FgBench("performance", 2560, 1440, 0.6, 0.2), new FgBench("low", 2560, 1440, 0.4, 0.15) });
            T.Equal(new FgChoice("auto", 4, "performance"), Get(Rec(Specs(), bench), "fg").FrameGen, "choice");
        });

        T.Test("fg: the run at the display's resolution is preferred", () =>
        {
            var bench = Bench(Heavy(3840, 2160).Concat(Fast()).ToArray());
            var fg = Get(Rec(Specs(hz: 144), bench), "fg");
            T.Equal("auto", fg.FrameGen?.Mode, "the 1440p runs used");
            Contains(fg.Reason, "2560×1440", "resolution");
        });
    }

    // ------------------------------------------------------------ boost items
    private static void Boosts()
    {
        T.Test("boost: Game Mode off -> on; already on in Windows -> nothing", () =>
        {
            var r = Rec(Specs(win: Win with { GameMode = false }));
            var gm = Get(r, "boost:sys.gamemode");
            T.Check(gm.Applicable && gm.SelectedByDefault, "applicable");
            T.Equal("sys.gamemode", gm.BoostFeatureId, "feature");
            T.Equal("꺼짐", gm.Current, "current");
            T.Equal("켜짐", gm.Proposed, "proposed");
            T.Check(!Has(Rec(Specs()), "boost:sys.gamemode"), "Game Mode on: no line");
            T.Equal("확인하지 못했어요", Get(Rec(Specs(win: Win with { GameMode = null })), "boost:sys.gamemode").Current, "unreadable");
        });

        T.Test("boost: Xbox recording on -> off; off -> nothing", () =>
        {
            var dvr = Get(Rec(Specs(win: Win with { XboxDvr = true })), "boost:sys.dvr");
            T.Check(dvr.Applicable, "applicable");
            T.Equal("켜짐", dvr.Current, "current");
            T.Equal("꺼짐", dvr.Proposed, "proposed");
            T.Check(!Has(Rec(Specs()), "boost:sys.dvr"), "recording off: no line");
        });

        T.Test("boost: notifications and updates pause, always offered", () =>
            T.Check(Get(Rec(Specs()), "boost:sys.focus").Applicable, "applicable"));

        T.Test("boost: laptop with two GPUs -> high performance GPU, both named", () =>
        {
            var hp = Get(Rec(Specs(laptop: true, gpus: new[] { Uhd, RtxLaptop })), "boost:gpu.highperf");
            T.Check(hp.Applicable && hp.SelectedByDefault, "applicable");
            Contains(hp.Reason, "Intel(R) UHD Graphics 770", "integrated named");
            Contains(hp.Reason, "RTX 4060 Laptop GPU", "discrete named");
        });

        T.Test("boost: laptop with one discrete GPU -> high performance GPU (laptop reason)", () =>
            Contains(Get(Rec(Specs(laptop: true, gpus: new[] { RtxLaptop })), "boost:gpu.highperf").Reason, "노트북이에요", "laptop"));

        T.Test("boost: desktop with an integrated and a discrete GPU -> high performance GPU", () =>
            T.Check(Has(Rec(Specs(gpus: new[] { Uhd, Rtx })), "boost:gpu.highperf"), "offered"));

        T.Test("boost: desktop with one card, or a laptop with integrated graphics only -> no GPU choice", () =>
        {
            T.Check(!Has(Rec(Specs()), "boost:gpu.highperf"), "desktop, one card");
            T.Check(!Has(Rec(Specs(laptop: true, gpus: new[] { Uhd })), "boost:gpu.highperf"), "nothing to choose");
        });

        T.Test("boost: hybrid CPU -> E-cores, with the core counts", () =>
        {
            var e = Get(Rec(Specs(pCores: 8, eCores: 16)), "boost:cpu.ecores");
            T.Check(e.Applicable && e.SelectedByDefault, "applicable");
            Contains(e.Reason, "P코어 8개와 E코어 16개", "counts");
            T.Check(!Has(Rec(Specs(pCores: 8, eCores: 0)), "boost:cpu.ecores"), "not hybrid: no line");
        });

        T.Test("boost: standby memory at 8 and 16 GB, not at 32 GB", () =>
        {
            Contains(Get(Rec(Specs(ramMB: 8192)), "boost:ram.standby").Reason, "메모리가 8 GB예요", "8 GB");
            Contains(Get(Rec(Specs(ramMB: 16384)), "boost:ram.standby").Reason, "메모리가 16 GB예요", "16 GB");
            T.Check(!Has(Rec(Specs(ramMB: 32768)), "boost:ram.standby"), "32 GB: no line");
            T.Check(!Has(Rec(Specs(ramMB: 0)), "boost:ram.standby"), "unknown: no line");
        });

        T.Test("boost: high-performance plan measured +6 % at 1.2 % noise -> recommended (benchmark)", () =>
        {
            var p = Get(Rec(Specs(), Bench(cpu: new CpuBench(1000, 10000, 1.0, 10600, 1.2))), "boost:cpu.power");
            T.Check(p.Applicable && p.SelectedByDefault, "applicable");
            Contains(p.Reason, "벤치마크 기준", "says benchmark");
            Contains(p.Reason, "6.0% 높았어요", "gain");
            Contains(p.Reason, "10000 → 10600", "scores");
            Contains(p.Reason, "게임 중에만", "only during games");
        });

        T.Test("boost: high-performance plan +2 % -> shown, not recommended", () =>
        {
            var p = Get(Rec(Specs(), Bench(cpu: new CpuBench(1000, 10000, 0.5, 10200, 0.5))), "boost:cpu.power");
            T.Check(!p.Applicable && !p.SelectedByDefault, "not applicable");
            Contains(p.Reason, "+2.0%", "gain");
        });

        T.Test("boost: high-performance plan +6 % at 4 % noise -> not recommended (under twice the noise)", () =>
        {
            var p = Get(Rec(Specs(), Bench(cpu: new CpuBench(1000, 10000, 1.0, 10600, 4.0))), "boost:cpu.power");
            T.Check(!p.Applicable, "not applicable");
            Contains(p.Reason, "편차 4.0%", "noise named");
            T.Check(!Get(Rec(Specs(), Bench(cpu: new CpuBench(1000, 10000, 4.0, 10600, 1.0))), "boost:cpu.power").Applicable,
                    "the larger noise of the two runs counts");
        });

        T.Test("boost: high-performance plan not measured, active or missing -> no line", () =>
        {
            T.Check(!Has(Rec(Specs()), "boost:cpu.power"), "not measured");
            var cpu = new CpuBench(1000, 10000, 1.0, 10600, 1.0);
            T.Check(!Has(Rec(Specs(win: Win with { HighPerfPlanActive = true }), Bench(cpu: cpu)), "boost:cpu.power"), "active");
            T.Check(!Has(Rec(Specs(win: Win with { HighPerfPlanExists = false }), Bench(cpu: cpu)), "boost:cpu.power"), "missing");
        });

        T.Test("boost: per-game optimization points to 게임별 측정, not selected", () =>
        {
            var p = Get(Rec(Specs()), "boost:boost.profiles");
            T.Check(p.Applicable && !p.SelectedByDefault, "applicable, not selected");
            Contains(p.Reason, "'게임별 측정'", "pointer");
        });

        T.Test("boost: items already on -> listed, not applicable", () =>
        {
            var ids = new[] { "sys.gamemode", "sys.dvr", "sys.focus", "gpu.highperf", "cpu.ecores", "ram.standby", "cpu.power", "boost.profiles" };
            var r = Rec(Specs(laptop: true, ramMB: 16384, pCores: 6, eCores: 8, gpus: new[] { Uhd, RtxLaptop },
                              win: Win with { GameMode = false, XboxDvr = true }),
                        Bench(cpu: new CpuBench(1000, 10000, 1.0, 10600, 1.0)), On(ids));
            foreach (var id in ids)
            {
                var x = Get(r, "boost:" + id);
                T.Check(!x.Applicable && !x.SelectedByDefault, id + " not applicable");
                T.Equal("켜짐", x.Current, id + " current");
            }
        });

        T.Test("boost: the order of the boost items", () =>
        {
            var r = Rec(Specs(laptop: true, ramMB: 8192, eCores: 8, gpus: new[] { Uhd, RtxLaptop }, win: Win with { GameMode = false, XboxDvr = true }),
                        Bench(cpu: new CpuBench(1000, 10000, 1.0, 10600, 1.0)));
            var order = r.Where(x => x.Kind == RecKind.Boost).Select(x => x.BoostFeatureId).ToArray();
            T.Equal("sys.gamemode,sys.dvr,sys.focus,gpu.highperf,cpu.ecores,ram.standby,cpu.power,boost.profiles", string.Join(",", order), "order");
        });
    }

    // ------------------------------------------------------------ GPU power
    private static GpuPowerTest Power(double testedFps = 1050, double noise = 1.0, int temp = 72, string? fault = null) =>
        new("NVIDIA GeForce RTX 4070", 200, 220, 1000, testedFps, noise, temp, fault);

    private static void GpuPower()
    {
        T.Test("gpu: +5 % at 1 % noise, 72 C -> offered, never selected by default", () =>
        {
            var g = Get(Rec(Specs(), Bench(power: Power())), "gpu:power");
            T.Equal(RecKind.Gpu, g.Kind, "kind");
            T.Check(g.Applicable && !g.SelectedByDefault, "opt-in");
            T.Equal(220, g.PowerW, "the tested limit");
            T.Equal("200 W (기본)", g.Current, "current");
            T.Equal("220 W", g.Proposed, "proposed");
            Contains(g.Reason, "200 W일 때 초당 1000프레임, 220 W일 때 1050프레임", "measured fps");
            Contains(g.Reason, "오버클럭 안내", "the notice");
            Contains(g.Reason, "클럭은 그대로", "clocks untouched");
        });

        T.Test("gpu: a fault -> not offered, the fault named", () =>
        {
            var g = Get(Rec(Specs(), Bench(power: Power(fault: "driver"))), "gpu:power");
            T.Check(!g.Applicable && g.PowerW == null, "not applicable");
            Contains(g.Reason, "드라이버 오류", "driver");
            Contains(Get(Rec(Specs(), Bench(power: Power(fault: "temp:87"))), "gpu:power").Reason, "87°C에 닿아서", "temperature");
            Contains(Get(Rec(Specs(), Bench(power: Power(fault: "helper"))), "gpu:power").Reason, "관리자 도우미", "helper");
            Contains(Get(Rec(Specs(), Bench(power: Power(fault: "nvml"))), "gpu:power").Reason, "(nvml)", "other");
        });

        T.Test("gpu: 82 C -> not offered", () =>
        {
            var g = Get(Rec(Specs(), Bench(power: Power(temp: 82))), "gpu:power");
            T.Check(!g.Applicable, "not applicable");
            Contains(g.Reason, "82°C까지", "temperature");
        });

        T.Test("gpu: +1 % -> not offered; +5 % at 3 % noise -> not offered", () =>
        {
            var g = Get(Rec(Specs(), Bench(power: Power(testedFps: 1010))), "gpu:power");
            T.Check(!g.Applicable, "small gain");
            Contains(g.Reason, "+1.0%", "gain");
            T.Check(!Get(Rec(Specs(), Bench(power: Power(noise: 3.0))), "gpu:power").Applicable, "noisy");
        });

        T.Test("gpu: laptop -> no power-limit line even with a test", () =>
            T.Check(!Has(Rec(Specs(laptop: true, gpus: new[] { RtxLaptop }), Bench(power: Power())), "gpu:power"), "no line"));

        T.Test("gpu: no test -> no line; the test could not run -> said", () =>
        {
            T.Check(!Has(Rec(Specs()), "gpu:power"), "no line");
            var g = Get(Rec(Specs(), Bench(powerError: "consent declined")), "gpu:power");
            T.Check(!g.Applicable, "not applicable");
            T.Equal("전력 제한 시험을 하지 못했어요: consent declined", g.Reason, "reason");
        });
    }

    // ------------------------------------------------------------ tips
    private static void Tips()
    {
        T.Test("tip: display set to 60 Hz, offers 144 Hz -> refresh tip first", () =>
        {
            var r = Rec(Specs(hz: 60, maxHz: 144));
            var t = Get(r, "tip:refresh");
            T.Equal(0, r.ToList().IndexOf(t), "first");
            T.Check(t.Kind == RecKind.Tip && !t.Applicable && !t.SelectedByDefault, "a tip");
            T.Equal("60 Hz", t.Current, "current");
            T.Equal("144 Hz", t.Proposed, "proposed");
            Contains(t.Reason, "144 Hz까지", "max");
            T.Equal(new FgChoice("auto", 2, "quality"), Get(r, "fg").FrameGen, "frame generation follows the refresh set now");
        });

        T.Test("tip: 59 against 60 Hz is no tip, nor a screen at its best", () =>
        {
            T.Check(!Has(Rec(Specs(hz: 59, maxHz: 60)), "tip:refresh"), "59/60");
            T.Check(!Has(Rec(Specs(hz: 144, maxHz: 144)), "tip:refresh"), "at max");
        });

        T.Test("tip: memory below its rated speed -> BIOS tip", () =>
        {
            var t = Get(Rec(Specs(configured: 2133, rated: 3200)), "tip:ram-speed");
            T.Check(!t.Applicable, "a tip");
            T.Equal("2133 MT/s", t.Current, "current");
            T.Equal("3200 MT/s (정격)", t.Proposed, "proposed");
            Contains(t.Reason, "XMP·EXPO", "how");
            Contains(t.Reason, "앱은 BIOS를 바꾸지 않아요", "the app does not");
            T.Check(!Has(Rec(Specs(configured: 3200, rated: 3200)), "tip:ram-speed"), "at speed");
            T.Check(!Has(Rec(Specs(configured: 0, rated: 3200)), "tip:ram-speed"), "unknown");
            T.Check(!Has(Rec(Specs(configured: 3600, rated: 2133)), "tip:ram-speed"), "above the JEDEC figure (XMP on)");
        });

        T.Test("tip: one stick -> dual channel tip (laptop text says some have one slot)", () =>
        {
            var t = Get(Rec(Specs(sticks: 1)), "tip:ram-sticks");
            T.Check(!t.Applicable, "a tip");
            T.Check(!t.Reason.Contains("노트북"), "desktop text");
            Contains(Get(Rec(Specs(sticks: 1, laptop: true, gpus: new[] { RtxLaptop })), "tip:ram-sticks").Reason, "슬롯이 하나뿐인", "laptop text");
            T.Check(!Has(Rec(Specs(sticks: 2)), "tip:ram-sticks"), "two sticks");
            T.Check(!Has(Rec(Specs(sticks: 0)), "tip:ram-sticks"), "unknown or soldered");
        });

        T.Test("tip: laptop on battery -> plug in", () =>
        {
            var t = Get(Rec(Specs(laptop: true, battery: true, gpus: new[] { RtxLaptop })), "tip:battery");
            T.Check(!t.Applicable, "a tip");
            Contains(t.Reason, "배터리 상태에서 잰 값", "measurements on battery");
            T.Check(!Has(Rec(Specs(laptop: true, battery: false, gpus: new[] { RtxLaptop })), "tip:battery"), "plugged in");
        });

        T.Test("order: tips, frame generation, boost items, GPU power", () =>
        {
            var r = Rec(Specs(hz: 60, maxHz: 144, configured: 2133, rated: 3200, sticks: 1, ramMB: 8192, eCores: 8,
                              gpus: new[] { Uhd, Rtx }, win: Win with { GameMode = false }),
                        Bench(power: Power(), cpu: new CpuBench(1000, 10000, 1.0, 10600, 1.0)));
            int Rank(Recommendation x) => x.Kind switch { RecKind.Tip => 0, RecKind.FrameGen => 1, RecKind.Boost => 2, _ => 3 };
            var ranks = r.Select(Rank).ToArray();
            T.Check(ranks.SequenceEqual(ranks.OrderBy(x => x)), "kinds in order: " + string.Join(",", r.Select(x => x.Id)));
            T.Equal("tip:refresh,tip:ram-speed,tip:ram-sticks", string.Join(",", r.Where(x => x.Kind == RecKind.Tip).Select(x => x.Id)), "tips");
            T.Check(ranks.Distinct().Count() == 4, "all four kinds");
        });
    }

    // ------------------------------------------------------------ robustness
    private static void Robustness()
    {
        T.Test("never throws: nulls everywhere", () =>
        {
            T.Equal(0, PcAdvisor.Recommend(null!, null!, null!).Count, "no specs: nothing");
            var specs = new PcSpecs("", 0, 0, 0, 0, 0, 0, 0, 0, null!, null!, false, false, null!);
            var r = PcAdvisor.Recommend(specs, new BenchResults(null, null!, null), new PcNow(null!, null!));
            T.Check(r.Count > 0, "still the spec-free items");
            All.AddRange(r);
        });

        T.Test("never throws: broken measurements", () =>
        {
            var fg = new[] { new FgBench("balanced", 0, 0, double.NaN, 1), new FgBench(null!, 0, 0, 1, 1), new FgBench("odd", 1, 1, 1, 1) };
            var r = Rec(Specs(), new BenchResults(new CpuBench(0, 0, double.NaN, double.PositiveInfinity, null), fg,
                                                  new GpuPowerTest("", 0, 0, 0, double.NaN, double.NaN, 0)));
            T.Check(!Get(r, "fg").Applicable, "no usable frame generation run");
            T.Check(!Has(r, "boost:cpu.power"), "no usable CPU run");
            T.Check(!Has(r, "gpu:power"), "no usable power test");
        });

        T.Test("ids are unique in every result", () =>
        {
            var r = Rec(Specs(hz: 60, maxHz: 144, configured: 2133, sticks: 1, laptop: true, battery: true, ramMB: 8192, eCores: 8,
                              gpus: new[] { Uhd, RtxLaptop }, win: Win with { GameMode = false, XboxDvr = true }),
                        Bench(cpu: new CpuBench(1000, 10000, 1.0, 10600, 1.0)));
            T.Equal(r.Count, r.Select(x => x.Id).Distinct().Count(), "unique ids");
        });
    }

    // ------------------------------------------------------------ over every produced recommendation
    private static void Invariants()
    {
        T.Test("every recommendation: consistent flags, says its source, no empty or unfilled text", () =>
        {
            T.Check(All.Count > 100, "scenarios ran: " + All.Count);
            foreach (var x in All)
            {
                string who = x.Id + " (" + x.Reason + ")";
                T.Check(x.Applicable || !x.SelectedByDefault, who + ": selected but not applicable");
                T.Check(x.Kind != RecKind.Tip || !x.Applicable, who + ": a tip is never applied");
                foreach (var s in new[] { x.Title, x.Current, x.Proposed, x.Reason })
                {
                    T.Check(!string.IsNullOrWhiteSpace(s), who + ": empty text");
                    T.Check(!Regex.IsMatch(s, @"\{\d+\}"), who + ": unfilled slot in " + s);
                }
                if (x.Applicable)
                    T.Check(x.Reason.Contains("측정") || x.Reason.Contains("사양 기준"), who + ": no source");
                if (x.Reason.StartsWith("사양 기준") && x.Kind == RecKind.Boost)
                    T.Check(x.Reason.Contains("효과는 게임마다 달라요"), who + ": an unmeasured effect must say it differs");
                if (x.Kind == RecKind.Gpu && x.Applicable) T.Check(!x.SelectedByDefault && x.PowerW > 0, who + ": GPU power is opt-in");
                if (x.Kind == RecKind.FrameGen && x.Applicable) T.Check(x.FrameGen != null, who + ": no choice");
                if (x.Kind == RecKind.Boost)
                {
                    T.Check(x.BoostFeatureId != null && x.Id == "boost:" + x.BoostFeatureId, who + ": id and feature");
                }
            }
        });

        T.Test("boost items are BoostCatalog ids that exist on Windows, with the catalog's titles", () =>
        {
            var path = FindUp(Path.Combine("app", "SystemBoost.App", "Services", "BoostCatalog.cs"))
                       ?? throw new CheckFailed("BoostCatalog.cs not found above " + AppContext.BaseDirectory);
            var catalog = new Dictionary<string, (string Title, bool MacOnly)>();
            foreach (var line in File.ReadAllLines(path))
            {
                var m = Regex.Match(line, @"new\(""([a-z.]+)"",\s*BoostCategory\.\w+,\s*""([^""]*)""");
                if (m.Success) catalog[m.Groups[1].Value] = (m.Groups[2].Value, line.Contains("MacOnly: true"));
            }
            T.Check(catalog.Count > 10, "catalog read");
            foreach (var x in All.Where(x => x.Kind == RecKind.Boost))
            {
                T.Check(catalog.TryGetValue(x.BoostFeatureId!, out var c), x.BoostFeatureId + " is not in BoostCatalog");
                T.Check(!c.MacOnly, x.BoostFeatureId + " is a Mac item");
                T.Equal(c.Title, x.Title, x.BoostFeatureId + " title");
            }
        });
    }

    // ------------------------------------------------------------ English
    private static void Translations()
    {
        var advisorPath = FindUp(Path.Combine("docs", "pc-check", "lang-advisor.json"));
        Dictionary<string, string>? advisor = null;
        T.Test("lang-advisor.json: UTF-8 without BOM, valid, English only, the same slots", () =>
        {
            if (advisorPath == null) throw new CheckFailed("docs/pc-check/lang-advisor.json not found above " + AppContext.BaseDirectory);
            var bytes = File.ReadAllBytes(advisorPath);
            T.Check(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "no BOM");
            advisor = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? throw new CheckFailed("empty");
            foreach (var (k, v) in advisor)
            {
                T.Check(LangMirror.HasKorean(k), "key without Korean: " + k);
                T.Check(!LangMirror.HasKorean(v) && v.Trim().Length > 0, "value not English: " + k);
                T.Equal(string.Join(",", Slots(k)), string.Join(",", Slots(v)), "slots of " + k);
            }
        });

        T.Test("every advisor text has its English (exact or a {0} template, slots too)", () =>
        {
            if (advisor == null) throw new CheckFailed("lang-advisor.json not loaded");
            var lang = new LangMirror(advisor);
            var missing = Texts().Where(s => lang.T(s) == null).Distinct().ToList();
            T.Check(missing.Count == 0, missing.Count + " texts without English: " + string.Join(" | ", missing.Take(5)));
        });

        T.Test("every K text with Korean is in lang-advisor.json", () =>
        {
            if (advisor == null) throw new CheckFailed("lang-advisor.json not loaded");
            var consts = typeof(PcAdvisor.K).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);
            var missing = consts.Where(LangMirror.HasKorean).Where(c => !advisor.ContainsKey(c)).ToList();
            T.Check(missing.Count == 0, "missing: " + string.Join(" | ", missing));
        });

        T.Test("merged with lang.en.json: same English, no entry of the app's catches an advisor text first", () =>
        {
            if (advisor == null) throw new CheckFailed("lang-advisor.json not loaded");
            var appPath = Path.Combine(AppContext.BaseDirectory, "lang.en.json");
            var app = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(appPath)) ?? new();
            foreach (var (k, v) in advisor)
                if (app.TryGetValue(k, out var other)) T.Equal(other, v, "lang.en.json already has " + k);
            var merged = new Dictionary<string, string>(app);
            foreach (var (k, v) in advisor) merged[k] = v;
            var alone = new LangMirror(advisor);
            var both = new LangMirror(merged);
            foreach (var s in Texts().Distinct()) T.Equal(alone.T(s), both.T(s), "merged translation of " + s);
        });
    }

    private static IEnumerable<string> Texts() => All.SelectMany(x => new[] { x.Title, x.Current, x.Proposed, x.Reason });

    private static IEnumerable<string> Slots(string s) => Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x);

    private static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, relative);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// Lang.cs's matching without its fallbacks (splitting at " · " and between sentences): a text counts as covered only
    /// when an exact entry or a {0} template takes all of it, and every slot that holds Korean is covered the same way.
    private sealed class LangMirror
    {
        private static readonly Regex Slot = new(@"\{(\d+)\}");
        private readonly Dictionary<string, string> _exact = new();
        private readonly List<(Regex Pattern, string Text)> _templates = new();

        public LangMirror(Dictionary<string, string> all)
        {
            foreach (var (key, value) in all.OrderByDescending(p => p.Key.Length))
            {
                if (!Slot.IsMatch(key)) { _exact[key] = value; continue; }
                var pattern = "^" + Slot.Replace(Regex.Escape(key).Replace(@"\{", "{"), "(?<s$1>.+?)") + "$";
                _templates.Add((new Regex(pattern, RegexOptions.Singleline), value));
            }
        }

        public static bool HasKorean(string s) => s.Any(c => c is >= '가' and <= '힣');

        public string? T(string text)
        {
            if (!HasKorean(text)) return text;
            var trimmed = text.Trim();
            if (_exact.TryGetValue(text, out var hit) || _exact.TryGetValue(trimmed, out hit)) return hit;
            foreach (var (pattern, value) in _templates)
            {
                var m = pattern.Match(trimmed);
                if (!m.Success) continue;
                bool ok = true;
                var done = Slot.Replace(value, s =>
                {
                    var inner = T(m.Groups["s" + s.Groups[1].Value].Value);
                    if (inner == null) ok = false;
                    return inner ?? "";
                });
                return ok ? done : null;   // Lang.cs takes the first template that matches, right or wrong
            }
            return null;
        }
    }

    // ------------------------------------------------------------ CPU benchmark
    private static void Benchmark()
    {
        T.Test("cpu: one work unit is deterministic", () =>
        {
            var table = CpuBenchmark.Table(1);
            T.Equal(CpuBenchmark.Unit(42, table), CpuBenchmark.Unit(42, CpuBenchmark.Table(1)), "same seed, same table");
            T.Check(CpuBenchmark.Unit(42, table) != CpuBenchmark.Unit(43, table), "another seed, another number");
        });

        T.Test("cpu: median and spread", () =>
        {
            T.Equal(2.0, CpuBenchmark.Median(new[] { 3.0, 1.0, 2.0 }), "odd count");
            T.Equal(2.5, CpuBenchmark.Median(new[] { 4.0, 1.0, 2.0, 3.0 }), "even count");
            T.Equal(20.0, CpuBenchmark.SpreadPercent(new[] { 90.0, 110.0, 100.0 }), "spread");
            T.Equal(0.0, CpuBenchmark.SpreadPercent(new[] { 100.0 }), "one run");
        });

        T.Test("cpu: a tiny run gives positive scores and no high-plan values", () =>
        {
            var b = CpuBenchmark.RunAsync(TimeSpan.FromMilliseconds(10), 2, CancellationToken.None).GetAwaiter().GetResult();
            T.Check(b.SingleScore > 0 && double.IsFinite(b.SingleScore), "single " + b.SingleScore);
            T.Check(b.MultiScore > 0 && double.IsFinite(b.MultiScore), "multi " + b.MultiScore);
            T.Check(b.NoisePercent >= 0 && double.IsFinite(b.NoisePercent), "noise " + b.NoisePercent);
            T.Check(b.HighPlanMultiScore == null && b.HighPlanNoisePercent == null, "high plan left to the runner");
            var (score, noise) = CpuBenchmark.MultiAsync(TimeSpan.FromMilliseconds(10), 1, CancellationToken.None).GetAwaiter().GetResult();
            T.Check(score > 0 && noise == 0, "multi alone, one run: no spread");
        });

        T.Test("cpu: cancelled before and during a run", () =>
        {
            using var before = new CancellationTokenSource();
            before.Cancel();
            T.Check(Throws<OperationCanceledException>(() => CpuBenchmark.RunAsync(TimeSpan.FromMilliseconds(10), 2, before.Token).GetAwaiter().GetResult()), "before");
            using var during = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            var started = DateTime.UtcNow;
            T.Check(Throws<OperationCanceledException>(() => CpuBenchmark.RunAsync(TimeSpan.FromSeconds(5), 3, during.Token).GetAwaiter().GetResult()), "during");
            T.Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "stops soon");
        });
    }

    private static bool Throws<TE>(Action a) where TE : Exception
    {
        try { a(); return false; }
        catch (TE) { return true; }
    }

    // ------------------------------------------------------------ spec parsers
    private static byte[] Core(byte efficiency, ulong mask)
    {
        var b = new byte[48];
        BitConverter.GetBytes(0).CopyTo(b, 0);    // RelationProcessorCore
        BitConverter.GetBytes(48).CopyTo(b, 4);
        b[8] = (byte)(System.Numerics.BitOperations.PopCount(mask) > 1 ? 1 : 0);
        b[9] = efficiency;
        BitConverter.GetBytes((ushort)1).CopyTo(b, 30);
        BitConverter.GetBytes(mask).CopyTo(b, 32);
        return b;
    }

    private static byte[] Cores(params (int Count, byte Efficiency, int Threads)[] kinds)
    {
        var all = new List<byte>();
        int bit = 0;
        var cache = new byte[32];   // a record of another kind (RelationCache) on the way
        BitConverter.GetBytes(2).CopyTo(cache, 0);
        BitConverter.GetBytes(32).CopyTo(cache, 4);
        all.AddRange(cache);
        foreach (var (count, eff, threads) in kinds)
            for (int i = 0; i < count; i++)
            {
                ulong mask = 0;
                for (int t = 0; t < threads; t++) mask |= 1UL << bit++;
                all.AddRange(Core(eff, mask));
            }
        return all.ToArray();
    }

    private static byte[] Smbios(params byte[][] structures)
    {
        var table = structures.SelectMany(s => s.Concat(new byte[] { 0, 0 })).Concat(new byte[] { 127, 4, 0xFF, 0xFF, 0, 0 }).ToArray();
        var raw = new byte[8 + table.Length];
        raw[1] = 3; raw[2] = 3;
        BitConverter.GetBytes((uint)table.Length).CopyTo(raw, 4);
        table.CopyTo(raw, 8);
        return raw;
    }

    private static byte[] Array16(ushort handle, byte use)
    {
        var s = new byte[0x17];
        s[0] = 16; s[1] = 0x17;
        BitConverter.GetBytes(handle).CopyTo(s, 2);
        s[4] = 0x03; s[5] = use;
        return s;
    }

    private static byte[] Device17(ushort array, int sizeMB, byte form, int speed, int configured, int extSizeMB = 0, int extSpeed = 0)
    {
        var s = new byte[0x5C];
        s[0] = 17; s[1] = 0x5C;
        BitConverter.GetBytes(array).CopyTo(s, 4);
        BitConverter.GetBytes((ushort)sizeMB).CopyTo(s, 0x0C);
        s[0x0E] = form;
        BitConverter.GetBytes((ushort)speed).CopyTo(s, 0x15);
        BitConverter.GetBytes((uint)extSizeMB).CopyTo(s, 0x1C);
        BitConverter.GetBytes((ushort)configured).CopyTo(s, 0x20);
        BitConverter.GetBytes((uint)extSpeed).CopyTo(s, 0x54);
        BitConverter.GetBytes((uint)extSpeed).CopyTo(s, 0x58);
        return s;
    }

    private static void Parsers()
    {
        T.Test("cores: hybrid 8 P + 16 E", () =>
        {
            var b = Cores((8, 1, 2), (16, 0, 1));
            T.Equal(new PcSpecParse.CpuCores(24, 32, 8, 16), PcSpecParse.ParseCores(b, b.Length), "counts");
        });

        T.Test("cores: not hybrid -> E 0, P = all", () =>
        {
            var b = Cores((8, 0, 2));
            T.Equal(new PcSpecParse.CpuCores(8, 16, 8, 0), PcSpecParse.ParseCores(b, b.Length), "counts");
        });

        T.Test("cores: three classes (low-power E-cores) -> P the top class, E the rest", () =>
        {
            var b = Cores((6, 2, 2), (8, 1, 1), (2, 0, 1));
            T.Equal(new PcSpecParse.CpuCores(16, 22, 6, 10), PcSpecParse.ParseCores(b, b.Length), "counts");
        });

        T.Test("cores: empty or cut short", () =>
        {
            T.Equal(new PcSpecParse.CpuCores(0, 0, 0, 0), PcSpecParse.ParseCores(Array.Empty<byte>(), 0), "empty");
            var b = Cores((4, 0, 2));
            T.Equal(3, PcSpecParse.ParseCores(b, b.Length - 10).Cores, "the cut record left out");
        });

        T.Test("smbios: two DDR4 sticks at 2133 of 3200, an empty slot", () =>
        {
            var raw = Smbios(Array16(0x1000, 3), Device17(0x1000, 16384, 0x09, 3200, 2133), Device17(0x1000, 16384, 0x09, 3200, 2133),
                             Device17(0x1000, 0, 0x02, 0, 0));
            T.Equal(new PcSpecParse.Memory(2133, 3200, 2, 32768), PcSpecParse.ParseSmbiosMemory(raw), "memory");
        });

        T.Test("smbios: soldered memory -> sticks unknown", () =>
            T.Equal(0, PcSpecParse.ParseSmbiosMemory(Smbios(Array16(1, 3), Device17(1, 8192, 0x0B, 6400, 6400))).Sticks, "sticks"));

        T.Test("smbios: running speed given as the clock (half) -> doubled", () =>
            T.Equal(3200, PcSpecParse.ParseSmbiosMemory(Smbios(Array16(1, 3), Device17(1, 8192, 0x0D, 3200, 1600))).ConfiguredMTs, "configured"));

        T.Test("smbios: extended size and extended speeds (SMBIOS 3.3)", () =>
        {
            var m = PcSpecParse.ParseSmbiosMemory(Smbios(Array16(1, 3), Device17(1, 0x7FFF, 0x09, 0xFFFF, 0xFFFF, extSizeMB: 65536, extSpeed: 8000)));
            T.Equal(new PcSpecParse.Memory(8000, 8000, 1, 65536), m, "memory");
        });

        T.Test("smbios: devices of a non-system memory array are left out; garbage gives zeros", () =>
        {
            var raw = Smbios(Array16(1, 3), Array16(2, 4), Device17(1, 8192, 0x09, 3200, 3200), Device17(2, 2048, 0x05, 1600, 1600));
            T.Equal(new PcSpecParse.Memory(3200, 3200, 1, 8192), PcSpecParse.ParseSmbiosMemory(raw), "system memory only");
            T.Equal(new PcSpecParse.Memory(0, 0, 0, 0), PcSpecParse.ParseSmbiosMemory(new byte[] { 1, 2, 3 }), "too short");
            T.Equal(new PcSpecParse.Memory(0, 0, 0, 0), PcSpecParse.ParseSmbiosMemory(new byte[] { 0, 3, 3, 0, 200, 0, 0, 0, 17, 2 }), "cut");
        });

        T.Test("gpu: vendor ids and the integrated guess", () =>
        {
            T.Equal("nvidia", PcSpecParse.VendorOf(0x10DE), "nvidia");
            T.Equal("amd", PcSpecParse.VendorOf(0x1002), "amd");
            T.Equal("intel", PcSpecParse.VendorOf(0x8086), "intel");
            T.Equal("other", PcSpecParse.VendorOf(0x1414), "other");
            T.Check(PcSpecParse.GuessIntegrated("intel", "Intel(R) UHD Graphics 770", 128), "UHD");
            T.Check(PcSpecParse.GuessIntegrated("intel", "Intel(R) Arc(TM) Graphics", 128), "Arc inside Core Ultra");
            T.Check(PcSpecParse.GuessIntegrated("intel", "Intel(R) Arc(TM) 140V GPU (16GB)", 128), "Lunar Lake");
            T.Check(!PcSpecParse.GuessIntegrated("intel", "Intel(R) Arc(TM) A770 Graphics", 16000), "Arc A770");
            T.Check(!PcSpecParse.GuessIntegrated("intel", "Intel(R) Arc(TM) B580 Graphics", 12000), "Arc B580");
            T.Check(PcSpecParse.GuessIntegrated("amd", "AMD Radeon(TM) Graphics", 2048), "APU with 2 GB set aside");
            T.Check(PcSpecParse.GuessIntegrated("amd", "AMD Radeon 780M Graphics", 512), "780M");
            T.Check(!PcSpecParse.GuessIntegrated("amd", "AMD Radeon RX 7800 XT", 16368), "RX 7800 XT");
            T.Check(!PcSpecParse.GuessIntegrated("nvidia", "NVIDIA GeForce RTX 4060 Laptop GPU", 8188), "NVIDIA");
            T.Check(PcSpecParse.GuessIntegrated("other", "Some GPU", 128), "other, small");
        });

        T.Test("gpu: matched to the engine's list by name", () =>
        {
            var mine = new[] { new GpuSpec("Intel(R) UHD Graphics 770", "intel", true, 128), new GpuSpec("NVIDIA GeForce RTX 4070", "nvidia", false, 12282) };
            var engine = new[] { ("0", "NVIDIA GeForce RTX 4070", false), ("1", "Intel(R) UHD Graphics 770", true) };
            var r = PcSpecParse.MatchEngine(mine, engine);
            T.Equal("1", r[0].EngineValue, "UHD");
            T.Equal("0", r[1].EngineValue, "RTX");
            T.Equal(12282L, r[1].VramMB, "memory kept");
            var amd = PcSpecParse.MatchEngine(new[] { new GpuSpec("AMD Radeon(TM) Graphics", "amd", true, 512) }, new[] { ("0", "AMD Radeon(TM) Graphics", false) });
            T.Check(!amd[0].Integrated, "the engine's word wins");
            var two = PcSpecParse.MatchEngine(new[] { Rtx with { EngineValue = "" }, Rtx with { EngineValue = "" } }, new[] { ("0", Rtx.Name, false), ("1", Rtx.Name, false) });
            T.Equal("0,1", string.Join(",", two.Select(g => g.EngineValue)), "two of a kind in order");
            T.Equal("", PcSpecParse.MatchEngine(mine, Array.Empty<(string, string, bool)>())[0].EngineValue, "no engine: unknown");
            var only = PcSpecParse.MatchEngine(Array.Empty<GpuSpec>(), engine);
            T.Equal("nvidia,intel", string.Join(",", only.Select(g => g.Vendor)), "the engine's list alone");
        });
    }
}
