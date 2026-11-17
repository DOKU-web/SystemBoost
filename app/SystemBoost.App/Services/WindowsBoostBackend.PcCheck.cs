// What PC 맞춤 추천 (Services/PcCheck/PcCheckRunner.cs) needs from the backend. Nothing here writes the PC its own way:
//   the high-performance plan   ApplyPowerPlan / DeactivateAsync("cpu.power"), the code a game uses, with its record in
//                               boost-state.json before the plan changes; always switched back in finally
//   the power-limit test        the helper's oc.set (power only, clocks 0, the temperature limit as set on the GPU page)
//                               and oc.restore through RestoreAsync, with a record of kind "oc" in boost-state.json
//                               before the card changes, so a crash finds its way back the way gpu.oc does
//                               (PutGpuBackAsync at the next start, RestoreAsync when the app closes, and the helper
//                               itself as it exits). The card goes back on every path: done, a fault, cancel, an error.
// The tuned part holds _gate, so nothing else (the watch loop, a switch on another page) moves while it runs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemBoost.App.Services.PcCheck;

namespace SystemBoost.App.Services;

public sealed partial class WindowsBoostBackend
{
    /// The feature name of the power-limit test's record (kind "oc"). Not a switch: it never shows as gpu.oc being on.
    internal const string PowerTestId = "pc.powertest";
    /// One --bench-load run of the test, and the first seconds of it that are not counted (clocks ramp up, shaders load).
    public const int PowerTestLoadSeconds = 8, PowerTestWarmupSeconds = 2;

    /// A game runs now: one in '내 게임', or the window frame generation is on (the watch loop's own test).
    public bool GameRunningNow
    {
        get
        {
            if (_gameRunning) return true;
            try { return RunningGames(ProcessNames()) != null; }
            catch (InvalidOperationException) { return false; }   // the game list changed while it was read: the watch loop sees it next
        }
    }

    /// A high-performance plan exists, another plan is active, and cpu.power has no record now (no game holds it).
    public bool HighPlanTestable
    {
        get
        {
            try { return !_measuring && !Has("cpu.power") && FindHighPlan() != null && !OnHighPlan(); }
            catch (Exception) { return false; }
        }
    }

    /// Runs work on the high-performance power plan: switched with cpu.power's own code and record, and switched back
    /// when work ends, however it ends. (result, null), or (default, the line why it did not run).
    public async Task<(T? Result, string? Error)> OnHighPerformancePlanAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (_measuring) return (default, BusyMeasuring);
        string line;
        await _gate.WaitAsync(ct);
        try
        {
            if (Has("cpu.power")) return (default, PlanBusy);
            line = ApplyPowerPlan();   // the record first (boost-state.json), then the plan
            Save();
        }
        finally { _gate.Release(); }
        if (!Has("cpu.power") || !OnHighPlan()) return (default, line);   // no such plan, or it could not be switched
        try { return (await work(), null); }
        finally
        {
            await _gate.WaitAsync();   // not cancellable: the plan always goes back
            try
            {
                // a game that started meanwhile with cpu.power on keeps the plan; its end puts it back (EndGameTimeAsync)
                if (!(_gameRunning && IsOn("cpu.power")) && await DeactivateAsync("cpu.power") is { } r && r != "원래대로 돌렸어요")
                    Log(PlanNotBack + r);
                Save();
            }
            catch (Exception x) { Log(PlanNotBack + x.Message); }
            finally { _gate.Release(); Publish(); }
        }
    }

    internal const string PlanBusy = "전원 계획이 이미 바뀌어 있어서 고성능 계획과 비교하지 않았어요";
    private const string PlanNotBack = "PC 맞춤 추천: 전원 계획을 되돌리지 못했어요 · ";

    // ------------------------------------------------------------ the power-limit test
    /// Why the power-limit test cannot run now; null when it can. gpu: the NVIDIA card as NVML reads it.
    public string? PowerTestProblem(out GpuReading? gpu)
    {
        gpu = Nvml.Read();
        if (gpu == null) return "NVIDIA 그래픽카드를 읽지 못해서 전력 제한 시험을 건너뛰었어요";
        if (PcForm.IsLaptop()) return "노트북에서는 전력 제한을 시험하지 않아요";
        if (!License.Allowed(PremiumFeature.Overclock)) return $"전력 제한 시험은 {License.PlanOf(PremiumFeature.Overclock)} 기능이에요";
        if (gpu.PowerMaxW <= gpu.PowerDefaultW) return "이 그래픽카드는 기본값보다 높은 전력 제한을 허용하지 않아요";
        if (_measuring) return BusyMeasuring;
        if (_s.Backups.Any(b => b.Kind == "oc")) return "오버클럭 / 전력 제한이 켜져 있어서 전력 제한 시험을 건너뛰었어요";
        if (gpu.CoreMHz != 0 || gpu.MemMHz != 0) return "클럭 오프셋이 0이 아니라서 전력 제한 시험을 건너뛰었어요 (다른 프로그램이 바꿨을 수 있어요)";
        bool atDefault = gpu.PowerDefaultMw > 0 ? Math.Abs((long)gpu.PowerNowMw - gpu.PowerDefaultMw) <= 1000 : gpu.PowerNowW == gpu.PowerDefaultW;
        if (!atDefault) return "전력 제한이 기본값이 아니라서 시험을 건너뛰었어요 (다른 프로그램이 바꿨을 수 있어요)";
        return null;
    }

    /// The same GPU load at the default limit, at the card's highest limit, and at the default again (the drift between
    /// the two default runs is the noise). load(seconds, token): one --bench-load run on this card, the frames per second
    /// of each second; it throws OperationCanceledException when the token ends. A fault (the helper put the card back,
    /// the driver, the temperature limit) ends the test and is in GpuPowerTest.Fault. The card goes back on every path.
    /// Call after OcConsent.EnsureAsync for this card. (test, null), (test with Fault, line), or (null, why it did not run).
    public async Task<(GpuPowerTest? Test, string? Error)> PowerLimitTestAsync(
        Func<int, CancellationToken, Task<IReadOnlyList<double>>> load, Action<string> progress, CancellationToken ct)
    {
        if (PowerTestProblem(out var gpu) is { } problem) return (null, problem);
        if (!OcConsent.Valid(gpu!.Name)) return (null, OverclockNotice.NotAgreed + " (바뀐 것 없음)");
        var e = await _helper.EnsureAsync();
        if (e != null) return (null, e);
        ct.ThrowIfCancellationRequested();

        var watch = new PowerWatch(Math.Clamp(_s.Gpu.TempLimitC, 40, 87), gpu.TempC);
        int tested = gpu.PowerMaxW;
        double? off1 = null, on = null, off2 = null;
        string? error = null;
        bool engineFault = false;
        try
        {
            progress($"전력 제한 시험 1/3: 기본 {gpu.PowerDefaultW} W에서 GPU 부하를 재는 중…");
            off1 = await LoadPhaseAsync(load, watch, tuned: false, ct);
            if (off1 == null && watch.Fault == null) return (null, NoLoadResult);
            if (watch.Fault == null)
            {
                progress($"전력 제한 시험 2/3: {tested} W에서 재는 중…");
                (on, tested, error) = await TunedPhaseAsync(load, watch, gpu, tested, ct);
                engineFault = error == null && on == null && watch.Fault == null;
            }
            if (watch.Fault == null && on != null)
            {
                progress($"전력 제한 시험 3/3: 기본 {gpu.PowerDefaultW} W에서 다시 재는 중…");
                off2 = await LoadPhaseAsync(load, watch, tuned: false, ct);
                engineFault |= off2 == null && watch.Fault == null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception x)   // the engine failed: the card is back already (TunedPhaseAsync)
        {
            if (off1 == null) return (null, string.Format(PowerTestFailed, x.Message));
            error = string.Format(PowerTestFailed, x.Message);
            engineFault = true;
        }
        if (error != null && on == null && !engineFault && watch.Fault == null) return (null, error);   // the limit could not be set

        double baseFps = off1 ?? 0;
        double defaultFps = off2 is > 0 ? (baseFps + off2.Value) / 2 : baseFps;
        double noise = off2 is > 0 && defaultFps > 0 ? Math.Abs(baseFps - off2.Value) / defaultFps * 100 : 0;
        var fault = watch.Fault ?? (engineFault ? "engine" : null);
        var test = new GpuPowerTest(gpu.Name, gpu.PowerDefaultW, tested, defaultFps, on ?? 0, noise, watch.MaxTempC, fault);
        return (test, fault != null ? error ?? FaultLine(fault) : null);
    }

    internal const string NoLoadResult = "GPU 부하 결과가 나오지 않아서 전력 제한 시험을 건너뛰었어요";
    internal const string PowerTestFailed = "전력 제한 시험을 마치지 못했어요: {0}";

    /// The line for a fault of the test (the card is back either way).
    public static string FaultLine(string fault) => fault switch
    {
        "driver" => "그래픽 드라이버 오류가 보여서 전력 제한 시험을 멈췄어요. GPU는 원래대로 돌렸어요",
        "timeout" => "전력 제한 시험이 너무 오래 걸려서 도우미가 GPU를 원래대로 돌렸어요",
        "engine" => "GPU 부하 프로그램이 멈춰서 전력 제한 시험을 멈췄어요. GPU는 원래대로 돌렸어요",
        var t when t.StartsWith("temp:") => $"GPU 온도가 {t[5..]} °C까지 올라서 전력 제한 시험을 멈췄어요. GPU는 원래대로 돌렸어요",
        _ => "관리자 도우미가 응답하지 않아서 전력 제한 시험을 멈췄어요. GPU는 원래대로 돌렸어요",
    };

    // oc.set with the record on disk first, the load, and the card back in finally, all under _gate.
    private async Task<(double? Fps, int Tested, string? Error)> TunedPhaseAsync(
        Func<int, CancellationToken, Task<IReadOnlyList<double>>> load, PowerWatch watch, GpuReading gpu, int tested, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_s.Backups.Any(b => b.Kind == "oc")) return (null, tested, "오버클럭 / 전력 제한이 켜져 있어서 전력 제한 시험을 건너뛰었어요");
            if (!OcConsent.Valid(gpu.Name)) return (null, tested, OverclockNotice.NotAgreed + " (바뀐 것 없음)");
            var now = Nvml.Read();
            if (now == null) return (null, tested, "NVIDIA 그래픽카드를 읽지 못해서 전력 제한 시험을 건너뛰었어요");
            _s.Backups.Add(new BoostBackup { Feature = PowerTestId, Kind = "oc", Value = $"{now.PowerNowMw};{now.CoreMHz};{now.MemMHz}" });
            Save();
            int limitC = watch.LimitC;
            var (ok, data) = await _helper.CallAsync("oc.set", Num(tested), "0", "0", Num(limitC));
            // the highest limit in whole watts can round past the driver's own milliwatts
            if (!ok && data.Contains("out of range") && tested - 1 > gpu.PowerDefaultW)
                (ok, data) = await _helper.CallAsync("oc.set", Num(--tested), "0", "0", Num(limitC));
            if (!ok) return (null, tested, "전력 제한을 바꾸지 못해서 시험을 건너뛰었어요: " + data);
            return (await LoadPhaseAsync(load, watch, tuned: true, ct), tested, null);
        }
        finally
        {
            // done, a fault (the helper may have put it back already), cancel or an exception: the recorded values go back
            try
            {
                var mine = _s.Backups.Where(b => b.Feature == PowerTestId).ToList();
                if (mine.Count > 0 && await RestoreAsync(mine) is { } failed)
                    Log($"전력 제한 시험 뒤에 GPU를 되돌리지 못했어요: {failed}. 앱을 끄면 관리자 도우미가 되돌려요");
                Save();
            }
            catch (Exception x) { Log($"전력 제한 시험 뒤에 GPU를 되돌리지 못했어요: {x.Message}. 앱을 끄면 관리자 도우미가 되돌려요"); }
            finally { _gate.Release(); Publish(); }
        }
    }

    // One load run with the watch beside it: the temperature every second, and in the tuned run the helper's state.
    // Null when it stopped for a fault (watch.Fault) or gave no seconds to count.
    private async Task<double?> LoadPhaseAsync(Func<int, CancellationToken, Task<IReadOnlyList<double>>> load, PowerWatch watch,
                                               bool tuned, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = WatchPowerTestAsync(watch, tuned, stop);
        try
        {
            var seconds = await load(PowerTestLoadSeconds, stop.Token);
            return watch.Fault == null ? FgBenchProtocol.LoadFps(seconds, PowerTestWarmupSeconds) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && watch.Fault != null) { return null; }
        finally
        {
            stop.Cancel();
            try { await monitor; }
            catch (Exception) { }   // it ends with the cancel
        }
    }

    private async Task WatchPowerTestAsync(PowerWatch w, bool tuned, CancellationTokenSource stop)
    {
        while (true)
        {
            await Task.Delay(1000, stop.Token);
            string? fault = null;
            try
            {
                var g = await Task.Run(Nvml.Read);
                if (g == null) fault = "driver";   // NVML stopped answering
                else
                {
                    w.Saw(g.TempC);
                    if (g.TempC >= w.LimitC) fault = $"temp:{g.TempC}";
                }
                if (fault == null && tuned)
                {
                    var (ok, data) = await _helper.CallAsync("oc.status");
                    var f = data.Split(';');
                    if (!ok) fault = "helper";
                    else if (f.Length >= 4 && f[0] == "0") fault = f[3].Length > 0 ? f[3] : "helper";   // the helper put it back
                }
            }
            catch (Exception x) when (x is not OperationCanceledException) { fault = "helper"; }   // cannot watch: stop
            if (fault == null) continue;
            w.Trip(fault);
            stop.Cancel();
            return;
        }
    }

    private sealed class PowerWatch
    {
        private readonly object _lock = new();
        private int _max;
        private string? _fault;
        public PowerWatch(int limitC, int startC) { LimitC = limitC; _max = startC; }
        public int LimitC { get; }
        public int MaxTempC { get { lock (_lock) return _max; } }
        public string? Fault { get { lock (_lock) return _fault; } }
        public void Saw(int c) { lock (_lock) _max = Math.Max(_max, c); }
        public void Trip(string why) { lock (_lock) _fault ??= why; }
    }

    // ------------------------------------------------------------ applying a recommended power limit
    /// Why a recommended power limit cannot go on this card now; null when it can: the NVIDIA card NVML reads, on a
    /// desktop, the one that was tested, and the value within the card's own range. gpu.oc itself checks the rest.
    public string? PowerLimitProblem(int watts, string? testedGpu)
    {
        var gpu = Nvml.Read();
        if (gpu == null) return "NVIDIA 그래픽카드를 읽지 못해서 전력 제한은 바꾸지 않았어요";
        if (PcForm.IsLaptop()) return "노트북에서는 전력 제한을 바꾸지 않아요";
        if (testedGpu is { Length: > 0 } && !OcConsent.SameGpu(testedGpu, gpu.Name)) return "검사한 뒤로 그래픽카드가 바뀌었어요. 다시 검사해 주세요";
        if (watts < gpu.PowerMinW || watts > gpu.PowerMaxW) return "그래픽카드가 허용하는 전력 제한 범위를 벗어나서 바꾸지 않았어요";
        return null;
    }
}
