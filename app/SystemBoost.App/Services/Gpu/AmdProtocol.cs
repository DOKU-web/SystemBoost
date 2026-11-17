// The AMD half of the helper protocol (native/helper, ADLX inside sb_helper.exe): replies parsed, commands built,
// journal values written and read back, and the decisions that need no window or helper (limits, watch, recovery).
// Everything here is plain logic, checked by app/SystemBoost.Gpu.Tests.
//   amd.info                          ok\t{json}                read-only, plain helper too
//   amd.metrics <gpu>                 ok\tt;hot;gpu;vram;W;use;vramMB (-1 = not reported)
//   amd.profile.get <gpu>             ok\tantiLag;chill;boost;afmf (1, 0 or - = not supported)
//   amd.profile.apply <gpu> <a> <c>   ok\ta;c                   admin
//   amd.profile.revert <gpu> <aOld> <cOld> <aSet> <cSet>   ok\t or ok\tchanged=antiLag,chill
//   amd.tuning.apply <gpu> <pct|-> <gpuMax|-> <vramMax|-> <tempC>   ok\toldPct;oldGpuMax;oldVramMax
//   amd.auto.apply <gpu> <undervolt|ocgpu|ocvram>          ok\toldPct;oldGpuMax;oldVramMax
//   amd.tuning.keep / amd.tuning.status (active;confirmed;tempC;tripped) / amd.tuning.revert / amd.tuning.reset
// Error words: no_amd, old_driver, unsupported, range, step, busy, failed, refused; off from a helper built without
// ADLX (CMake SB_ADLX=OFF, the default while the owner decides on AMD's licence): amd.info answers status "off".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SystemBoost.App.Services;

/// One ADLX tuning value: percent (power) or MHz (clocks). Ok false = the card does not support it.
public sealed record AmdRange(bool Ok, int Min, int Max, int Step, int Now)
{
    public static readonly AmdRange None = new(false, 0, 0, 1, 0);
}

public sealed record AmdSwitch(bool Ok, bool On)
{
    public static readonly AmdSwitch None = new(false, false);
}

public sealed record AmdGpuInfo(int Index, string Name, bool Integrated, bool AtFactory,
                                AmdRange Power, AmdRange GpuMax, AmdRange VramMax,
                                bool AutoUndervolt, bool AutoOverclockGpu, bool AutoOverclockVram,
                                AmdSwitch AntiLag, AmdSwitch Chill, AmdSwitch Boost, AmdSwitch Afmf);

/// Status: ok, no_dll, old_driver, no_gpu, failed, off (from the helper; off = built without ADLX);
/// no_helper and bad_reply (from the app).
public sealed record AmdInfo(string Status, string Adlx, string Driver, IReadOnlyList<AmdGpuInfo> Gpus, string Detail = "")
{
    public static AmdInfo Of(string status, string detail = "") => new(status, "", "", Array.Empty<AmdGpuInfo>(), detail);
}

/// '1' on, '0' off, '-' not supported.
public sealed record AmdProfileState(char AntiLag, char Chill, char Boost, char Afmf);

/// amd.tuning.status. Tripped: 0, temp, fault, readfail or timeout.
public sealed record AmdTuningStatus(bool Active, bool Confirmed, int TempC, string Tripped);

/// The values of one tuning record ("pct;gpuMax;vramMax", '-' = left alone).
public sealed record AmdTuningValues(int? PowerPct, int? GpuMaxMHz, int? VramMaxMHz)
{
    public bool Empty => PowerPct == null && GpuMaxMHz == null && VramMaxMHz == null;
}

/// One amdoc record: "pct;gpuMax;vramMax;auto;factory". Auto: written by amd.auto.apply. Factory: amd.info said
/// "factory": true just before. Records from before these two fields have three fields (manual, factory unknown).
public sealed record AmdTuningRecord(AmdTuningValues Values, bool Auto, bool Factory);

/// The game profile record: what each setting was and what SystemBoost set ('-' = not touched).
public sealed record AmdProfilePlan(char AntiLagOld, char ChillOld, char AntiLagSet, char ChillSet)
{
    public bool Empty => AntiLagSet == '-' && ChillSet == '-';
}

/// What was picked on the GPU page for an AMD card. Null = leave the value on the card.
public sealed class AmdGpuChoice
{
    public const int DefaultTempLimitC = 83, MinTempLimitC = 60, MaxTempLimitC = 90;
    public int? PowerPct { get; set; }
    public int? GpuMaxMHz { get; set; }
    public int? VramMaxMHz { get; set; }
    public int TempLimitC { get; set; } = DefaultTempLimitC;
    public bool RiskAccepted { get; set; }
    /// Same meaning as GpuChoice.RiskTerms: the wording the tick answers (2 = from 0.8.13).
    public int RiskTerms { get; set; }
}

public enum AmdWatchAction { Running, Tripped, PutBack }

public enum AmdProfileCheck { StillSet, AlreadyBack, ChangedByUser }

public static class AmdProtocol
{
    public const string TuneKind = "amdoc", ProfileKind = "amd3d";
    public const string TuneFeature = "gpu.oc", ProfileFeature = "gpu.driver";
    /// Biggest raise in one apply above the value on the card now; lowering is always allowed (within range).
    public const int MaxPowerRaisePct = 5, MaxGpuRaiseMHz = 100, MaxVramRaiseMHz = 50;
    public const string Unchanged = " (바뀐 것 없음)";
    /// The helper was built without ADLX: no AMD driver features in this build (not the user's driver).
    public const string OffText = "AMD 드라이버 기능은 아직 준비 중이에요";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(int v) => v.ToString(Inv);
    private static string N(int? v) => v is int x ? N(x) : "-";

    // ------------------------------------------------------------ replies
    public static AmdInfo ParseInfo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return AmdInfo.Of("bad_reply");
            var status = Str(root, "status");
            if (status.Length == 0) return AmdInfo.Of("bad_reply");
            var gpus = new List<AmdGpuInfo>();
            if (status == "ok" && root.TryGetProperty("gpus", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in list.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.Object || !g.TryGetProperty("i", out var i) || !i.TryGetInt32(out var index)) continue;
                    if (index < 0 || index > 7) continue;
                    var auto = g.TryGetProperty("auto", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
                    gpus.Add(new AmdGpuInfo(index, Str(g, "name"), Bool(g, "integrated"), Bool(g, "factory"),
                                            Range(g, "power"), Range(g, "gpuMax"), Range(g, "vramMax"),
                                            Bool(auto, "undervolt"), Bool(auto, "ocGpu"), Bool(auto, "ocVram"),
                                            Switch(g, "antiLag"), Switch(g, "chill"), Switch(g, "boost"), Switch(g, "afmf")));
                }
            }
            return new AmdInfo(status, Str(root, "adlx"), Str(root, "driver"), gpus);
        }
        catch (JsonException) { return AmdInfo.Of("bad_reply"); }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    // A section with "ok":false (or any missing number) counts as not supported.
    private static AmdRange Range(JsonElement g, string name)
    {
        if (!g.TryGetProperty(name, out var r) || r.ValueKind != JsonValueKind.Object || !Bool(r, "ok")) return AmdRange.None;
        if (Int(r, "min") is not int min || Int(r, "max") is not int max || Int(r, "now") is not int now || min > max) return AmdRange.None;
        int step = Int(r, "step") is int s && s > 0 ? s : 1;
        return new AmdRange(true, min, max, step, now);
    }

    private static AmdSwitch Switch(JsonElement g, string name) =>
        g.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.Object && Bool(s, "ok") ? new AmdSwitch(true, Bool(s, "on")) : AmdSwitch.None;

    public static GpuTelemetry? ParseMetrics(string data)
    {
        var f = data.Split(';');
        if (f.Length != 7) return null;
        var v = new int[7];
        for (int i = 0; i < 7; i++)
            if (!int.TryParse(f[i], NumberStyles.AllowLeadingSign, Inv, out v[i])) return null;
        return new GpuTelemetry(v[0], v[1], v[2], v[3], v[4], v[5], v[6]);
    }

    public static AmdProfileState? ParseProfile(string data)
    {
        var f = data.Split(';');
        if (f.Length != 4 || f.Any(x => x is not ("0" or "1" or "-"))) return null;
        return new AmdProfileState(f[0][0], f[1][0], f[2][0], f[3][0]);
    }

    public static AmdTuningStatus? ParseStatus(string data)
    {
        var f = data.Split(';');
        if (f.Length != 4 || f[0] is not ("0" or "1") || f[1] is not ("0" or "1")) return null;
        if (!int.TryParse(f[2], NumberStyles.AllowLeadingSign, Inv, out var temp)) return null;
        if (f[3] is not ("0" or "temp" or "fault" or "readfail" or "timeout")) return null;
        return new AmdTuningStatus(f[0] == "1", f[1] == "1", temp, f[3]);
    }

    /// "changed=antiLag,chill" -> the settings someone else changed during the game, which were left as they were.
    public static IReadOnlyList<string> ParseChanged(string data)
    {
        const string head = "changed=";
        if (!data.StartsWith(head, StringComparison.Ordinal)) return Array.Empty<string>();
        return data[head.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // ------------------------------------------------------------ commands
    public static string[] Info() => new[] { "amd.info" };
    public static string[] Metrics(int gpu) => new[] { "amd.metrics", N(gpu) };
    public static string[] ProfileGet(int gpu) => new[] { "amd.profile.get", N(gpu) };
    public static string[] ProfileApply(int gpu, AmdProfilePlan p) => new[] { "amd.profile.apply", N(gpu), p.AntiLagSet.ToString(), p.ChillSet.ToString() };
    public static string[] ProfileRevert(int gpu, AmdProfilePlan p) =>
        new[] { "amd.profile.revert", N(gpu), p.AntiLagOld.ToString(), p.ChillOld.ToString(), p.AntiLagSet.ToString(), p.ChillSet.ToString() };
    public static string[] TuningApply(int gpu, AmdTuningValues v, int tempLimitC) =>
        new[] { "amd.tuning.apply", N(gpu), N(v.PowerPct), N(v.GpuMaxMHz), N(v.VramMaxMHz), N(Math.Clamp(tempLimitC, AmdGpuChoice.MinTempLimitC, AmdGpuChoice.MaxTempLimitC)) };
    public static string[] AutoApply(int gpu, GpuAutoTuning kind) => new[] { "amd.auto.apply", N(gpu), AutoWord(kind) };
    public static string[] TuningKeep() => new[] { "amd.tuning.keep" };
    public static string[] TuningStatus() => new[] { "amd.tuning.status" };
    public static string[] TuningRevert(int gpu, AmdTuningValues v) => new[] { "amd.tuning.revert", N(gpu), N(v.PowerPct), N(v.GpuMaxMHz), N(v.VramMaxMHz) };
    public static string[] TuningReset(int gpu) => new[] { "amd.tuning.reset", N(gpu) };

    public static string AutoWord(GpuAutoTuning kind) => kind switch
    {
        GpuAutoTuning.Undervolt => "undervolt",
        GpuAutoTuning.OverclockGpu => "ocgpu",
        _ => "ocvram",
    };

    // ------------------------------------------------------------ journal values
    public static string Encode(AmdTuningValues v) => $"{N(v.PowerPct)};{N(v.GpuMaxMHz)};{N(v.VramMaxMHz)}";

    public static string Encode(AmdTuningRecord r) => $"{Encode(r.Values)};{(r.Auto ? 1 : 0)};{(r.Factory ? 1 : 0)}";

    /// The three values of a record, in either form.
    public static AmdTuningValues? DecodeTuning(string value) => DecodeRecord(value)?.Values;

    /// Five fields, or the three of records written before auto and factory were kept (read as manual, factory unknown).
    public static AmdTuningRecord? DecodeRecord(string value)
    {
        var f = value.Split(';');
        if (f.Length != 3 && f.Length != 5) return null;
        var n = new int?[3];
        for (int i = 0; i < 3; i++)
        {
            if (f[i] == "-") continue;
            if (!int.TryParse(f[i], NumberStyles.AllowLeadingSign, Inv, out var x)) return null;
            n[i] = x;
        }
        if (f.Length == 5 && (f[3] is not ("0" or "1") || f[4] is not ("0" or "1"))) return null;
        return new AmdTuningRecord(new AmdTuningValues(n[0], n[1], n[2]), f.Length == 5 && f[3] == "1", f.Length == 5 && f[4] == "1");
    }

    /// How a record goes back. amd.tuning.revert sets power and the two max clocks only; an auto tuning (an undervolt
    /// above all) changes the voltage too. Started from factory values, it goes back with amd.tuning.reset, which
    /// covers the voltage. Otherwise the three values go back and the voltage of an auto undervolt may stay as it is.
    public static string[] RevertFor(int gpu, AmdTuningRecord r) => r.Auto && r.Factory ? TuningReset(gpu) : TuningRevert(gpu, r.Values);

    public static string Encode(AmdProfilePlan p) => $"{p.AntiLagOld};{p.ChillOld};{p.AntiLagSet};{p.ChillSet}";

    public static AmdProfilePlan? DecodeProfile(string value)
    {
        var f = value.Split(';');
        if (f.Length != 4 || f.Any(x => x is not ("0" or "1" or "-"))) return null;
        return new AmdProfilePlan(f[0][0], f[1][0], f[2][0], f[3][0]);
    }

    /// The values on the card now, for the record saved before a write ('-' where the card cannot be tuned).
    public static AmdTuningValues Current(AmdGpuInfo g) =>
        new(g.Power.Ok ? g.Power.Now : null, g.GpuMax.Ok ? g.GpuMax.Now : null, g.VramMax.Ok ? g.VramMax.Now : null);

    // ------------------------------------------------------------ choosing a card and what it allows
    /// The AMD card the features are for: the first discrete one, otherwise the first one (an APU).
    public static AmdGpuInfo? Pick(IReadOnlyList<AmdGpuInfo> gpus) => gpus.FirstOrDefault(g => !g.Integrated) ?? gpus.FirstOrDefault();

    /// Laptops and APUs never get tuning, whatever ADLX says: their makers set power and clocks for the case and the cooling.
    public static GpuCapabilities Capabilities(AmdGpuInfo g, bool laptop)
    {
        bool blocked = laptop || g.Integrated;
        bool any = g.Power.Ok || g.GpuMax.Ok || g.VramMax.Ok || g.AutoUndervolt || g.AutoOverclockGpu || g.AutoOverclockVram;
        bool profile = g.AntiLag.Ok || g.Chill.Ok;
        return new GpuCapabilities
        {
            Telemetry = true,
            GameProfile = profile,
            GameProfileNote = profile ? null : GpuCapabilities.NotOnThisCard,
            PowerLimit = !blocked && g.Power.Ok,
            CoreClock = !blocked && g.GpuMax.Ok,
            MemClock = !blocked && g.VramMax.Ok,
            AutoUndervolt = !blocked && g.AutoUndervolt,
            AutoOverclockGpu = !blocked && g.AutoOverclockGpu,
            AutoOverclockVram = !blocked && g.AutoOverclockVram,
            ResetToFactory = !blocked && any,
            TuningNote = blocked ? GpuCapabilities.DesktopOnly : any ? null : GpuCapabilities.NotOnThisCard,
        };
    }

    // ------------------------------------------------------------ limits
    /// Null when target may be sent, else the helper's error word: unsupported, range or step.
    public static string? Check(AmdRange r, int? target, int maxRaise)
    {
        if (target is not int t) return null;
        if (!r.Ok) return "unsupported";
        if (t < r.Min || t > r.Max || (t - r.Min) % r.Step != 0) return "range";
        if (t > r.Now + maxRaise) return "step";
        return null;
    }

    public static string? Check(AmdGpuInfo g, AmdTuningValues v) =>
        Check(g.Power, v.PowerPct, MaxPowerRaisePct) ?? Check(g.GpuMax, v.GpuMaxMHz, MaxGpuRaiseMHz) ?? Check(g.VramMax, v.VramMaxMHz, MaxVramRaiseMHz);

    /// What the picker offers: the value on the card now, steps of pickStep (rounded up to the card's step) down to
    /// maxDrop below it and up to maxRaise above it, all inside the card's range.
    public static List<int> Choices(AmdRange r, int maxRaise, int pickStep, int maxDrop)
    {
        var list = new List<int>();
        if (!r.Ok) return list;
        int step = Math.Max(1, (pickStep + r.Step - 1) / r.Step * r.Step);
        int lo = Math.Max(r.Min, r.Now - maxDrop), hi = Math.Min(r.Max, r.Now + maxRaise);
        for (int v = r.Now - step; v >= lo; v -= step) list.Add(v);
        list.Reverse();
        list.Add(r.Now);
        for (int v = r.Now + step; v <= hi; v += step) list.Add(v);
        return list;
    }

    /// The chosen values that differ from the card; a value for something the card cannot tune is dropped.
    public static AmdTuningValues Targets(AmdGpuInfo g, AmdGpuChoice c)
    {
        int? Pick(AmdRange r, int? chosen) => r.Ok && chosen is int x && x != r.Now ? x : null;
        return new AmdTuningValues(Pick(g.Power, c.PowerPct), Pick(g.GpuMax, c.GpuMaxMHz), Pick(g.VramMax, c.VramMaxMHz));
    }

    /// The card still holds the values of the record: nothing to put back.
    public static bool AlreadyBack(AmdTuningValues saved, AmdGpuInfo now) =>
        Same(saved.PowerPct, now.Power) && Same(saved.GpuMaxMHz, now.GpuMax) && Same(saved.VramMaxMHz, now.VramMax);

    /// An auto tuning started from factory values is back only when the driver says factory again (the voltage too).
    public static bool AlreadyBack(AmdTuningRecord r, AmdGpuInfo now) => r.Auto && r.Factory ? now.AtFactory : AlreadyBack(r.Values, now);

    private static bool Same(int? saved, AmdRange r) => saved is not int s || (r.Ok && r.Now == s);

    // ------------------------------------------------------------ game profile
    /// During a game: Anti-Lag on and Radeon Chill off, only where supported and not already so.
    public static AmdProfilePlan PlanGameProfile(AmdProfileState now) => new(
        now.AntiLag == '0' ? '0' : '-', now.Chill == '1' ? '1' : '-',
        now.AntiLag == '0' ? '1' : '-', now.Chill == '1' ? '0' : '-');

    /// Where a recorded profile stands now: still as SystemBoost set it, back as it was, or changed by someone.
    public static AmdProfileCheck CheckProfile(AmdProfilePlan rec, AmdProfileState now)
    {
        bool set = true, back = true;
        foreach (var (old, wanted, cur) in new[] { (rec.AntiLagOld, rec.AntiLagSet, now.AntiLag), (rec.ChillOld, rec.ChillSet, now.Chill) })
        {
            if (wanted == '-') continue;
            if (cur != wanted) set = false;
            if (cur != old) back = false;
        }
        return set ? AmdProfileCheck.StillSet : back ? AmdProfileCheck.AlreadyBack : AmdProfileCheck.ChangedByUser;
    }

    // ------------------------------------------------------------ watch
    /// What the 2-second watch does with the reply to amd.tuning.status.
    /// A failed call means the helper is gone (it may have been killed before it could undo anything); an inactive
    /// tuning with no reason means this helper never had it (a new helper): both put the values back from the journal.
    public static (AmdWatchAction action, AmdTuningStatus? status) Watch(bool ok, string data)
    {
        var s = ok ? ParseStatus(data) : null;
        if (s == null) return (AmdWatchAction.PutBack, null);
        if (s.Active) return (AmdWatchAction.Running, s);
        return (s.Tripped == "0" ? AmdWatchAction.PutBack : AmdWatchAction.Tripped, s);
    }

    /// Why the helper put the card back. Heat, a failed temperature read and a driver fault go to the factory values.
    public static string TripText(string tripped, int limitC) => tripped switch
    {
        "temp" => $"GPU 온도가 제한 온도({N(limitC)} °C)에 닿아서 AMD 그래픽카드를 공장 초기값으로 돌렸어요",
        "fault" => "그래픽 드라이버 오류가 보여서 AMD 그래픽카드를 공장 초기값으로 돌렸어요",
        "readfail" => "GPU 온도를 읽지 못해서 AMD 그래픽카드를 공장 초기값으로 돌렸어요",
        "timeout" => "'유지'를 누르지 않아서 AMD 그래픽카드를 이전 값으로 돌렸어요",
        _ => "AMD 그래픽카드 설정을 원래대로 돌렸어요",
    };

    // ------------------------------------------------------------ texts
    /// The words the helper answers with, for the screen. Anything else is shown as it came.
    public static string ErrorText(string data) => data switch
    {
        "no_amd" => "AMD 그래픽카드나 AMD 드라이버를 찾지 못했어요",
        "old_driver" => "AMD 드라이버가 오래돼서 쓸 수 없어요. AMD Software에서 드라이버를 업데이트해 주세요",
        "unsupported" => GpuCapabilities.NotOnThisCard,
        "range" => "그래픽카드가 허용하는 범위를 벗어난 값이에요",
        "step" => $"한 번에 올릴 수 있는 값은 전력 +{MaxPowerRaisePct}%, GPU 최대 클럭 +{MaxGpuRaiseMHz} MHz, VRAM 최대 클럭 +{MaxVramRaiseMHz} MHz까지예요",
        "busy" => "앞서 적용한 값이 아직 확인되지 않았어요. '유지'를 누르거나 원래대로 돌아갈 때까지 기다려 주세요",
        "failed" => "AMD 드라이버가 값을 받지 않았어요",
        "refused" => "관리자 도우미가 이 요청을 받지 않았어요. 앱을 다시 설치해 주세요",
        "disconnected" or "not running" => "관리자 도우미와 연결이 끊겼어요",
        "off" => OffText,
        _ => data,
    };

    /// Why amd.info found nothing to work with; null when a card is there.
    public static string? InfoProblem(AmdInfo info) => info.Status switch
    {
        "ok" when info.Gpus.Count > 0 => null,
        "ok" or "no_gpu" => "AMD 그래픽카드를 찾지 못했어요",
        "no_dll" => "AMD 그래픽 드라이버를 찾지 못했어요. AMD Software(드라이버)를 설치해 주세요",
        "old_driver" => ErrorText("old_driver"),
        "off" => OffText,
        "no_helper" when info.Detail.Length > 0 => info.Detail.Replace(Unchanged, ""),
        _ => "AMD 드라이버에서 정보를 읽지 못했어요",
    };

    public static string SwitchText(char state) => state switch { '1' => "켜짐", '0' => "꺼짐", _ => "지원하지 않음" };

    public static string ProfileName(string setting) => setting switch
    {
        "antiLag" => "Anti-Lag",
        "chill" => "Radeon Chill",
        _ => setting,
    };
}
