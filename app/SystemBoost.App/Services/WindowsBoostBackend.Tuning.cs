// GPU tuning and per-game measurement, both premium.
//   gpu.oc          power limit and clock offsets through NVML. Lasts only while the app
//                   runs: the helper undoes it if it is not confirmed in time, on heat, on a
//                   driver fault and when the app goes away (native/helper/src/gpuoc.cpp).
//   boost.profiles  switches an item on and off while the game runs, measures frames from
//                   Windows event tracing and keeps only what clearly helped that game.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

/// What was picked on the GPU page. PowerW 0 means the card's default.
public sealed class GpuChoice
{
    public int PowerW { get; set; }
    public int CoreMHz { get; set; }
    public int MemMHz { get; set; }
    public bool RiskAccepted { get; set; }
    public int TempLimitC { get; set; } = 85;
}

public sealed class ProfileResult
{
    public string Feature { get; set; } = "";
    public double OffFps { get; set; }
    public double OnFps { get; set; }
    public double OffLow { get; set; }
    public double OnLow { get; set; }
    public double GainPercent { get; set; }
    public double NoisePercent { get; set; }
    public bool Keep { get; set; }
    public string Note { get; set; } = "";      // why it could not be measured
}

public sealed class GameProfile
{
    public DateTime MeasuredAt { get; set; }
    public List<ProfileResult> Results { get; set; } = new();
}

public sealed record GpuReading(string Name, int PowerNowW, int PowerDefaultW, int PowerMinW, int PowerMaxW,
                                int CoreMHz, int MemMHz, int TempC, uint PowerNowMw);

/// Read-only NVML calls, which need no elevation. Tuning goes through the helper.
internal static class Nvml
{
    private const string Dll = "nvml.dll";
    private const int ClockGraphics = 0, ClockMemory = 2, TemperatureGpu = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ClockOffset { public uint Version; public int Type, Pstate, OffsetMHz, MinMHz, MaxMHz; }
    private static readonly uint ClockOffsetV1 = (uint)Marshal.SizeOf<ClockOffset>() | (1u << 24);

    [DllImport(Dll, EntryPoint = "nvmlInit_v2"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Init();
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetHandle(uint index, out IntPtr device);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetName(IntPtr device, byte[] name, uint length);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimit"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPower(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDefaultPower(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPowerRange(IntPtr device, out uint min, out uint max);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockOffsets"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetOffset(IntPtr device, ref ClockOffset info);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetTemperature(IntPtr device, int sensor, out uint celsius);

    private static IntPtr _device;

    /// Null when there is no NVIDIA card or its driver cannot report offsets.
    public static GpuReading? Read()
    {
        try
        {
            if (_device == IntPtr.Zero && (Init() != 0 || GetHandle(0, out _device) != 0)) return null;
            var name = new byte[96];
            if (GetName(_device, name, (uint)name.Length) != 0) return null;
            if (GetPower(_device, out var now) != 0 || GetDefaultPower(_device, out var def) != 0 ||
                GetPowerRange(_device, out var min, out var max) != 0) return null;
            var core = new ClockOffset { Version = ClockOffsetV1, Type = ClockGraphics };
            var mem = new ClockOffset { Version = ClockOffsetV1, Type = ClockMemory };
            if (GetOffset(_device, ref core) != 0 || GetOffset(_device, ref mem) != 0) return null;
            if (GetTemperature(_device, TemperatureGpu, out var temp) != 0) return null;
            return new GpuReading(Encoding.UTF8.GetString(name).TrimEnd('\0'), Watts(now), Watts(def), Watts(min), Watts(max),
                                  core.OffsetMHz, mem.OffsetMHz, (int)temp, now);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static int Watts(uint milliwatts) => (int)Math.Round(milliwatts / 1000.0);
}

public sealed partial class WindowsBoostBackend
{
    private const int GpuConfirmSeconds = 25;   // the helper gives up a little later

    private const int MeasureWindowSeconds = 15, MeasureSettleSeconds = 3;
    private const double MinGainPercent = 3, NoiseMargin = 2, WorstLowDrop = 0.97;
    private const string BusyMeasuring = "측정이 끝난 뒤에 바꿀 수 있어요 (바뀐 것 없음)";
    private static readonly string[] Measurable = { "cpu.power", "cpu.ecores" };

    private DateTime? _gpuConfirmBy;
    private int _gpuTempC;
    private bool _measuring;
    private readonly HashSet<string> _profileItems = new();

    private bool IsOn(string id) => _s.Enabled.Contains(id) || _profileItems.Contains(id);

    // ------------------------------------------------------------ GPU tuning
    public static GpuReading? ReadGpu() => Nvml.Read();
    public GpuChoice GpuSettings => _s.Gpu;
    public bool GpuTuned => Has("gpu.oc");
    public int? GpuConfirmLeft => _gpuConfirmBy is DateTime t ? Math.Max(0, (int)Math.Ceiling((t - DateTime.UtcNow).TotalSeconds)) : null;

    public void SetGpuChoice(int powerW, int coreMHz, int memMHz, bool riskAccepted)
    {
        (_s.Gpu.PowerW, _s.Gpu.CoreMHz, _s.Gpu.MemMHz, _s.Gpu.RiskAccepted) = (powerW, coreMHz, memMHz, riskAccepted);
        Save();
    }

    private async Task<string> ApplyGpuTuningAsync()
    {
        if (!License.Allowed(PremiumFeature.Overclock)) return $"{License.PlanOf(PremiumFeature.Overclock)} 기능이에요 (바뀐 것 없음)";
        var gpu = Nvml.Read();
        if (gpu == null) return "NVIDIA 그래픽카드와 최신 드라이버가 필요해요 (바뀐 것 없음)";
        var c = _s.Gpu;
        int power = c.PowerW > 0 ? c.PowerW : gpu.PowerDefaultW;
        if (power == gpu.PowerNowW && c.CoreMHz == gpu.CoreMHz && c.MemMHz == gpu.MemMHz)
            return "아래에서 값을 바꾼 뒤 켜 주세요 (바뀐 것 없음)";
        bool raises = power > gpu.PowerDefaultW || c.CoreMHz > 0 || c.MemMHz > 0;
        if (raises && !c.RiskAccepted) return "아래 안내를 읽고 체크해 주세요 (바뀐 것 없음)";
        var e = await _helper.EnsureAsync();
        if (e != null) return e;
        // on disk before anything changes, so a crash can still find its way back
        _s.Backups.Add(new BoostBackup { Feature = "gpu.oc", Kind = "oc", Value = $"{gpu.PowerNowMw};{gpu.CoreMHz};{gpu.MemMHz}" });
        Save();
        var (ok, data) = await _helper.CallAsync("oc.set", Num(power), Num(c.CoreMHz), Num(c.MemMHz), Num(c.TempLimitC));
        if (!ok)
        {
            _s.Backups.RemoveAll(b => b.Kind == "oc");
            return "적용하지 못해서 원래 값으로 뒀어요: " + data;
        }
        _gpuConfirmBy = DateTime.UtcNow.AddSeconds(GpuConfirmSeconds);
        Log($"GPU 설정 적용: 전력 {power} W, 코어 {c.CoreMHz:+0;-0;0} MHz, 메모리 {c.MemMHz:+0;-0;0} MHz");
        return $"적용했어요. {GpuConfirmSeconds}초 안에 아래 '유지'를 누르지 않으면 원래대로 돌아가요";
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    public async Task<string> KeepGpuAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var (ok, _) = await _helper.CallAsync("oc.keep");
            _gpuConfirmBy = null;
            return ok ? "유지할게요. 앱을 끄면 원래대로 돌아가요" : "이미 원래대로 돌아갔어요";
        }
        finally { _gate.Release(); Publish(); }
    }

    private async Task<string?> RestoreGpuAsync(BoostBackup b)
    {
        _gpuConfirmBy = null;
        var saved = b.Value.Split(';');
        if (saved.Length != 3) return "GPU 백업 값이 잘못됐어요";
        return await HelperError("oc.restore", saved[0], saved[1], saved[2]);
    }

    private async Task WatchGpuAsync()
    {
        if (!Has("gpu.oc")) return;
        var (ok, data) = await _helper.CallAsync("oc.status");
        if (!ok)
        {
            await PutGpuBackAsync();    // the helper was killed before it could undo anything
            return;
        }
        var f = data.Split(';');
        if (f.Length < 4 || f[0] == "1") { if (f.Length >= 3) int.TryParse(f[2], out _gpuTempC); return; }
        _gpuConfirmBy = null;
        _s.Backups.RemoveAll(b => b.Kind == "oc");
        Save();
        var why = f[3] switch
        {
            "timeout" => "'유지'를 누르지 않아서",
            "driver" => "그래픽 드라이버 오류가 보여서",
            var t when t.StartsWith("temp:") => $"GPU 온도가 {t[5..]} °C까지 올라서",
            _ => "",
        };
        var line = $"{why} GPU 설정을 원래대로 돌렸어요".TrimStart();
        Log(line);
        Notice?.Invoke(line);
        Publish();
    }

    // After a crash, or when the helper is gone. Nothing to do if the card is already back.
    private async Task PutGpuBackAsync()
    {
        var saved = _s.Backups.Where(b => b.Kind == "oc").ToList();
        if (saved.Count == 0) return;
        var gpu = Nvml.Read();
        if (gpu != null && saved[0].Value == $"{gpu.PowerNowMw};{gpu.CoreMHz};{gpu.MemMHz}")
        {
            _gpuConfirmBy = null;
            _s.Backups.RemoveAll(b => b.Kind == "oc");
            Save();
            Publish();
            return;
        }
        var e = await _helper.EnsureAsync();
        if (e == null) e = await RestoreAsync(saved);
        Log(e == null ? "GPU 설정을 원래대로 돌렸어요" : "GPU 설정을 되돌리지 못했어요: " + e);
        Save();
        Publish();
    }

    // ------------------------------------------------------------ per-game measurement
    public bool Measuring => _measuring;
    public IReadOnlyDictionary<string, GameProfile> GameProfiles => _s.GameProfiles;

    private async Task<string> ApplyGameProfileAsync()
    {
        if (!License.Allowed(PremiumFeature.PerGameProfiles)) return $"게임별 자동 최적화는 {License.PlanOf(PremiumFeature.PerGameProfiles)} 기능이에요";
        if (_gameName == null || !_s.GameProfiles.TryGetValue(_gameName, out var profile)) return $"{_gameName}은(는) 아직 측정하지 않았어요";
        var keep = profile.Results.Where(r => r.Keep).Select(r => r.Feature).ToList();
        if (keep.Count == 0) return $"{_gameName}: 측정에서 효과가 확인된 항목이 없어요";
        foreach (var id in keep) if (_profileItems.Add(id)) Log(await ActivateAsync(id));
        return $"{_gameName}: 측정에서 효과가 확인된 {keep.Count}개 항목을 켰어요";
    }

    private async Task EndGameProfileAsync()
    {
        foreach (var id in _profileItems.ToList())
        {
            _profileItems.Remove(id);
            if (!_s.Enabled.Contains(id)) await DeactivateAsync(id);
        }
    }

    private string? CannotMeasure(string id) => id switch
    {
        "cpu.ecores" when EfficientMask() == 0 => "이 CPU에는 E코어가 없어요",
        "cpu.power" when FindHighPlan() == null => "이 PC에는 고성능 전원 계획이 없어요",
        "cpu.power" when !Has(id) && OnHighPlan() => "이미 고성능 전원 계획이라 비교할 수 없어요",
        _ => null,
    };

    /// Runs for about two and a half minutes. Returns the line to show.
    public async Task<string> MeasureGameAsync(Action<string> progress)
    {
        if (!License.Allowed(PremiumFeature.PerGameProfiles)) return $"게임별 자동 최적화는 {License.PlanOf(PremiumFeature.PerGameProfiles)} 기능이에요";
        if (_measuring) return "이미 측정 중이에요";
        var game = RunningGames(ProcessNames());
        if (game == null) return "먼저 '내 게임'에 있는 게임을 실행하세요";
        var e = await _helper.EnsureAsync();
        if (e != null) return e;
        var (ok, data) = await _helper.CallAsync("fps.start");
        if (!ok) return "프레임을 잴 수 없어요: " + data;
        _measuring = true;
        Publish();
        try
        {
            var results = new List<ProfileResult>();
            foreach (var id in Measurable)
            {
                var title = BoostCatalog.All.First(f => f.Id == id).Title;
                var note = CannotMeasure(id);
                if (note != null) { results.Add(new ProfileResult { Feature = id, Note = note }); continue; }
                progress($"{game}: {title} 비교 중…");
                var r = await CompareAsync(id, game, title, progress);
                if (r == null) return "프레임을 재지 못했어요. 게임을 맨 앞에 두고 다시 해 주세요 (저장한 것 없음)";
                results.Add(r);
            }
            _s.GameProfiles[game] = new GameProfile { MeasuredAt = DateTime.Now, Results = results };
            return $"{game}: 측정을 마쳤어요. 효과가 확인된 항목 {results.Count(r => r.Keep)}개";
        }
        catch (OperationCanceledException) { return "측정을 멈췄어요 (저장한 것 없음)"; }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                foreach (var id in Measurable)
                {
                    if (IsOn(id) && _gameRunning) await ActivateAsync(id);
                    else await DeactivateAsync(id);
                }
                await _helper.CallAsync("fps.stop");
                _measuring = false;
                Save();
                if (_gameRunning && _s.Enabled.Contains("boost.profiles")) Log(await ApplyGameProfileAsync());
                Save();
            }
            finally { _gate.Release(); Publish(); }
        }
    }

    // Off, on, on, off: a slow drift in the game hits both sides evenly.
    private async Task<ProfileResult?> CompareAsync(string id, string game, string title, Action<string> progress)
    {
        var order = new[] { false, true, true, false };
        double[] avg = new double[order.Length], low = new double[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            progress($"{game}: {title} {(order[i] ? "켜고" : "끄고")} 재는 중 ({i + 1}/{order.Length})");
            await _gate.WaitAsync();
            try { if (order[i]) await ActivateAsync(id); else await DeactivateAsync(id); }
            finally { _gate.Release(); }
            await Task.Delay(TimeSpan.FromSeconds(MeasureSettleSeconds), _stop.Token);
            await _helper.CallAsync("fps.mark");
            await Task.Delay(TimeSpan.FromSeconds(MeasureWindowSeconds), _stop.Token);
            var frame = await ReadFramesAsync(game);
            if (frame == null) return null;
            (avg[i], low[i]) = frame.Value;
        }
        double offAvg = (avg[0] + avg[3]) / 2, onAvg = (avg[1] + avg[2]) / 2;
        double offLow = (low[0] + low[3]) / 2, onLow = (low[1] + low[2]) / 2;
        double gain = (onAvg - offAvg) / offAvg * 100;
        double noise = (Math.Abs(avg[0] - avg[3]) + Math.Abs(avg[1] - avg[2])) / 2 / offAvg * 100;
        return new ProfileResult
        {
            Feature = id, OffFps = offAvg, OnFps = onAvg, OffLow = offLow, OnLow = onLow, GainPercent = gain, NoisePercent = noise,
            Keep = gain >= MinGainPercent && gain >= NoiseMargin * noise && onLow >= offLow * WorstLowDrop,
        };
    }

    // The process with the most frames: a launcher can share the game's name.
    private async Task<(double avg, double low)?> ReadFramesAsync(string game)
    {
        long best = 0;
        (double avg, double low)? result = null;
        foreach (var p in Process.GetProcessesByName(game))
        {
            var (ok, data) = await _helper.CallAsync("fps.read", Num(p.Id));
            p.Dispose();
            var f = data.Split(';');
            if (!ok || f.Length != 4 || !long.TryParse(f[0], out var frames) || frames <= best) continue;
            best = frames;
            result = (double.Parse(f[2], CultureInfo.InvariantCulture), double.Parse(f[3], CultureInfo.InvariantCulture));
        }
        return result;
    }
}
