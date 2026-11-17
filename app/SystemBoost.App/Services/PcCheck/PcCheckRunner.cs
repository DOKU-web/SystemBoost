// PC 맞춤 추천: runs the check (specs, CPU benchmark, frame generation benchmark, optional power-limit test), keeps the
// report, applies the chosen recommendations at once and puts them back. Owner: the runner part (CONTRACT.md).
//   Check   PcSpecReader -> CpuBenchmark (and its all-thread run again on the high-performance plan, switched through
//           the backend's cpu.power code and always back in finally) -> sb_framegen --bench at the display's resolution
//           -> the power-limit test when asked (desktop NVIDIA, after the overclocking notice; WindowsBoostBackend.PcCheck.cs)
//           -> PcAdvisor -> data\pc-check.json. Cancel puts everything back the same way (the finally blocks).
//   Engine  started like FrameGenEngine starts it (no window, stdout as UTF-8, --parent-pid), with a time limit; one that
//           hangs is ended by its own process handle, never by name.
//   Apply   PcApplyPlanner decides the steps; data\pc-check-applied.json is written before the first one; boost items
//           through backend.ApplyAsync, frame generation through FrameGenViewModel.ApplyChoice, the power limit through
//           SetGpuChoice + ApplyAsync("gpu.oc") (the helper's '유지' countdown and auto-restore as on the GPU page).
//   Undo    PcApplyPlanner.Undo: what was turned on here goes off, values go back while they are still the ones set here.
// Nothing leaves this PC.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using SystemBoost.App.ViewModels;

namespace SystemBoost.App.Services.PcCheck;

[SupportedOSPlatform("windows")]
public sealed class PcCheckRunner
{
    // CONTRACT.md: about 2 s, 3 times, per side
    private static readonly TimeSpan CpuEach = TimeSpan.FromSeconds(2);
    private const int CpuRepeats = 3;
    private const int BenchWaitSeconds = FgBenchProtocol.BenchSeconds + 25;   // its 15 s and the start-up (devices, shaders)
    private const int LoadWaitExtraSeconds = 20;

    public const string NotUltra = "PC 맞춤 추천은 울트라 기능이에요. 왼쪽 '★ 플랜'에서 열 수 있어요.";
    public const string GameRunning = "게임을 끈 뒤에 검사해 주세요";
    public const string AlreadyRunning = "이미 검사하고 있어요";
    public const string Busy = "추천을 적용하거나 되돌리는 중이에요. 끝난 뒤에 다시 눌러 주세요";
    /// The line under the overclocking notice's headline when a recommended power limit is about to be applied.
    public const string RecommendationReason = "PC 맞춤 추천의 전력 제한 값을 적용하기 전에 다시 동의를 받아요.";

    private readonly WindowsBoostBackend _backend;
    private readonly FrameGenViewModel _fg;
    private volatile bool _running, _applying;

    public PcCheckRunner(WindowsBoostBackend backend, FrameGenViewModel fg)
    {
        _backend = backend;
        _fg = fg;
    }

    public bool Running => _running;

    private static string ReportPath => Path.Combine(Platform.DataDir, PcCheckStore.ReportFile);
    private static string AppliedPath => Path.Combine(Platform.DataDir, PcCheckStore.AppliedFile);

    /// Null when the check may start, otherwise the line to show (plan, a game is running, already running).
    public string? CannotStart()
    {
        if (!License.Allowed(PremiumFeature.PcCheck)) return NotUltra;
        if (_running) return AlreadyRunning;
        if (_applying) return Busy;
        if (_fg.Running || _backend.GameRunningNow) return GameRunning;
        return null;
    }

    /// About a minute. testPower: also the power-limit test (desktop NVIDIA only; asks the overclocking notice and the
    /// administrator prompt first). Saved (data\pc-check.json) before it returns.
    public async Task<PcCheckReport> RunAsync(Window owner, bool testPower, Action<string> progress, CancellationToken ct)
    {
        if (CannotStart() is { } why) throw new InvalidOperationException(why);
        _running = true;
        var say = OnUi(progress);
        try
        {
            say("PC 사양을 읽는 중…");
            var specs = await PcSpecReader.ReadAsync();
            ct.ThrowIfCancellationRequested();

            var (cpu, cpuError) = await CpuAsync(say, ct);

            IReadOnlyList<FgBench> fg = Array.Empty<FgBench>();
            string? fgError;
            if (_backend.GameRunningNow) fgError = GameStarted;
            else
            {
                say("프레임 생성이 GPU를 얼마나 쓰는지 재는 중… (15초 안쪽)");
                (fg, fgError) = await FrameGenBenchAsync(specs, ct);
            }

            GpuPowerTest? power = null;
            string? powerError = null;
            if (testPower)
            {
                if (_backend.GameRunningNow) powerError = GameStarted;
                else (power, powerError) = await PowerTestAsync(owner, specs, say, ct);
            }

            ct.ThrowIfCancellationRequested();
            var bench = new BenchResults(cpu, fg, power, cpuError, fgError, powerError);
            var recommendations = PcAdvisor.Recommend(specs, bench, await NowAsync());
            var report = new PcCheckReport(DateTime.Now, OcConsent.AppVersion, specs, bench, recommendations);
            try { PcCheckStore.SaveReport(ReportPath, report); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { say(string.Format(NotSaved, x.Message)); }
            SetLast(report);
            say("검사를 마쳤어요");
            return report;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            say("검사를 멈췄어요. 검사 중에 바꾼 것은 모두 원래대로 돌렸어요");
            throw;
        }
        finally { _running = false; }
    }

    private const string GameStarted = "검사 중에 게임이 켜져서 GPU 측정은 건너뛰었어요";
    private const string NotSaved = "검사 결과를 저장하지 못했어요: {0}";

    // ------------------------------------------------------------ CPU
    private async Task<(CpuBench? Cpu, string? Error)> CpuAsync(Action<string> say, CancellationToken ct)
    {
        say("CPU 성능을 재는 중… (약 12초)");
        CpuBench cpu;
        try { cpu = await Task.Run(() => CpuBenchmark.RunAsync(CpuEach, CpuRepeats, ct), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception x) { return (null, string.Format(CpuFailed, x.Message)); }
        if (!_backend.HighPlanTestable) return (cpu, null);

        say("고성능 전원 계획으로 바꿔서 CPU를 다시 재는 중… (끝나면 원래 계획으로 돌려요)");
        try
        {
            var (high, error) = await _backend.OnHighPerformancePlanAsync(
                () => Task.Run(() => CpuBenchmark.MultiAsync(CpuEach, CpuRepeats, ct), ct), ct);
            if (error == null) cpu = cpu with { HighPlanMultiScore = high.Score, HighPlanNoisePercent = high.NoisePercent };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }   // the plan is back (finally); the comparison is simply not there
        return (cpu, null);
    }

    private const string CpuFailed = "CPU 벤치마크를 하지 못했어요: {0}";

    // ------------------------------------------------------------ frame generation (sb_framegen --bench)
    private static async Task<(IReadOnlyList<FgBench> Results, string? Error)> FrameGenBenchAsync(PcSpecs specs, CancellationToken ct)
    {
        if (!Platform.EngineAvailable) return (Array.Empty<FgBench>(), NoEngine);
        var d = specs.Display;
        if (d is not { Width: > 0, Height: > 0 } || d.Width > FgBenchProtocol.MaxSide || d.Height > FgBenchProtocol.MaxSide)
            return (Array.Empty<FgBench>(), "화면 해상도를 읽지 못해서 프레임 생성은 재지 않았어요");
        // "same": the GPU frame generation uses by default (생성 GPU: 화면 GPU)
        var args = FgBenchProtocol.BenchArgs(d.Width, d.Height, "same", Environment.ProcessId);
        var lines = new BenchCollector();
        bool timedOut;
        int exit;
        try { (exit, timedOut) = await RunEngineAsync(args, TimeSpan.FromSeconds(BenchWaitSeconds), lines.Add, ct); }
        catch (Exception x) when (x is not OperationCanceledException) { return (Array.Empty<FgBench>(), string.Format(FgFailed, x.Message)); }
        var results = lines.Ordered();
        if (lines.Error != null) return (results, string.Format(FgFailed, lines.Error));
        if (results.Count > 0 && lines.Done) return (results, null);
        if (timedOut) return (results, "프레임 생성 벤치마크가 제시간에 끝나지 않아서 멈췄어요");
        if (results.Count == 0) return (results, exit != 0 ? string.Format(FgFailed, "exit " + exit) : "프레임 생성 엔진이 결과를 보내지 않았어요");
        return (results, null);
    }

    private const string NoEngine = "프레임 생성 엔진 파일이 없어서 GPU는 재지 않았어요";
    private const string FgFailed = "프레임 생성 벤치마크를 하지 못했어요: {0}";

    // ------------------------------------------------------------ the power-limit test
    private async Task<(GpuPowerTest? Test, string? Error)> PowerTestAsync(Window owner, PcSpecs specs, Action<string> say, CancellationToken ct)
    {
        if (_backend.PowerTestProblem(out var gpu) is { } problem) return (null, problem);
        if (!Platform.EngineAvailable) return (null, NoEngine);
        say("전력 제한 시험: 오버클럭 안내와 관리자 권한을 확인해요");
        var name = gpu!.Name;
        if (!await OnUiAsync(() => OcConsent.EnsureAsync(owner, name, fromPreset: false)))
            return (null, OcConsent.Problem ?? "오버클럭 안내에 동의하지 않아서 전력 제한 시험은 건너뛰었어요");
        ct.ThrowIfCancellationRequested();
        var d = specs.Display;
        int w = d.Width is > 0 and <= FgBenchProtocol.MaxSide ? d.Width : 1920;
        int h = d.Height is > 0 and <= FgBenchProtocol.MaxSide ? d.Height : 1080;
        // the NVIDIA card itself (the one the helper tunes), when the engine's list names it
        var genGpu = specs.Gpus.FirstOrDefault(g => g.Vendor == "nvidia" && !g.Integrated && g.EngineValue.Length > 0)?.EngineValue ?? "same";
        return await _backend.PowerLimitTestAsync((seconds, token) => LoadAsync(seconds, w, h, genGpu, token), say, ct);
    }

    // One --bench-load run: the frames per second of each second. Throws when the engine failed.
    private static async Task<IReadOnlyList<double>> LoadAsync(int seconds, int w, int h, string genGpu, CancellationToken ct)
    {
        var lines = new BenchCollector();
        var args = FgBenchProtocol.LoadArgs(seconds, w, h, genGpu, Environment.ProcessId);
        var (exit, timedOut) = await RunEngineAsync(args, TimeSpan.FromSeconds(seconds + LoadWaitExtraSeconds), lines.Add, ct);
        if (lines.Error != null) throw new InvalidOperationException(lines.Error);
        if (timedOut && !lines.Done) throw new InvalidOperationException("timeout");
        if (lines.LoadFps.Count == 0 && exit != 0) throw new InvalidOperationException("exit " + exit);
        return lines.LoadFps;
    }

    // ------------------------------------------------------------ the engine
    /// Runs sb_framegen with these arguments and hands over each stdout line. TimedOut: it was ended at the time limit.
    /// Throws OperationCanceledException when ct ends (the engine is ended first).
    private static async Task<(int Exit, bool TimedOut)> RunEngineAsync(IReadOnlyList<string> args, TimeSpan timeout,
                                                                         Action<BenchLine> onLine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(Platform.EnginePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            // stdin stays open while it runs: the engine may take its end of file as "quit"
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,   // the engine writes UTF-8; the default is the ANSI code page
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, _) => { };
        if (!p.Start()) throw new InvalidOperationException("the engine did not start");
        p.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        // its own process, by its handle: an engine that hangs or is cancelled ends here, and its stdout with it
        using var end = limit.Token.Register(() => Kill(p));
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null) onLine(FgBenchProtocol.Parse(line));
            await p.WaitForExitAsync(limit.Token);
            return (p.ExitCode, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (-1, true); }
        finally
        {
            Kill(p);
            try { p.StandardInput.Close(); }
            catch (Exception) { }
        }
    }

    private static void Kill(Process p)
    {
        try
        {
            if (p.HasExited) return;
            p.Kill();
            p.WaitForExit(2000);
        }
        catch (Exception) { }   // gone already
    }

    // ------------------------------------------------------------ what the app holds now
    private async Task<IReadOnlyCollection<string>> BoostsOnAsync()
    {
        var on = new List<string>();
        foreach (var f in BoostCatalog.All.Where(f => !f.MacOnly))
            if (await _backend.IsAppliedAsync(f.Id)) on.Add(f.Id);
        return on;
    }

    private async Task<PcNow> NowAsync() => new(await BoostsOnAsync(), _fg.CurrentChoice);

    private static GpuValues Values(GpuChoice c) => new(c.PowerW, c.CoreMHz, c.MemMHz);

    // ------------------------------------------------------------ apply
    /// Applies the chosen applicable recommendations. GPU ones only after OcConsent.EnsureAsync(owner, gpu, fromPreset: true).
    /// A GPU result asks for '유지' within the confirm time (WindowsBoostBackend.GpuConfirmLeft / KeepGpuAsync).
    public async Task<IReadOnlyList<ApplyResult>> ApplyAsync(Window owner, IReadOnlyList<Recommendation> chosen)
    {
        if (_running || _applying) return new[] { new ApplyResult("", false, Busy) };
        _applying = true;
        try { return await ApplyCoreAsync(owner, chosen); }
        finally { _applying = false; }
    }

    private async Task<IReadOnlyList<ApplyResult>> ApplyCoreAsync(Window owner, IReadOnlyList<Recommendation> chosen)
    {
        var picked = chosen.Where(r => r is { Applicable: true }).ToList();
        if (picked.Count == 0) return Array.Empty<ApplyResult>();
        bool tuned = _backend.GpuTuned;
        string? gpuProblem = null;
        bool consent = false;
        if (!tuned && picked.FirstOrDefault(r => r.Kind == RecKind.Gpu && r.PowerW is > 0) is { } gpuRec)
        {
            gpuProblem = _backend.PowerLimitProblem(gpuRec.PowerW!.Value, LastReport?.Bench?.Power?.Gpu);
            if (gpuProblem == null)
            {
                var name = await _backend.TuningGpuNameAsync();
                if (name == null) gpuProblem = "NVIDIA 그래픽카드를 읽지 못해서 전력 제한은 바꾸지 않았어요";
                // a power limit is an overclocking setting: the notice every time, like an imported preset
                else if (!(consent = await OnUiAsync(() => OcConsent.EnsureAsync(owner, name, fromPreset: true, RecommendationReason))))
                    gpuProblem = OcConsent.Problem;   // null: declined (PcApplyPlanner.GpuNoConsent)
            }
        }
        var state = new PcApplyState(await BoostsOnAsync(), _fg.CurrentChoice, FrameGenViewModel.SavedChoice, tuned,
                                     Values(_backend.GpuSettings), gpuProblem, consent);
        var existing = PcCheckStore.LoadApplied(AppliedPath);
        var plan = PcApplyPlanner.Plan(picked, state, existing, DateTime.Now, FrameGenViewModel.Allowance);
        var results = new Dictionary<string, ApplyResult>();
        foreach (var s in plan.Skipped) results[s.Id] = s;
        if (plan.Steps.Count > 0)
        {
            // what is about to change and what is there now, on disk before the first change
            try { PcCheckStore.SaveApplied(AppliedPath, plan.Record); }
            catch (Exception x)
            {
                foreach (var step in plan.Steps) results[step.RecId] = new ApplyResult(step.RecId, false, string.Format(RecordFailed, x.Message));
                return Ordered(plan, results);
            }
        }
        bool gpuFailed = false;
        foreach (var step in plan.Steps)
        {
            ApplyResult r;
            try { r = await RunStepAsync(step, state); }
            catch (Exception x) { r = new ApplyResult(step.RecId, false, "실패했어요: " + x.Message); }
            gpuFailed |= step.Kind == ApplyStepKind.Gpu && !r.Ok;
            results[step.RecId] = r;
        }
        if (plan.Steps.Count > 0)
        {
            var settled = PcApplyPlanner.Settle(plan.Record, existing, await BoostsOnAsync(), gpuFailed);
            try { PcCheckStore.SaveApplied(AppliedPath, settled); }
            catch (Exception) { }   // the record written first stays; undo only turns off what is on
        }
        return Ordered(plan, results);
    }

    private const string RecordFailed = "되돌리기 기록을 저장하지 못해서 바꾸지 않았어요: {0}";

    private static IReadOnlyList<ApplyResult> Ordered(ApplyPlan plan, Dictionary<string, ApplyResult> results) =>
        plan.Order.Where(results.ContainsKey).Select(id => results[id]).ToList();

    private async Task<ApplyResult> RunStepAsync(ApplyStep step, PcApplyState state)
    {
        switch (step.Kind)
        {
            case ApplyStepKind.Boost:
                var text = await _backend.ApplyAsync(step.BoostId!);
                return new ApplyResult(step.RecId, await _backend.IsAppliedAsync(step.BoostId!), text);
            case ApplyStepKind.FrameGen:
                var want = step.FrameGen!;
                FgChoice set;
                string? unsaved = null;
                try { set = await OnUiAsync(() => Task.FromResult(_fg.ApplyChoice(want))); }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException)
                {
                    set = _fg.CurrentChoice;   // set, only not on disk
                    unsaved = x.Message;
                }
                var line = string.Format(FgApplied, Describe(set));
                if (unsaved != null) line += " · " + string.Format(FgNotSaved, unsaved);
                return new ApplyResult(step.RecId, true, line);
            case ApplyStepKind.Gpu:
                // the power limit only, clocks 0, through the GPU page's own switch (backup, '유지', auto-restore)
                _backend.SetGpuChoice(step.PowerW, 0, 0);
                var r = await _backend.ApplyAsync("gpu.oc");
                if (_backend.GpuTuned) return new ApplyResult(step.RecId, true, r);
                var before = state.GpuChoice;   // not on: the GPU page keeps the values it had
                _backend.SetGpuChoice(before.PowerW, before.CoreMHz, before.MemMHz);
                return new ApplyResult(step.RecId, false, r);
            default:
                return new ApplyResult(step.RecId, false, PcApplyPlanner.NothingToApply);
        }
    }

    private const string FgApplied = "프레임 생성 화면을 바꿨어요: {0}";
    private const string FgNotSaved = "저장하지 못해서 앱을 다시 켜면 기본값으로 시작해요 ({0})";

    /// "자동 · 최대 4배 · 균형": the names the FrameGen page shows.
    public static string Describe(FgChoice c)
    {
        var mode = c.Mode switch { "auto" => "자동", "off" => "끄기", var m when m.StartsWith('x') => m[1..] + "배", var m => m };
        var quality = c.Quality switch { "low" => "저사양", "performance" => "성능", "quality" => "품질", _ => "균형" };
        return c.Mode == "auto" ? $"{mode} · 최대 {c.MaxMul}배 · {quality}" : $"{mode} · {quality}";
    }

    // ------------------------------------------------------------ undo
    /// True when something applied through this page can still be put back (also after an app restart).
    public bool CanUndo => PcCheckStore.LoadApplied(AppliedPath) is { IsEmpty: false };

    /// Puts back what the last ApplyAsync changed. Returns the line to show.
    public async Task<string> UndoAsync()
    {
        if (_running || _applying) return Busy;
        _applying = true;
        try
        {
            var record = PcCheckStore.LoadApplied(AppliedPath);
            if (record is not { IsEmpty: false })
            {
                PcCheckStore.Delete(AppliedPath);
                return "되돌릴 추천 적용이 없어요";
            }
            var now = new PcUndoState(await BoostsOnAsync(), _fg.CurrentChoice, _backend.GpuTuned, Values(_backend.GpuSettings));
            var plan = PcApplyPlanner.Undo(record, now);
            var notes = new List<string>(plan.Kept);
            int back = 0;
            if (plan.Gpu is { } g)
            {
                if (plan.TurnGpuOff)
                {
                    var r = await _backend.RevertAsync("gpu.oc");
                    if (_backend.GpuTuned) notes.Add("오버클럭 / 전력 제한: " + r);
                }
                if (!_backend.GpuTuned)
                {
                    _backend.SetGpuChoice(g.Before.PowerW, g.Before.CoreMHz, g.Before.MemMHz);
                    back++;
                }
            }
            if (plan.FrameGen is { } f)
            {
                try { await OnUiAsync(() => { _fg.RestoreChoice(f.Before, f.BeforeSaved); return Task.FromResult(true); }); }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException) { notes.Add(string.Format(FgNotRestored, x.Message)); }
                back++;
            }
            foreach (var id in plan.RevertBoosts)
            {
                var r = await _backend.RevertAsync(id);
                if (!await _backend.IsAppliedAsync(id)) back++;
                else notes.Add($"{BoostCatalog.All.FirstOrDefault(x => x.Id == id)?.Title ?? id} · {r}");   // " · ": each part has its own English
            }
            var left = PcApplyPlanner.AfterUndo(record, plan, await BoostsOnAsync(), _backend.GpuTuned, DateTime.Now);
            try { PcCheckStore.SaveApplied(AppliedPath, left); }   // empty: the file goes
            catch (Exception x) { notes.Add(string.Format(RecordNotCleared, x.Message)); }
            var head = back > 0 ? string.Format(UndoDone, back) : plan.IsEmpty ? "되돌릴 것이 없었어요" : "되돌리지 못했어요";
            return string.Join(" · ", notes.Prepend(head));
        }
        finally { _applying = false; }
    }

    private const string UndoDone = "추천으로 바꾼 {0}개 항목을 원래대로 돌렸어요";
    private const string FgNotRestored = "프레임 생성 설정은 돌렸지만 저장된 선택을 고치지 못했어요 ({0})";
    private const string RecordNotCleared = "되돌리기 기록을 지우지 못했어요 ({0})";

    // ------------------------------------------------------------ the last report
    private static readonly object LastLock = new();
    private static PcCheckReport? _last;
    private static bool _lastRead;

    /// The last saved report, or null.
    public static PcCheckReport? LastReport
    {
        get
        {
            lock (LastLock)
            {
                if (!_lastRead) { _last = PcCheckStore.LoadReport(ReportPath); _lastRead = true; }
                return _last;
            }
        }
    }

    private static void SetLast(PcCheckReport report)
    {
        lock (LastLock) { _last = report; _lastRead = true; }
    }

    // ------------------------------------------------------------ the UI thread
    // The page passes a progress line that sets a control; the notice and the FrameGen page live on the UI thread.
    private static Action<string> OnUi(Action<string> progress) => line =>
    {
        if (Dispatcher.UIThread.CheckAccess()) progress(line);
        else Dispatcher.UIThread.Post(() => progress(line));
    };

    private static Task<T> OnUiAsync<T>(Func<Task<T>> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);
}
