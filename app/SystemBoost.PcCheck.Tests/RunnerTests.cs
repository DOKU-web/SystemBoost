// The runner part's checks: the sb_framegen bench protocol, the report file and the apply/undo record.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SystemBoost.App.Services.PcCheck;
using static SystemBoost.PcCheck.Tests.T;

namespace SystemBoost.PcCheck.Tests;

internal static class RunnerTests
{
    public static void Run()
    {
        Protocol();
        Arguments();
        Store();
        ApplyPlan();
        Undo();
        FrameGenChoice();
    }

    // ------------------------------------------------------------ protocol lines
    private static void Protocol()
    {
        Test("bench line", () =>
        {
            var l = FgBenchProtocol.Parse("{\"type\":\"bench\",\"quality\":\"balanced\",\"width\":2560,\"height\":1440,\"analyzeMs\":1.234,\"genMs\":0.456,\"gpu\":\"NVIDIA GeForce RTX 4070\"}");
            Equal(BenchLineType.Bench, l.Type, "type");
            Check(l.Result != null, "result");
            Equal(new FgBench("balanced", 2560, 1440, 1.234, 0.456, "NVIDIA GeForce RTX 4070"), l.Result!, "values");
        });
        Test("bench line without gpu, integer times", () =>
        {
            var l = FgBenchProtocol.Parse("{\"type\":\"bench\",\"quality\":\"low\",\"width\":1920,\"height\":1080,\"analyzeMs\":1,\"genMs\":0}");
            Equal(BenchLineType.Bench, l.Type, "type");
            Equal(1.0, l.Result!.AnalyzeMs, "analyzeMs");
            Equal("", l.Result.Gpu, "gpu");
        });
        Test("bench_load, bench_done, alive", () =>
        {
            var l = FgBenchProtocol.Parse("{\"type\":\"bench_load\",\"sec\":3,\"framesPerSec\":812.5,\"gpu\":\"X\"}");
            Equal(BenchLineType.BenchLoad, l.Type, "type");
            Equal(3, l.Sec, "sec");
            Equal(812.5, l.FramesPerSec, "fps");
            Equal(BenchLineType.Done, FgBenchProtocol.Parse("{\"type\":\"bench_done\"}").Type, "done");
            Equal(BenchLineType.Other, FgBenchProtocol.Parse("{\"type\":\"alive\"}").Type, "alive");
            Equal(BenchLineType.Done, FgBenchProtocol.Parse("\uFEFF{\"type\":\"bench_done\"}\r").Type, "BOM and CR");
        });
        Test("error lines (message and msg)", () =>
        {
            var a = FgBenchProtocol.Parse("{\"type\":\"error\",\"message\":\"no D3D11 device\"}");
            Equal(BenchLineType.Error, a.Type, "type");
            Equal("no D3D11 device", a.Message, "message");
            Equal("unknown option --bench", FgBenchProtocol.Parse("{\"type\":\"error\",\"msg\":\"unknown option --bench\"}").Message, "msg");
            Equal("", FgBenchProtocol.Parse("{\"type\":\"error\"}").Message, "no text");
        });
        Test("garbage is Bad, never a result", () =>
        {
            string?[] bad =
            {
                null, "", "   ", "hello", "[1,2]", "{", "{\"type\":", "{\"quality\":\"low\"}", "{\"type\":5}",
                "{\"type\":\"bench\"}",
                "{\"type\":\"bench\",\"quality\":\"ultra\",\"width\":1920,\"height\":1080,\"analyzeMs\":1,\"genMs\":1}",
                "{\"type\":\"bench\",\"quality\":\"low\",\"width\":0,\"height\":1080,\"analyzeMs\":1,\"genMs\":1}",
                "{\"type\":\"bench\",\"quality\":\"low\",\"width\":1920.5,\"height\":1080,\"analyzeMs\":1,\"genMs\":1}",
                "{\"type\":\"bench\",\"quality\":\"low\",\"width\":1920,\"height\":1080,\"analyzeMs\":-1,\"genMs\":1}",
                "{\"type\":\"bench\",\"quality\":\"low\",\"width\":1920,\"height\":1080,\"analyzeMs\":\"1.2\",\"genMs\":1}",
                "{\"type\":\"bench_load\",\"sec\":-1,\"framesPerSec\":10}",
                "{\"type\":\"bench_load\",\"sec\":1}",
                "{\"type\":\"bench_load\",\"sec\":1,\"framesPerSec\":1e400}",
                "{\"type\":\"bench\",\"quality\":\"low\",\"width\":1920,\"height\":1080,\"analyzeMs\":1,\"genMs\":1} trailing",
            };
            foreach (var line in bad) Equal(BenchLineType.Bad, FgBenchProtocol.Parse(line).Type, line ?? "null");
        });
        Test("numbers are read the same in a comma culture", () =>
        {
            var was = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CommaCulture();
                var l = FgBenchProtocol.Parse("{\"type\":\"bench\",\"quality\":\"quality\",\"width\":3840,\"height\":2160,\"analyzeMs\":12.5,\"genMs\":3.25}");
                Equal(12.5, l.Result!.AnalyzeMs, "analyzeMs");
                Equal(3.25, l.Result.GenMs, "genMs");
                // a decimal comma is not a JSON number: refused, not read as 1 or 15
                Equal(BenchLineType.Bad, FgBenchProtocol.Parse("{\"type\":\"bench_load\",\"sec\":1,\"framesPerSec\":1,5}").Type, "comma");
                Equal(BenchLineType.Bad, FgBenchProtocol.Parse("{\"type\":\"bench_load\",\"sec\":1,\"framesPerSec\":\"1,5\"}").Type, "comma text");
            }
            finally { CultureInfo.CurrentCulture = was; }
        });
        Test("collector: first per quality, ordered, errors, load seconds", () =>
        {
            var c = new BenchCollector();
            foreach (var (q, ms) in new[] { ("quality", 4), ("low", 1), ("balanced", 2), ("low", 9), ("performance", 1) })
                c.Add(FgBenchProtocol.Parse($"{{\"type\":\"bench\",\"quality\":\"{q}\",\"width\":1920,\"height\":1080,\"analyzeMs\":{ms},\"genMs\":0.5,\"gpu\":\"G\"}}"));
            c.Add(FgBenchProtocol.Parse("garbage"));
            c.Add(FgBenchProtocol.Parse("{\"type\":\"bench_done\"}"));
            Equal("low,performance,balanced,quality", string.Join(",", c.Ordered().Select(r => r.Quality)), "order");
            Equal(1.0, c.Ordered()[0].AnalyzeMs, "first low kept");
            Check(c.Done, "done");
            Equal(1, c.BadLines, "bad lines");
            Equal("G", c.Gpu, "gpu");
            Check(c.Error == null, "no error");
            var e = new BenchCollector();
            e.Add(FgBenchProtocol.Parse("{\"type\":\"error\",\"message\":\"first\"}"));
            e.Add(FgBenchProtocol.Parse("{\"type\":\"error\",\"message\":\"second\"}"));
            Equal("first", e.Error, "first error kept");
            var load = new BenchCollector();
            foreach (var fps in new[] { 100, 700, 800, 810, 790 })
                load.Add(FgBenchProtocol.Parse($"{{\"type\":\"bench_load\",\"sec\":1,\"framesPerSec\":{fps}}}"));
            Equal(5, load.LoadFps.Count, "seconds");
            Equal(800.0, FgBenchProtocol.LoadFps(load.LoadFps, 2), "median after warm-up");
            Equal(795.0, FgBenchProtocol.LoadFps(new[] { 1.0, 790, 800 }, 1), "even count");
            Check(FgBenchProtocol.LoadFps(new[] { 1.0, 2.0 }, 2) == null, "nothing after warm-up");
            Check(FgBenchProtocol.LoadFps(new[] { 1.0, 0.0, double.NaN }, 1) == null, "no usable seconds");
        });
    }

    // ------------------------------------------------------------ arguments
    private static void Arguments()
    {
        Test("--bench arguments", () =>
        {
            var a = FgBenchProtocol.BenchArgs(2560, 1440, "same", 4242);
            Equal("--bench --bench-width 2560 --bench-height 1440 --gen-gpu same --parent-pid 4242", string.Join(" ", a), "args");
            var d = FgBenchProtocol.BenchArgs(1920, 1080, "1", 0, @"C:\Program Files\SystemBoost\shaders");
            Equal("--bench|--bench-width|1920|--bench-height|1080|--gen-gpu|1|--shader-dir|C:\\Program Files\\SystemBoost\\shaders",
                  string.Join("|", d), "index, no parent, shader dir as one argument");
        });
        Test("--bench-load arguments, seconds kept in 1..60", () =>
        {
            Equal("--bench-load 8 --bench-width 1920 --bench-height 1080 --gen-gpu 0 --parent-pid 7",
                  string.Join(" ", FgBenchProtocol.LoadArgs(8, 1920, 1080, "0", 7)), "args");
            Equal("60", FgBenchProtocol.LoadArgs(600, 1920, 1080, null, 0)[1], "max");
            Equal("1", FgBenchProtocol.LoadArgs(0, 1920, 1080, null, 0)[1], "min");
        });
        Test("--gen-gpu takes same or an index only", () =>
        {
            Equal("same", FgBenchProtocol.GenGpu(null), "null");
            Equal("same", FgBenchProtocol.GenGpu(""), "empty");
            Equal("same", FgBenchProtocol.GenGpu("igpu"), "igpu is no bench value");
            Equal("same", FgBenchProtocol.GenGpu("1 --upscale"), "no extra options");
            Equal("same", FgBenchProtocol.GenGpu("-1"), "negative");
            Equal("12", FgBenchProtocol.GenGpu("12"), "index");
        });
        Test("bad sizes are refused", () =>
        {
            foreach (var (w, h) in new[] { (0, 1080), (1920, 0), (-1, 1), (20000, 1080) })
            {
                bool threw = false;
                try { FgBenchProtocol.BenchArgs(w, h, "same", 1); }
                catch (ArgumentOutOfRangeException) { threw = true; }
                Check(threw, $"{w}x{h}");
            }
        });
        Test("arguments are invariant in a comma culture", () =>
        {
            var was = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CommaCulture();
                Equal("--bench --bench-width 2560 --bench-height 1440 --gen-gpu same --parent-pid 123456",
                      string.Join(" ", FgBenchProtocol.BenchArgs(2560, 1440, "same", 123456)), "args");
            }
            finally { CultureInfo.CurrentCulture = was; }
        });
    }

    // ------------------------------------------------------------ the files
    private static void Store()
    {
        Test("report round trip", () => InTemp(dir =>
        {
            var path = Path.Combine(dir, PcCheckStore.ReportFile);
            Check(PcCheckStore.LoadReport(path) == null, "missing file: null");
            var report = SampleReport();
            var was = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CommaCulture();
                PcCheckStore.SaveReport(path, report);
            }
            finally { CultureInfo.CurrentCulture = was; }
            var text = File.ReadAllText(path);
            Check(text.Contains("1.234") && !text.Contains("1,234"), "invariant numbers in the file");
            Check(text.Contains("\"Kind\": \"Gpu\"") && text.Contains("\"Kind\": \"Tip\""), "enum names as text");
            Check(!File.Exists(path + ".tmp"), "no temp file left");
            var read = PcCheckStore.LoadReport(path);
            Check(read != null, "read back");
            var back = read!;
            Equal(report.At, back.At, "at");
            Equal(report.AppVersion, back.AppVersion, "version");
            Equal(report.Specs.CpuName, back.Specs.CpuName, "cpu");
            Equal(report.Specs.RamRatedMTs, back.Specs.RamRatedMTs, "ram");
            Equal(report.Specs.Display, back.Specs.Display, "display");
            Equal(report.Specs.Windows, back.Specs.Windows, "windows");
            Equal(report.Specs.Gpus.Count, back.Specs.Gpus.Count, "gpus");
            Equal(report.Specs.Gpus[1], back.Specs.Gpus[1], "gpu");
            Equal(report.Bench.Cpu, back.Bench.Cpu, "cpu bench");
            Equal(report.Bench.Power, back.Bench.Power, "power test");
            Equal(report.Bench.FrameGen.Count, back.Bench.FrameGen.Count, "fg bench count");
            Equal(report.Bench.FrameGen[1], back.Bench.FrameGen[1], "fg bench");
            Equal(report.Bench.FgError, back.Bench.FgError, "fg error");
            Check(back.Bench.CpuError == null && back.Bench.PowerError == null, "no other errors");
            Equal(report.Recommendations.Count, back.Recommendations.Count, "recommendations");
            for (int i = 0; i < report.Recommendations.Count; i++)
                Equal(report.Recommendations[i], back.Recommendations[i], $"recommendation {i}");
        }));
        Test("unreadable report reads as null", () => InTemp(dir =>
        {
            var path = Path.Combine(dir, PcCheckStore.ReportFile);
            File.WriteAllText(path, "{ not json");
            Check(PcCheckStore.LoadReport(path) == null, "broken");
            File.WriteAllText(path, "null");
            Check(PcCheckStore.LoadReport(path) == null, "null");
            File.WriteAllText(path, "{}");
            Check(PcCheckStore.LoadReport(path) == null, "empty object");
        }));
        Test("applied record round trip; empty removes the file", () => InTemp(dir =>
        {
            var path = Path.Combine(dir, "data", PcCheckStore.AppliedFile);
            var rec = new PcApplyRecord(new DateTime(2026, 11, 18, 3, 0, 0), new[] { "sys.gamemode", "cpu.power" },
                new FgUndo(new FgChoice("x2", 2, "balanced"), null, new FgChoice("auto", 8, "quality")),
                new GpuUndo(new GpuValues(0, 15, -30), 320));
            PcCheckStore.SaveApplied(path, rec);
            var back = PcCheckStore.LoadApplied(path)!;
            Equal("sys.gamemode,cpu.power", string.Join(",", back.Boosts), "boosts");
            Equal(rec.FrameGen, back.FrameGen, "frame generation");
            Equal(rec.Gpu, back.Gpu, "gpu");
            Check(!back.IsEmpty, "not empty");
            PcCheckStore.SaveApplied(path, new PcApplyRecord(DateTime.Now, Array.Empty<string>()));
            Check(!File.Exists(path), "empty record: no file");
            Check(PcCheckStore.LoadApplied(path) == null, "nothing to undo");
        }));
        Test("frame generation choice file", () => InTemp(dir =>
        {
            var path = Path.Combine(dir, PcCheckStore.FgChoiceFile);
            Check(PcCheckStore.LoadFgChoice(path) == null, "none");
            PcCheckStore.SaveFgChoice(path, new FgChoice("auto", 6, "performance"));
            Equal(new FgChoice("auto", 6, "performance"), PcCheckStore.LoadFgChoice(path), "read back");
            File.WriteAllText(path, "{\"MaxMul\":4}");
            Check(PcCheckStore.LoadFgChoice(path) == null, "incomplete");
            PcCheckStore.Delete(path);
            PcCheckStore.Delete(path);   // twice: nothing to delete is fine
            Check(!File.Exists(path), "deleted");
        }));
    }

    // ------------------------------------------------------------ apply plan and record
    private static readonly FgAllowance Ultra = new(true, true, true, "auto", 4);
    private static readonly DateTime Now = new(2026, 11, 18, 4, 0, 0);

    private static Recommendation Boost(string id, bool applicable = true) =>
        new("boost:" + id, RecKind.Boost, id, "꺼짐", "켜짐", "사양 기준", applicable, true, BoostFeatureId: id);
    private static Recommendation Fg(FgChoice c) => new("fg", RecKind.FrameGen, "프레임 생성", "", "", "측정", true, true, FrameGen: c);
    private static Recommendation Gpu(int w) => new("gpu.power", RecKind.Gpu, "GPU 전력 제한", "", "", "측정", true, false, PowerW: w);
    private static Recommendation Tip() => new("tip.refresh", RecKind.Tip, "주사율", "", "", "", false, false);

    private static PcApplyState State(IEnumerable<string>? on = null, bool tuned = false, bool consent = true, string? problem = null,
                                      FgChoice? fgNow = null, FgChoice? saved = null, GpuValues? gpu = null) =>
        new((on ?? Array.Empty<string>()).ToList(), fgNow ?? new FgChoice("x2", 2, "balanced"), saved, tuned,
            gpu ?? new GpuValues(0, 0, 0), problem, consent);

    private static void ApplyPlan()
    {
        Test("already-on boost items are skipped, off ones become steps", () =>
        {
            var plan = PcApplyPlanner.Plan(new[] { Boost("sys.gamemode"), Boost("sys.dvr"), Boost("cpu.power") },
                                           State(on: new[] { "sys.dvr" }), null, Now, Ultra);
            Equal("sys.gamemode,cpu.power", string.Join(",", plan.Steps.Select(s => s.BoostId)), "steps");
            Equal(1, plan.Skipped.Count, "skipped");
            Equal("boost:sys.dvr", plan.Skipped[0].Id, "skipped id");
            Check(plan.Skipped[0].Ok, "already on is not a failure");
            Equal(PcApplyPlanner.AlreadyOn, plan.Skipped[0].Text, "text");
            Equal("sys.gamemode,cpu.power", string.Join(",", plan.Record.Boosts), "record: only what was off");
            Equal("boost:sys.gamemode,boost:sys.dvr,boost:cpu.power", string.Join(",", plan.Order), "results in page order");
        });
        Test("tips and duplicates are not applied", () =>
        {
            var plan = PcApplyPlanner.Plan(new[] { Tip(), Boost("sys.dvr"), Boost("sys.dvr"), Boost("ram.standby", applicable: false) },
                                           State(), null, Now, Ultra);
            Equal(1, plan.Steps.Count, "one step");
            Equal(0, plan.Skipped.Count, "nothing skipped");
            Equal("boost:sys.dvr", string.Join(",", plan.Order), "order");
        });
        Test("GPU without consent is skipped and not recorded", () =>
        {
            var plan = PcApplyPlanner.Plan(new[] { Gpu(320), Boost("sys.dvr") }, State(consent: false), null, Now, Ultra);
            Check(plan.Steps.All(s => s.Kind != ApplyStepKind.Gpu), "no GPU step");
            var r = plan.Skipped.Single(s => s.Id == "gpu.power");
            Check(!r.Ok, "a GPU item not applied is not ok");
            Equal(PcApplyPlanner.GpuNoConsent, r.Text, "text");
            Check(plan.Record.Gpu == null, "nothing to put back");
            Equal(1, plan.Steps.Count, "the rest is applied");
        });
        Test("GPU while gpu.oc is on, or with a problem, is skipped", () =>
        {
            var tuned = PcApplyPlanner.Plan(new[] { Gpu(320) }, State(tuned: true), null, Now, Ultra);
            Equal(0, tuned.Steps.Count, "tuned: no step");
            Equal(PcApplyPlanner.GpuAlreadyTuned, tuned.Skipped[0].Text, "tuned text");
            var problem = PcApplyPlanner.Plan(new[] { Gpu(320) }, State(problem: "카드가 바뀌었어요"), null, Now, Ultra);
            Equal("카드가 바뀌었어요", problem.Skipped[0].Text, "problem text");
            var zero = PcApplyPlanner.Plan(new[] { Gpu(0) }, State(), null, Now, Ultra);
            Equal(PcApplyPlanner.NothingToApply, zero.Skipped[0].Text, "no watts");
            Check(tuned.Record.IsEmpty && problem.Record.IsEmpty, "no record");
        });
        Test("steps run boosts, then frame generation, then the GPU last", () =>
        {
            var plan = PcApplyPlanner.Plan(new[] { Gpu(320), Fg(new FgChoice("auto", 8, "quality")), Boost("sys.gamemode") },
                                           State(gpu: new GpuValues(250, 50, 100)), null, Now, Ultra);
            Equal("Boost,FrameGen,Gpu", string.Join(",", plan.Steps.Select(s => s.Kind)), "order");
            Equal(320, plan.Steps[2].PowerW, "watts");
            Equal(new GpuUndo(new GpuValues(250, 50, 100), 320), plan.Record.Gpu, "GPU values before");
            Equal("gpu.power,fg,boost:sys.gamemode", string.Join(",", plan.Order), "results in page order");
        });
        Test("frame generation is fitted to the plan; the record keeps what was there", () =>
        {
            var pro = new FgAllowance(true, false, false, "auto", 4);
            var saved = new FgChoice("x3", 3, "performance");
            var plan = PcApplyPlanner.Plan(new[] { Fg(new FgChoice("auto", 8, "quality")) },
                                           State(fgNow: new FgChoice("x4", 4, "low"), saved: saved), null, Now, pro);
            Equal(new FgChoice("auto", 4, "balanced"), plan.Steps[0].FrameGen, "fitted");
            Equal(new FgUndo(new FgChoice("x4", 4, "low"), saved, new FgChoice("auto", 4, "balanced")), plan.Record.FrameGen, "undo record");
        });
        Test("a second apply keeps the earliest before", () =>
        {
            var first = PcApplyPlanner.Plan(new[] { Boost("sys.gamemode"), Fg(new FgChoice("auto", 8, "quality")), Gpu(320) },
                                            State(gpu: new GpuValues(0, 0, 0)), null, Now, Ultra).Record;
            var second = PcApplyPlanner.Plan(new[] { Boost("cpu.power"), Fg(new FgChoice("auto", 6, "balanced")), Gpu(310) },
                                             State(on: new[] { "sys.gamemode" }, fgNow: new FgChoice("auto", 8, "quality"),
                                                   gpu: new GpuValues(320, 0, 0)), first, Now.AddMinutes(5), Ultra).Record;
            Equal("sys.gamemode,cpu.power", string.Join(",", second.Boosts), "boosts of both");
            Equal(new FgChoice("x2", 2, "balanced"), second.FrameGen!.Before, "frame generation before the first");
            Equal(new FgChoice("auto", 6, "balanced"), second.FrameGen.Applied, "applied: the latest");
            Equal(new GpuValues(0, 0, 0), second.Gpu!.Before, "GPU before the first");
            Equal(310, second.Gpu.AppliedW, "GPU applied: the latest");
            var nothing = PcApplyPlanner.Plan(new[] { Boost("cpu.power") }, State(on: new[] { "cpu.power" }), second, Now, Ultra);
            Equal(0, nothing.Steps.Count, "no steps");
            Equal(second, nothing.Record, "the record stays as it was");
        });
        Test("settle: items that did not come on, a failed GPU step", () =>
        {
            var plan = PcApplyPlanner.Plan(new[] { Boost("sys.gamemode"), Boost("gpu.highperf"), Gpu(320) }, State(), null, Now, Ultra);
            var settled = PcApplyPlanner.Settle(plan.Record, null, new[] { "sys.gamemode" }, gpuFailed: true);
            Equal("sys.gamemode", string.Join(",", settled.Boosts), "only what came on");
            Check(settled.Gpu == null, "failed GPU: nothing to put back");
            var ok = PcApplyPlanner.Settle(plan.Record, null, new[] { "sys.gamemode", "gpu.highperf" }, gpuFailed: false);
            Equal(plan.Record.Gpu, ok.Gpu, "GPU kept");
            var none = PcApplyPlanner.Settle(plan.Record, null, Array.Empty<string>(), gpuFailed: true);
            Check(none.IsEmpty, "nothing came on: empty");
        });
    }

    // ------------------------------------------------------------ undo
    private static void Undo()
    {
        var before = new FgChoice("x2", 2, "balanced");
        var saved = new FgChoice("x3", 3, "performance");
        var applied = new FgChoice("auto", 8, "quality");
        var record = new PcApplyRecord(Now, new[] { "sys.gamemode", "sys.dvr", "cpu.power" },
            new FgUndo(before, saved, applied), new GpuUndo(new GpuValues(250, 50, 100), 320));

        Test("undo puts back the frame generation choice and the GPU values", () =>
        {
            var plan = PcApplyPlanner.Undo(record, new PcUndoState(new[] { "sys.gamemode", "sys.dvr", "cpu.power" }, applied, true, new GpuValues(320, 0, 0)));
            Equal("cpu.power,sys.dvr,sys.gamemode", string.Join(",", plan.RevertBoosts), "newest first");
            Equal(before, plan.FrameGen!.Before, "frame generation values before");
            Equal(saved, plan.FrameGen.BeforeSaved, "saved choice before");
            Equal(new GpuValues(250, 50, 100), plan.Gpu!.Before, "GPU page values before");
            Check(plan.TurnGpuOff, "gpu.oc goes off first");
            Equal(0, plan.Kept.Count, "nothing kept");
        });
        Test("apply then undo on a fake PC gives back the state before", () =>
        {
            // before: game mode off, DVR off already (its item is on), frame generation x2, GPU page 250/50/100, gpu.oc off
            var on = new HashSet<string> { "sys.dvr" };
            FgChoice fgNow = before;
            FgChoice? fgSaved = saved;
            var gpu = new GpuValues(250, 50, 100);
            bool tuned = false;
            var chosen = new[] { Boost("sys.gamemode"), Boost("sys.dvr"), Fg(applied), Gpu(320) };
            var plan = PcApplyPlanner.Plan(chosen, State(on: on, fgNow: fgNow, saved: fgSaved, gpu: gpu), null, Now, Ultra);
            foreach (var s in plan.Steps)   // what the runner does, on the fake
            {
                if (s.Kind == ApplyStepKind.Boost) on.Add(s.BoostId!);
                if (s.Kind == ApplyStepKind.FrameGen) { fgNow = s.FrameGen!; fgSaved = fgNow; }
                if (s.Kind == ApplyStepKind.Gpu) { gpu = new GpuValues(s.PowerW, 0, 0); tuned = true; }
            }
            var rec = PcApplyPlanner.Settle(plan.Record, null, on, gpuFailed: false);
            var undo = PcApplyPlanner.Undo(rec, new PcUndoState(on, fgNow, tuned, gpu));
            if (undo.TurnGpuOff) tuned = false;
            if (undo.Gpu is { } g) gpu = g.Before;
            if (undo.FrameGen is { } f) { fgNow = f.Before; fgSaved = f.BeforeSaved; }
            foreach (var id in undo.RevertBoosts) on.Remove(id);
            Equal("sys.dvr", string.Join(",", on), "boosts: only what was on before");
            Equal(before, fgNow, "frame generation values");
            Equal(saved, fgSaved, "saved choice");
            Equal(new GpuValues(250, 50, 100), gpu, "GPU page values");
            Check(!tuned, "gpu.oc off");
            Check(PcApplyPlanner.AfterUndo(rec, undo, on, tuned, Now).IsEmpty, "nothing left on record");
        });
        Test("values changed since stay; items already off are not touched", () =>
        {
            var plan = PcApplyPlanner.Undo(record, new PcUndoState(new[] { "sys.dvr" }, new FgChoice("x4", 4, "low"), true, new GpuValues(300, 0, 0)));
            Equal("sys.dvr", string.Join(",", plan.RevertBoosts), "only what is still on");
            Check(plan.FrameGen == null, "frame generation changed since: stays");
            Check(plan.Gpu == null && !plan.TurnGpuOff, "GPU values changed since: the user's tuning stays");
            Equal(2, plan.Kept.Count, "both said");
            Check(plan.Kept.Contains(PcApplyPlanner.FgKept) && plan.Kept.Contains(PcApplyPlanner.GpuKept), "kept lines");
        });
        Test("after undo only what did not go back stays on record", () =>
        {
            var plan = PcApplyPlanner.Undo(record, new PcUndoState(new[] { "sys.gamemode", "cpu.power" }, applied, true, new GpuValues(320, 0, 0)));
            var left = PcApplyPlanner.AfterUndo(record, plan, new[] { "cpu.power" }, gpuStillTuned: true, Now);
            Equal("cpu.power", string.Join(",", left.Boosts), "the boost that stayed on");
            Equal(record.Gpu, left.Gpu, "gpu.oc still on: kept");
            Check(left.FrameGen == null, "frame generation done");
            var clean = PcApplyPlanner.AfterUndo(record, plan, Array.Empty<string>(), gpuStillTuned: false, Now);
            Check(clean.IsEmpty, "all back: empty");
            var untuned = PcApplyPlanner.Undo(record, new PcUndoState(Array.Empty<string>(), applied, false, new GpuValues(320, 0, 0)));
            Check(untuned.Gpu != null && !untuned.TurnGpuOff, "the helper put the card back already: only the page values");
        });
    }

    // ------------------------------------------------------------ the FrameGen page's choice
    private static void FrameGenChoice()
    {
        Test("the choice is fitted to what the plan opens", () =>
        {
            var free = new FgAllowance(false, false, false, "x2", 2);
            var pro = new FgAllowance(true, false, false, "auto", 4);
            Equal(new FgChoice("auto", 8, "quality"), FgChoiceRules.Fit(new FgChoice("auto", 8, "quality"), Ultra), "ultra: all");
            Equal(new FgChoice("auto", 4, "balanced"), FgChoiceRules.Fit(new FgChoice("auto", 8, "quality"), pro), "pro");
            Equal(new FgChoice("x4", 4, "low"), FgChoiceRules.Fit(new FgChoice("x8", 6, "low"), pro), "pro: 8x to 4x");
            Equal(new FgChoice("x2", 2, "balanced"), FgChoiceRules.Fit(new FgChoice("auto", 8, "quality"), free), "free");
            Equal(new FgChoice("off", 2, "performance"), FgChoiceRules.Fit(new FgChoice("off", 3, "performance"), free), "off stays");
            Equal(new FgChoice("auto", 4, "balanced"), FgChoiceRules.Fit(new FgChoice("x5", 5, "best"), Ultra), "unknown: defaults");
            Check(FgChoiceRules.Same(new FgChoice("x2", 2, "low"), new FgChoice("x2", 2, "low")), "same");
            Check(!FgChoiceRules.Same(null, new FgChoice("x2", 2, "low")), "null");
        });
    }

    // ------------------------------------------------------------ helpers
    private static CultureInfo CommaCulture()
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        c.NumberFormat.NumberDecimalSeparator = ",";
        c.NumberFormat.NumberGroupSeparator = ".";
        return c;
    }

    // a folder of its own under the test's output folder, gone afterwards
    private static void InTemp(Action<string> body)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    private static PcCheckReport SampleReport()
    {
        var specs = new PcSpecs("Intel Core i7-14700K", 20, 28, 8, 12, 32768, 4800, 6000, 2,
            new[]
            {
                new GpuSpec("Intel UHD Graphics 770", "intel", true, 512, "1"),
                new GpuSpec("NVIDIA GeForce RTX 4070", "nvidia", false, 12282, "0"),
            },
            new DisplaySpec(2560, 1440, 143.97, 165), false, false,
            new WindowsState(true, true, false, null, false, true));
        var bench = new BenchResults(
            new CpuBench(1520.5, 18123.25, 1.2, 18900.0, 0.9),
            new[] { new FgBench("low", 2560, 1440, 0.61, 0.2, "NVIDIA GeForce RTX 4070"), new FgBench("balanced", 2560, 1440, 1.234, 0.456) },
            new GpuPowerTest("NVIDIA GeForce RTX 4070", 200, 220, 812.5, 845.25, 0.8, 71),
            FgError: "프레임 생성 벤치마크를 하지 못했어요: x");
        var recs = new List<Recommendation>
        {
            new("tip.refresh", RecKind.Tip, "주사율", "143.97Hz", "165Hz", "사양 기준", false, false),
            new("fg", RecKind.FrameGen, "프레임 생성", "2배", "자동 · 최대 4배", "측정: 1.234 ms", true, true, FrameGen: new FgChoice("auto", 4, "balanced")),
            new("boost.sys.gamemode", RecKind.Boost, "Windows 게임 모드", "꺼짐", "켜짐", "사양 기준", true, true, BoostFeatureId: "sys.gamemode"),
            new("gpu.power", RecKind.Gpu, "GPU 전력 제한", "200 W", "220 W", "측정: 812.5 → 845.3", true, false, PowerW: 220),
        };
        return new PcCheckReport(new DateTime(2026, 11, 18, 3, 4, 5, DateTimeKind.Local), "0.8.19", specs, bench, recs);
    }
}
