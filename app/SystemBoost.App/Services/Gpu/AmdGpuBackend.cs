// AMD Radeon through ADLX, AMD's official SDK, which lives inside the elevated helper (native/helper): the app never
// loads amdadlx64.dll itself and never opens a game. Reads (amd.info, amd.metrics, amd.profile.get) may go through
// the plain helper, which needs no administrator prompt; every write goes through the elevated one.
// Every write is saved in boost-state.json first (amdoc before a tuning, amd3d before the game profile) and the record
// is taken out again when the write fails. What is not yet tried on a real Radeon PC says so where the user reads it.
//   Tuning   power limit %, GPU max clock, VRAM max clock and ADLX auto tuning, only while the app runs: the helper
//            puts the previous values back when '유지' is not pressed in time, and the factory values on heat, a failed
//            temperature read or a driver fault. A crash or forced kill is put back at the next start, never applied again.
//   Profile  Anti-Lag on and Radeon Chill off while a game runs (ADLX 3D settings are global, not per game).
// A helper built without ADLX (status "off") changes nothing: every feature shows AmdProtocol.OffText, and a record
// left by a build with ADLX is kept, never retried with a prompt, and the user is told how to put the card back.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

/// boost-state.json as the GPU code sees it: the records, and a way to put them on disk.
internal interface IGpuJournal
{
    List<BoostBackup> Backups { get; }
    void Save();
}

/// What the AMD code needs from the app around it. The defaults fit the tests.
internal sealed class AmdHost
{
    public Func<bool> DllPresent { get; init; } = () => true;
    public Func<bool> IsLaptop { get; init; } = () => false;
    public Func<bool> TuningAllowed { get; init; } = () => true;
    public string TuningPlan { get; init; } = "울트라";
    public Func<AmdGpuChoice> Choice { get; init; } = () => new AmdGpuChoice();
    /// A consent to the current overclocking notice is on record for the card of this name (OcConsent.Valid).
    /// No consent unless the app or a test says so.
    public Func<string, bool> Consented { get; init; } = _ => false;
    public Action<string> Log { get; init; } = _ => { };
    public Action<string> Notice { get; init; } = _ => { };
    /// Toggles or the list below them changed.
    public Action Changed { get; init; } = () => { };
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
}

/// Dropped: the card or driver is gone, and the tuning with it: only the record was removed.
/// Unavailable: this helper has no ADLX (status "off"), so the record is kept for a build that has it.
internal enum AmdPutBack { Nothing, AlreadyBack, Reverted, Failed, Dropped, Unavailable }

internal sealed class AmdGpuBackend : IGpuVendorBackend
{
    public const int ConfirmSeconds = 25;   // the helper gives up at 30
    public const string Untested = "아직 실제 라데온 PC에서 시험하지 않음";

    private readonly IHelperChannel _read, _admin;
    private readonly IGpuJournal _journal;
    private readonly AmdHost _host;
    private AmdInfo? _info;
    private int _index = -1;
    private int _limitC = AmdGpuChoice.DefaultTempLimitC;
    private string? _revertNote;

    public AmdGpuBackend(IHelperChannel read, IHelperChannel admin, IGpuJournal journal, AmdHost host)
    {
        _read = read;
        _admin = admin;
        _journal = journal;
        _host = host;
    }

    public GpuVendor Vendor => GpuVendor.Amd;
    public string Name => Gpu?.Name ?? "";
    /// The last amd.info read, null before the first.
    public AmdInfo? Info => _info;
    /// The card the features are for, from the last read.
    public AmdGpuInfo? Gpu => _info?.Gpus.FirstOrDefault(g => g.Index == _index) ?? (_info is { } i ? AmdProtocol.Pick(i.Gpus) : null);
    /// When the 25-second confirm window ends; null when nothing waits for '유지'.
    public DateTime? ConfirmBy { get; private set; }
    public int TempC { get; private set; } = -1;
    public bool Tuned => _journal.Backups.Any(b => b.Kind == AmdProtocol.TuneKind);
    /// The last amd.info came from a helper built without ADLX.
    public bool Off => _info?.Status == "off";

    /// Uses this ADLX index from now on (GpuSelection picked it).
    public void Select(int index) => _index = index;

    /// What the last game-profile revert left as it was because someone changed it during the game, once.
    public string? TakeRevertNote()
    {
        var n = _revertNote;
        _revertNote = null;
        return n;
    }

    private static bool Index(string text, out int gpu) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out gpu) && gpu is >= 0 and <= 7;

    // ADLX found no card, or the driver is gone: tuning and 3D settings went with it, so a record has nothing to put back.
    private static bool CardGone(AmdInfo info) => info.Status is "no_dll" or "no_gpu";

    // ------------------------------------------------------------ reads
    // The elevated helper when it runs anyway, otherwise the plain one, which never asks for administrator rights.
    private async Task<(IHelperChannel? channel, string? error)> ReaderAsync()
    {
        if (_admin.Running) return (_admin, null);
        var e = await _read.EnsureAsync();
        return e == null ? (_read, null) : (null, e);
    }

    /// amd.info, fresh. Starts no helper when amdadlx64.dll is not in System32.
    public async Task<AmdInfo> ReadInfoAsync()
    {
        if (!_host.DllPresent()) return _info = AmdInfo.Of("no_dll");
        var (ch, e) = await ReaderAsync();
        if (ch == null) return AmdInfo.Of("no_helper", e ?? "");   // not kept: the next look tries again
        var (ok, data) = await ch.CallAsync(AmdProtocol.Info());
        return _info = ok ? AmdProtocol.ParseInfo(data) : AmdInfo.Of("bad_reply", data);
    }

    public async Task<bool> DetectAsync() => (await ReadInfoAsync()).Gpus.Count > 0;

    public GpuCapabilities GetCapabilities()
    {
        if (Gpu is { } g) return AmdProtocol.Capabilities(g, _host.IsLaptop());
        var why = Off ? AmdProtocol.OffText : GpuCapabilities.NotOnThisCard;
        return new GpuCapabilities { GameProfileNote = why, TuningNote = why };
    }

    public async Task<GpuTelemetry?> ReadTelemetryAsync()
    {
        if (Gpu is not { } g) return null;
        var (ch, _) = await ReaderAsync();
        if (ch == null) return null;
        var (ok, data) = await ch.CallAsync(AmdProtocol.Metrics(g.Index));
        return ok ? AmdProtocol.ParseMetrics(data) : null;
    }

    /// Anti-Lag, Chill, Boost and Fluid Motion Frames as the driver has them now.
    public async Task<AmdProfileState?> ReadProfileAsync()
    {
        if (Gpu is not { } g) return null;
        var (ch, _) = await ReaderAsync();
        if (ch == null) return null;
        var (ok, data) = await ch.CallAsync(AmdProtocol.ProfileGet(g.Index));
        return ok ? AmdProtocol.ParseProfile(data) : null;
    }

    private async Task<AmdProfileState?> ProfileAsync(IHelperChannel ch, int gpu)
    {
        var (ok, data) = await ch.CallAsync(AmdProtocol.ProfileGet(gpu));
        return ok ? AmdProtocol.ParseProfile(data) : null;
    }

    // ------------------------------------------------------------ game profile
    public async Task<string> ApplyGameProfileAsync(string? game)
    {
        var info = await ReadInfoAsync();
        if (AmdProtocol.InfoProblem(info) is { } problem) return problem + AmdProtocol.Unchanged;
        var g = Gpu!;
        if (!g.AntiLag.Ok && !g.Chill.Ok) return GpuCapabilities.NotOnThisCard + AmdProtocol.Unchanged;
        var e = await _admin.EnsureAsync();
        if (e != null) return e;
        var now = await ProfileAsync(_admin, g.Index);
        if (now == null) return "AMD 3D 설정을 읽지 못했어요" + AmdProtocol.Unchanged;
        var earlier = _journal.Backups.FirstOrDefault(b => b.Kind == AmdProtocol.ProfileKind);
        if (earlier != null)
        {
            var rec = AmdProtocol.DecodeProfile(earlier.Value);
            if (rec != null && AmdProtocol.CheckProfile(rec, now) == AmdProfileCheck.StillSet) return "이미 적용돼 있어요 · " + Describe(now);
            // the helper or the user put it back meanwhile: that record is finished first, so its old values are not lost
            if (rec != null && await RevertGameProfileAsync(earlier) is { } failed) return "앞서 바꾼 AMD 3D 설정을 되돌리지 못했어요: " + failed;
            _journal.Backups.Remove(earlier);
            _journal.Save();
            now = await ProfileAsync(_admin, g.Index);
            if (now == null) return "AMD 3D 설정을 읽지 못했어요" + AmdProtocol.Unchanged;
        }
        var plan = AmdProtocol.PlanGameProfile(now);
        if (plan.Empty) return Describe(now) + AmdProtocol.Unchanged;
        var record = new BoostBackup
        {
            Feature = AmdProtocol.ProfileFeature, Kind = AmdProtocol.ProfileKind, Target = game ?? "",
            Key = g.Index.ToString(CultureInfo.InvariantCulture), Value = AmdProtocol.Encode(plan),
        };
        _journal.Backups.Add(record);
        _journal.Save();   // before anything changes, so a crash can still find its way back
        var (ok, data) = await _admin.CallAsync(AmdProtocol.ProfileApply(g.Index, plan));
        if (!ok)
        {
            _journal.Backups.Remove(record);
            _journal.Save();
            return "적용하지 못했어요: " + AmdProtocol.ErrorText(data) + AmdProtocol.Unchanged;
        }
        var after = data.Split(';');
        var done = new List<string>();
        if (plan.AntiLagSet != '-') done.Add(after.Length > 0 && after[0] == "1" ? "Anti-Lag를 켰어요" : "Anti-Lag를 켜지 못했어요");
        if (plan.ChillSet != '-') done.Add(after.Length > 1 && after[1] == "0" ? "Radeon Chill을 껐어요" : "Radeon Chill을 끄지 못했어요");
        if (now.Boost != '-') done.Add($"Radeon Boost {AmdProtocol.SwitchText(now.Boost)} (바꾸지 않아요)");
        return string.Join(" · ", done);
    }

    private static string Describe(AmdProfileState s)
    {
        var parts = new List<string>();
        if (s.AntiLag != '-') parts.Add($"Anti-Lag {AmdProtocol.SwitchText(s.AntiLag)}");
        if (s.Chill != '-') parts.Add($"Radeon Chill {AmdProtocol.SwitchText(s.Chill)}");
        if (s.Boost != '-') parts.Add($"Radeon Boost {AmdProtocol.SwitchText(s.Boost)}");
        return string.Join(" · ", parts);
    }

    public async Task<string?> RevertGameProfileAsync(BoostBackup record)
    {
        if (AmdProtocol.DecodeProfile(record.Value) is not { } plan || !Index(record.Key, out var gpu))
            return "AMD 3D 설정 백업 값이 잘못됐어요";
        var (ok, data) = await _admin.CallAsync(AmdProtocol.ProfileRevert(gpu, plan));
        if (!ok)
        {
            if (data == "off") return AmdProtocol.OffText;   // kept for a build with ADLX
            if (data != "no_amd") return "amd.profile.revert: " + data;
            _host.Log("AMD 그래픽카드나 드라이버가 없어져서 3D 설정 기록만 지웠어요");   // the settings went with the driver
            return null;
        }
        var changed = AmdProtocol.ParseChanged(data);
        if (changed.Count > 0)
        {
            _revertNote = $"{string.Join(", ", changed.Select(AmdProtocol.ProfileName))}은(는) 게임 중에 다른 값으로 바뀌어 있어서 그대로 뒀어요";
            _host.Log(_revertNote);
        }
        return null;
    }

    // ------------------------------------------------------------ tuning
    public Task<string> ApplyTuningAsync() => TuneAsync(null);

    public Task<string> ApplyAutoTuningAsync(GpuAutoTuning kind) => TuneAsync(kind);

    // auto null: the values chosen on the GPU page.
    private async Task<string> TuneAsync(GpuAutoTuning? auto)
    {
        if (!_host.TuningAllowed()) return $"{_host.TuningPlan} 기능이에요" + AmdProtocol.Unchanged;
        if (Tuned) return "이미 적용 중이에요. 위 스위치를 끈 뒤 다시 해 주세요" + AmdProtocol.Unchanged;
        var c = _host.Choice();
        // first a look without the administrator prompt, then the same checks again on a fresh read right before writing
        var (_, problem) = Prepare(await ReadInfoAsync(), auto, c);
        if (problem != null) return problem;
        var e = await _admin.EnsureAsync();
        if (e != null) return e;
        var (target, again) = Prepare(await ReadInfoAsync(), auto, c);
        if (again != null) return again;
        var g = Gpu!;
        // the helper runs auto tuning with the default limit; the manual values with the one picked on the page
        int limit = auto != null ? AmdGpuChoice.DefaultTempLimitC : Math.Clamp(c.TempLimitC, AmdGpuChoice.MinTempLimitC, AmdGpuChoice.MaxTempLimitC);
        // auto and factory decide how it goes back (AmdProtocol.RevertFor): from factory values with a reset
        var saved = new AmdTuningRecord(AmdProtocol.Current(g), auto != null, g.AtFactory);
        var record = new BoostBackup
        {
            Feature = AmdProtocol.TuneFeature, Kind = AmdProtocol.TuneKind,
            Target = g.Index.ToString(CultureInfo.InvariantCulture), Name = g.Name,
            Value = AmdProtocol.Encode(saved),
        };
        _journal.Backups.Add(record);
        _journal.Save();   // on disk before anything changes, so a crash can still find its way back
        var (ok, data) = await _admin.CallAsync(auto is { } a ? AmdProtocol.AutoApply(g.Index, a) : AmdProtocol.TuningApply(g.Index, target, limit));
        if (!ok)
        {
            _journal.Backups.Remove(record);
            _journal.Save();
            return "적용하지 못해서 원래 값으로 뒀어요: " + AmdProtocol.ErrorText(data);
        }
        // the helper read the card again right before writing: its values are the ones to go back to
        if (AmdProtocol.DecodeTuning(data) is { Empty: false } before)
        {
            record.Value = AmdProtocol.Encode(saved with { Values = Keep(before, saved.Values) });
            _journal.Save();
        }
        _limitC = limit;
        ConfirmBy = _host.UtcNow().AddSeconds(ConfirmSeconds);
        _host.Log(auto is { } k ? $"AMD 자동 튜닝 적용: {AutoTitle(k)}" : $"AMD GPU 설정 적용: {DescribeTargets(target)} · 제한 온도 {limit} °C");
        return $"적용했어요. {ConfirmSeconds}초 안에 아래 '유지'를 누르지 않으면 원래대로 돌아가요";
    }

    // A value the helper could not report stays as read before.
    private static AmdTuningValues Keep(AmdTuningValues helper, AmdTuningValues ours) =>
        new(helper.PowerPct ?? ours.PowerPct, helper.GpuMaxMHz ?? ours.GpuMaxMHz, helper.VramMaxMHz ?? ours.VramMaxMHz);

    // The checks before a tuning write. Returns the values to send, or the text to show (nothing changed).
    private (AmdTuningValues target, string? problem) Prepare(AmdInfo info, GpuAutoTuning? auto, AmdGpuChoice c)
    {
        var none = new AmdTuningValues(null, null, null);
        if (AmdProtocol.InfoProblem(info) is { } p) return (none, p + AmdProtocol.Unchanged);
        var g = Gpu!;
        var caps = AmdProtocol.Capabilities(g, _host.IsLaptop());
        if (caps.TuningNote == GpuCapabilities.DesktopOnly) return (none, "데스크톱 그래픽카드 전용이에요" + AmdProtocol.Unchanged);
        if (caps.TuningNote != null) return (none, caps.TuningNote + AmdProtocol.Unchanged);
        var target = none;
        if (auto is { } a)
        {
            if (!caps.Auto(a)) return (none, GpuCapabilities.NotOnThisCard + AmdProtocol.Unchanged);
        }
        else
        {
            target = AmdProtocol.Targets(g, c);
            if (target.Empty) return (none, "아래에서 값을 바꾼 뒤 켜 주세요" + AmdProtocol.Unchanged);
            if (AmdProtocol.Check(g, target) is { } bad) return (none, AmdProtocol.ErrorText(bad) + AmdProtocol.Unchanged);
        }
        // lowering can make a card unstable too (an undervolt is a lowering), so every AMD tuning needs the notice agreed
        // to for this card; checked before the administrator prompt
        if (!_host.Consented(g.Name)) return (none, OverclockNotice.NotAgreed + AmdProtocol.Unchanged);
        return (target, null);
    }

    public static string AutoTitle(GpuAutoTuning k) => k switch
    {
        GpuAutoTuning.Undervolt => "GPU 언더볼트",
        GpuAutoTuning.OverclockGpu => "GPU 오버클럭",
        _ => "VRAM 오버클럭",
    };

    private static string DescribeTargets(AmdTuningValues v)
    {
        var parts = new List<string>();
        if (v.PowerPct is int p) parts.Add($"전력 제한 {p:+0;-0;0}%");
        if (v.GpuMaxMHz is int g) parts.Add($"GPU 최대 {g} MHz");
        if (v.VramMaxMHz is int m) parts.Add($"VRAM 최대 {m} MHz");
        return string.Join(" · ", parts);
    }

    public async Task<string> KeepTuningAsync()
    {
        var (ok, _) = await _admin.CallAsync(AmdProtocol.TuningKeep());
        ConfirmBy = null;
        return ok ? "유지할게요. 앱을 끄면 원래대로 돌아가요" : "이미 원래대로 돌아갔어요";
    }

    public async Task<string?> RevertTuningAsync(BoostBackup record)
    {
        ConfirmBy = null;
        if (AmdProtocol.DecodeRecord(record.Value) is not { } saved || !Index(record.Target, out var gpu))
            return "GPU 백업 값이 잘못됐어요";
        // an auto tuning from factory values: amd.tuning.reset; otherwise the three values (an undervolt's voltage may stay)
        var (ok, data) = await _admin.CallAsync(AmdProtocol.RevertFor(gpu, saved));
        if (ok) return null;
        if (data == "off") return AmdProtocol.OffText;   // kept for a build with ADLX
        if (data == "no_amd")
        {
            _host.Log("AMD 그래픽카드나 드라이버가 없어져서 GPU 설정 기록만 지웠어요");   // tuning goes with the driver
            return null;
        }
        return "amd.tuning.revert: " + data;
    }

    public async Task<string> ResetToFactoryAsync()
    {
        var info = await ReadInfoAsync();
        if (AmdProtocol.InfoProblem(info) is { } problem) return problem + AmdProtocol.Unchanged;
        var caps = GetCapabilities();
        if (!caps.ResetToFactory) return (caps.TuningNote == GpuCapabilities.DesktopOnly ? "데스크톱 그래픽카드 전용이에요" : caps.TuningNote ?? GpuCapabilities.NotOnThisCard) + AmdProtocol.Unchanged;
        var e = await _admin.EnsureAsync();
        if (e != null) return e;
        var (ok, data) = await _admin.CallAsync(AmdProtocol.TuningReset(Gpu!.Index));
        if (!ok) return "공장 초기값으로 돌리지 못했어요: " + AmdProtocol.ErrorText(data);
        ConfirmBy = null;
        // the card is at its factory values now: a record of earlier values has nothing left to put back
        if (_journal.Backups.RemoveAll(b => b.Kind == AmdProtocol.TuneKind) > 0) _journal.Save();
        _host.Log("AMD 그래픽카드를 공장 초기값으로 돌렸어요");
        _host.Changed();
        return "공장 초기값으로 돌렸어요";
    }

    public async Task WatchTuningAsync()
    {
        if (!Tuned || Off) return;   // off: nothing of this run, and the record waits for a build with ADLX
        var (ok, data) = await _admin.CallAsync(AmdProtocol.TuningStatus());
        var (action, status) = AmdProtocol.Watch(ok, data);
        switch (action)
        {
            case AmdWatchAction.Running:
                TempC = status!.TempC;
                if (status.Confirmed) ConfirmBy = null;
                return;
            case AmdWatchAction.Tripped:
                ConfirmBy = null;
                _journal.Backups.RemoveAll(b => b.Kind == AmdProtocol.TuneKind);
                _journal.Save();
                var line = AmdProtocol.TripText(status!.Tripped, _limitC);
                _host.Log(line);
                _host.Notice(line);
                _host.Changed();
                return;
            default:
                await PutBackAsync();   // the helper is gone, or a new one never had this tuning
                return;
        }
    }

    /// After a crash, or when the helper is gone: the values of the record go back. Nothing to do if the card is back already.
    public async Task<AmdPutBack> PutBackAsync()
    {
        var saved = _journal.Backups.Where(b => b.Kind == AmdProtocol.TuneKind).ToList();
        if (saved.Count == 0) return AmdPutBack.Nothing;
        var info = await ReadInfoAsync();
        if (info.Status == "off")
        {
            ConfirmBy = null;
            _host.Log("이 버전에는 AMD 드라이버 기능이 없어서 GPU 설정을 되돌리지 못했어요. 기록은 남겨 둬요");
            return AmdPutBack.Unavailable;   // no prompt: this helper could not do it anyway
        }
        if (CardGone(info))
        {
            ConfirmBy = null;
            _journal.Backups.RemoveAll(b => b.Kind == AmdProtocol.TuneKind);
            _journal.Save();
            _host.Log("AMD 그래픽카드나 드라이버가 없어져서 GPU 설정 기록만 지웠어요");
            _host.Changed();
            return AmdPutBack.Dropped;   // the log says so; nothing was put back, so no notice
        }
        bool back = saved.All(b => AmdProtocol.DecodeRecord(b.Value) is { } v && Index(b.Target, out var i) &&
                                   info.Gpus.FirstOrDefault(g => g.Index == i) is { } now && AmdProtocol.AlreadyBack(v, now));
        if (back)
        {
            ConfirmBy = null;
            _journal.Backups.RemoveAll(b => b.Kind == AmdProtocol.TuneKind);
            _journal.Save();
            _host.Changed();
            return AmdPutBack.AlreadyBack;
        }
        var e = await _admin.EnsureAsync();
        if (e == null)
        {
            foreach (var b in Enumerable.Reverse(saved))
            {
                var failed = await RevertTuningAsync(b);
                if (failed == null) _journal.Backups.Remove(b);
                else e = failed;
            }
        }
        _host.Log(e == null ? "GPU 설정을 원래대로 돌렸어요" : "GPU 설정을 되돌리지 못했어요: " + e);
        _journal.Save();
        _host.Changed();
        return e == null ? AmdPutBack.Reverted : AmdPutBack.Failed;
    }

    // ------------------------------------------------------------ the next start after a crash
    /// Tuning left by a run that ended without putting it back (a crash, a forced kill, a power cut): it goes back,
    /// and is never applied again; the user is told and can apply it again. Asks for administrator rights only the
    /// way the NVIDIA tuning does (when the card does not hold the earlier values any more).
    public async Task RecoverTuningAsync()
    {
        if (!Tuned) return;
        var r = await PutBackAsync();
        var line = r switch
        {
            AmdPutBack.Reverted or AmdPutBack.AlreadyBack =>
                "SystemBoost가 갑자기 꺼져서 AMD 그래픽카드 설정(전력·클럭)을 원래대로 돌렸어요. 다시 쓰려면 GPU 화면에서 직접 다시 적용해 주세요",
            AmdPutBack.Unavailable =>
                "지난번에 바꾼 AMD 그래픽카드 설정(전력·클럭)을 이 버전에서는 되돌릴 수 없어요. AMD Software의 튜닝 화면에서 초기화해 주세요",
            _ => null,
        };
        if (line != null)
        {
            _host.Log(line);
            _host.Notice(line);
        }
    }

    /// A game profile left by a run that ended during a game. The helper puts it back as it exits, so usually only the
    /// record is left. Never asks for administrator rights here: what still holds waits for the elevated helper
    /// (the end of the next game, '전체 되돌리기', or the uninstaller's question).
    public async Task RecoverProfileAsync()
    {
        var records = _journal.Backups.Where(b => b.Kind == AmdProtocol.ProfileKind).ToList();
        if (records.Count == 0) return;
        bool changed = false, left = false, off = false;
        bool gone = !_host.DllPresent();
        foreach (var b in records)
        {
            if (gone || AmdProtocol.DecodeProfile(b.Value) is not { } plan || !Index(b.Key, out var gpu))
            {
                _journal.Backups.Remove(b);   // nothing in it could ever be put back
                changed = true;
                continue;
            }
            AmdProfileState? now = null;
            var ch = _admin.Running ? _admin : await _read.EnsureAsync() == null ? _read : null;
            if (ch != null)
            {
                var (ok, data) = await ch.CallAsync(AmdProtocol.ProfileGet(gpu));
                if (ok) now = AmdProtocol.ParseProfile(data);
                else if (data == "off") { off = true; continue; }   // kept for a build with ADLX
            }
            var where = now == null ? AmdProfileCheck.StillSet : AmdProtocol.CheckProfile(plan, now);
            if (where != AmdProfileCheck.StillSet)
            {
                _journal.Backups.Remove(b);   // back already, or changed since by someone: nothing of ours is left
                changed = true;
                continue;
            }
            if (_admin.Running && await RevertGameProfileAsync(b) == null)
            {
                _journal.Backups.Remove(b);
                changed = true;
                _host.Log("SystemBoost가 갑자기 꺼져서 남아 있던 AMD 3D 설정(Anti-Lag·Radeon Chill)을 원래대로 돌렸어요");
                continue;
            }
            left = true;
        }
        if (changed) _journal.Save();
        if (off)
        {
            var line = "지난번 게임 중에 바꾼 AMD 3D 설정(Anti-Lag·Radeon Chill)을 이 버전에서는 되돌릴 수 없어요. AMD Software에서 직접 확인해 주세요";
            _host.Log(line);
            _host.Notice(line);
        }
        else if (left)
        {
            var line = "지난번 게임 중에 바꾼 AMD 3D 설정(Anti-Lag·Radeon Chill)이 남아 있어요. 다음 게임이 끝날 때나 '백업 · 되돌리기'에서 원래대로 돌려요";
            _host.Log(line);
            _host.Notice(line);
        }
    }
}
