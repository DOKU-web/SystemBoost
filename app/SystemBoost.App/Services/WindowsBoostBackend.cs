// Windows boost features. Every change saves the previous value first
// (boost-state.json) and can be undone one by one or all at once. A value goes back
// only while it still holds what was set here (RestoreRules); one the user changed since stays.
// The game process itself is never opened with a right that could change it, and never modified:
// games are recognised by name in the process list (and by path, read the way Task Manager reads it),
// and per-game settings go through Windows or the driver.
//   Stays until turned off   gpu.highperf, gpu.hags, gpu.driver, cpu.priority,
//                            net.adapterpower, sys.gamemode, sys.dvr
//   Only while a game runs   ram.standby, ram.workingset, cpu.ecores, cpu.power,
//                            net.monitor, sys.focus, boost.profiles
//   Only while the app runs  gpu.oc, the frame limit for frame generation (fg.limit)
//   Biz (WindowsBoostBackend.Biz.cs)  biz.stream and biz.work while their programs run; startup programs until turned back
// Admin-only work goes through sb_helper (BoostHelper.cs).
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

/// One saved value. Kind says how to put it back.
public sealed class BoostBackup
{
    public string Feature { get; set; } = "";
    public string Kind { get; set; } = "";     // hkcu, hags, ifeo, netpm, nv, nvlimit, power, wu, ecores, oc, startup
    public string Target { get; set; } = "";   // game path, exe name or adapter
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public bool Dword { get; set; }
    public string Value { get; set; } = "";
    public string ValueKind { get; set; } = "";   // hkcu: the registry type before (records from before this: Dword)
    public string SetTo { get; set; } = "";       // what SystemBoost wrote; "" in records from before this
}

public sealed class BoostState
{
    public List<string> Games { get; set; } = new();
    public List<BoostBackup> Backups { get; set; } = new();
    public List<string> Enabled { get; set; } = new();        // game-time features that are turned on
    public string Profile { get; set; } = "basic";
    public bool OneClick { get; set; }
    public List<string> OneClickOwned { get; set; } = new();  // what one-click turned on and must undo
    public int StandbyLimitMB { get; set; } = 1024;
    public List<string> TrimTargets { get; set; } = new();
    public List<string> WorkApps { get; set; } = new();        // Biz work mode: programs raised with the video calls
    public GpuChoice Gpu { get; set; } = new();
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new();
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsBoostBackend : IBoostBackend, IGameLimiter, IDisposable
{
    private const string GameBar = @"Software\Microsoft\GameBar";
    private const string GameConfig = @"System\GameConfigStore";
    private const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string PushNotifications = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string GpuPreferences = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";
    private const string UpdateUx = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    // IFEO PerfOptions\CpuPriorityClass: 6 is "above normal". Never 3 (high) or 4 (realtime): voice, audio and
    // input would suffer under a busy game, the same rule as the "자원 분배" page (ResourceManager.cs).
    private const int AboveNormalPriorityClass = 6, HighPriorityClass = 3;
    private const int ExitWaitMs = 5000;
    private const int UninstallWaitMs = 25000;   // Velopack ends the uninstall hook after 30 seconds

    private static readonly string[] GameTime = { "ram.standby", "ram.workingset", "cpu.ecores", "cpu.power", "net.monitor", "sys.focus", "boost.profiles" };
    private static readonly string[] PerGame = { "gpu.highperf", "cpu.priority", "gpu.driver" };
    private static readonly string[] AdminKinds = { "hags", "ifeo", "netpm", "nv", "wu", "oc" };
    private static readonly string[] BasicProfile = { "cpu.power", "cpu.ecores", "ram.workingset", "net.monitor", "sys.focus", "sys.gamemode", "sys.dvr" };
    private static readonly string[] FullProfile = BasicProfile.Append("ram.standby").ToArray();
    private static readonly (string start, string end)[] UpdatePauses =
    {
        ("PauseUpdatesStartTime", "PauseUpdatesExpiryTime"),
        ("PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime"),
        ("PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime"),
    };
    // Stay on all cores while a game runs: voice, streaming and capture need the speed, and
    // whatever a launcher or a browser starts would inherit its affinity. A game started from a web page
    // (Roblox's "참여" opens roblox-player: through the browser) would otherwise run on the E-cores only.
    private static readonly HashSet<string> KeepOnPCores = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "obs32", "obs", "Streamlabs Desktop", "Streamlabs OBS", "XSplit.Core", "PRISMLiveStudio",
        "Discord", "DiscordPTB", "DiscordCanary", "TeamSpeak", "ts3client_win64", "mumble", "Skype", "Zoom",
        "ms-teams", "Teams", "KakaoTalk", "NVIDIA Broadcast", "nvsphelper64", "NVIDIA Share", "SteelSeriesSonar",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "RiotClientServices", "Riot Client",
        "RiotClientUx", "LeagueClient", "GalaxyClient", "upc", "UbisoftConnect", "EADesktop", "Origin", "XboxPcApp",
        "GameBarPresenceWriter", "Launcher", "Playnite.DesktopApp", "Playnite.FullscreenApp", "Overwolf", "CurseForge",
        "MinecraftLauncher", "itch", "Amazon Games UI", "NexonPlug", "STOVE",
        // browsers: games and launchers start from links in them
        "chrome", "msedge", "firefox", "whale", "opera", "brave", "vivaldi", "iexplore", "Arc", "zen",
        // the launchers that install and start Roblox (the game itself is recognised by its folder)
        "Bloxstrap", "Fishstrap", "RobloxPlayerLauncher", "RobloxPlayerInstaller",
    };

    private static bool KeepsAllCores(string name) =>
        KeepOnPCores.Contains(name) || name.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("strap", StringComparison.OrdinalIgnoreCase);

    // A program that just started may still turn out to be a game (its window is not up yet), so it is left alone for a while.
    private const int NewProcessGraceSeconds = 60;

    private readonly BoostHelper _helper = BoostHelper.Admin;
    private readonly BoostHelper _plainHelper = new(elevated: false);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _activity = new();
    private BoostState _s = Load();
    private bool _gameRunning;
    private string? _gameName;
    private DateTime _lastPurge = DateTime.MinValue, _lastTrim = DateTime.MinValue, _lastCores = DateTime.MinValue, _lastFreeCheck = DateTime.MinValue;
    private PerformanceCounter? _freeCounter;
    private long _netLast;
    private DateTime _netTime;
    private int _netHigh;
    private DateTime _netNotified = DateTime.MinValue;
    private Dictionary<int, long>? _netByProcess;

    public WindowsBoostBackend() { BoostHelper.Deferred += OnDeferred; }

    // An administrator prompt waited because a game was in front: one line in 최근 동작, not one every tick.
    private DateTime _deferredLogged = DateTime.MinValue;
    private void OnDeferred()
    {
        if (DateTime.UtcNow - _deferredLogged < TimeSpan.FromMinutes(1)) return;
        _deferredLogged = DateTime.UtcNow;
        Log("게임 화면이 앞에 있어서 관리자 확인 창을 띄우지 않았어요. 관리자 권한이 필요한 항목은 그동안 바뀌지 않아요");
    }

    public bool Connected => true;
    /// Toggles or the list below them changed outside the page. May fire on any thread.
    public event Action? Changed;
    /// A line for the small on-screen notice. May fire on any thread.
    public event Action<string>? Notice;

    private static string StateFile => Path.Combine(Platform.DataDir, "boost-state.json");

    private static BoostState Load()
    {
        try { if (File.Exists(StateFile)) return JsonSerializer.Deserialize<BoostState>(File.ReadAllText(StateFile)) ?? new(); }
        catch (Exception) { }
        return new();
    }

    private void Save()
    {
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_s, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StateFile, true);
    }

    private void Publish()
    {
        ResourceManagers.Games = _s.Games.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        ResourceManagers.GameFolders = _s.Games.Select(GameFolder).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Changed?.Invoke();
    }

    // The folder a game is installed in, where its launcher, crash reporter and anti-cheat sit too.
    private static string? GameFolder(string exe)
    {
        var dir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir)) return null;
        int i = dir.IndexOf(@"\Binaries\", StringComparison.OrdinalIgnoreCase);   // Unreal games keep the rest one level up
        if (i > 0) dir = dir[..i];
        // a game right in a drive or in Program Files would take everything there with it
        return dir.Count(c => c == '\\') >= 2 ? dir + @"\" : null;
    }

    private void Log(string line)
    {
        lock (_activity)
        {
            _activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
            if (_activity.Count > 30) _activity.RemoveAt(_activity.Count - 1);
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<string> Activity { get { lock (_activity) return _activity.ToList(); } }
    public IReadOnlyList<string> Games => _s.Games.ToList();
    public IReadOnlyList<string> TrimTargets => _s.TrimTargets.ToList();
    public string Profile => _s.Profile;
    public int StandbyLimitMB => _s.StandbyLimitMB;
    public string? RunningGame => _gameRunning ? _gameName : null;

    /// Names of the features that are changed right now, for the backup page.
    public IReadOnlyList<string> ChangedItems()
    {
        var ids = _s.Backups.Select(b => b.Feature).Concat(_s.Enabled).Distinct().ToList();
        var titles = BoostCatalog.All.Where(f => ids.Contains(f.Id)).Select(f => f.Title).ToList();
        if (ids.Contains(StartupFeature)) titles.Add(StartupTitle);
        if (ShareRecord.Any) titles.Add(ShareTitle);
        return titles;
    }

    internal const string ShareTitle = "앱별 분배";
    internal const string PagefileTitle = "가상 메모리 (페이징 파일)";
    private static bool IsUninstallAdminKind(BoostBackup b) => b.Kind is "hags" or "ifeo" or "netpm" or "nv" or "wu";

    /// Settings changed with administrator rights that are still changed. Uninstalling cannot put them back
    /// without asking, so the backup page names them.
    public IReadOnlyList<string> AdminItemsLeft()
    {
        var ids = _s.Backups.Where(IsUninstallAdminKind).Select(b => b.Feature).Distinct().ToList();
        var titles = BoostCatalog.All.Where(f => ids.Contains(f.Id)).Select(f => f.Title).ToList();
        if (VirtualMemory.HasBackup) titles.Add(PagefileTitle);
        return titles;
    }

    public void SetProfile(string profile) { _s.Profile = profile; Save(); }
    public void SetStandbyLimit(int mb) { _s.StandbyLimitMB = mb; Save(); }
    public void SetTrimTargets(IEnumerable<string> names) { _s.TrimTargets = names.ToList(); Save(); }

    // ------------------------------------------------------------ start / stop
    public async Task StartAsync()
    {
        Publish();
        if (_s.Enabled.Except(_s.OneClickOwned).Any(id => id is "ram.standby" or "sys.focus" or "net.monitor"))
            await _helper.EnsureAsync();
        await _gate.WaitAsync();
        try { await RecoverAsync(); await MovePriorityDownAsync(); Save(); }
        catch (Exception e) { Log("복구하지 못했어요: " + e.Message); }
        finally { _gate.Release(); }
        Publish();
        // The recovery above also runs for someone who refuses the agreement;
        // applying boosts to a game does not start until it is accepted.
        _ = Task.Run(async () => { await Eula.WhenAccepted; await WatchLoopAsync(); });
    }

    // After a crash or a closed PC: what was only meant for the game goes back. boost-state.json holds what
    // was there before and what was set here (the power plan, the notification switch, the update pause),
    // and each goes back only while it is still what was set here.
    private async Task RecoverAsync()
    {
        try
        {
            var capped = ShareRecord.RecoverEarlierRuns();   // the "자원 분배" page of a run that is gone
            if (capped.Count > 0)
            {
                var line = $"SystemBoost가 갑자기 꺼져서 {string.Join(", ", capped)}의 CPU 제한이 남아 있어요. 그 프로그램을 다시 켜면 풀려요";
                Log(line);
                Notice?.Invoke(line);
            }
        }
        catch (Exception e) { Log("자원 분배를 되돌리지 못했어요: " + e.Message); }
        await PutGpuBackAsync();
        var stream = _s.Backups.Where(b => b.Feature == "biz.stream").ToList();
        if (stream.Count > 0) await RestoreAsync(stream);
        // the helper undoes it as it exits; a power cut leaves it
        if (Has(GameLimit)) await RestoreAsync(_s.Backups.Where(b => b.Feature == GameLimit).ToList());
        if (RunningGames(ProcessNames()) != null) return;
        // an update pause that is over, or that someone changed since, needs nobody; one still on needs the helper once
        DropSettledPause();
        if (_s.Backups.Any(b => b.Kind == "wu" && b.SetTo.Length > 0) && !_helper.Running)
        {
            Log("지난번에 멈춘 Windows 업데이트를 다시 켜려고 관리자 권한을 물어봐요");
            if (await _helper.EnsureAsync() != null) Log("Windows 업데이트 일시정지는 하루 안에 저절로 끝나요");
        }
        if (_s.OneClick) await OneClickOffAsync();
        await EndGameTimeAsync();
    }

    // Until this version a game's start priority was set to "높음" (3). With the helper up anyway (no extra prompt),
    // a value that was set here and is still there moves to "보통 이상" (6).
    private async Task MovePriorityDownAsync()
    {
        if (!_helper.Running) return;
        var high = HighPriorityClass.ToString(CultureInfo.InvariantCulture);
        var above = AboveNormalPriorityClass.ToString(CultureInfo.InvariantCulture);
        foreach (var b in _s.Backups.Where(b => b.Kind == "ifeo" && b.SetTo != above).ToList())
        {
            if (b.Existed && b.Value == high) continue;   // the user had it so before
            if (ReadMachineDword(Ifeo + b.Target + @"\PerfOptions", "CpuPriorityClass") != HighPriorityClass) continue;
            if (await HelperError("ifeo.set", b.Target, above) != null) continue;
            b.SetTo = above;
            Log($"{b.Target}: 시작 우선순위를 '높음'에서 '보통 이상'으로 낮췄어요");
        }
    }

    /// Update-pause records that need no helper to settle: values someone changed since, and a pause set here that
    /// is over (Windows ignores a pause whose end has passed, so writing the old times back changes nothing).
    /// Dropping them keeps a later restore from writing old times over a pause the user set themselves.
    private void DropSettledPause()
    {
        var wu = _s.Backups.Where(b => b.Kind == "wu" && b.SetTo.Length > 0).ToList();
        if (wu.Count == 0) return;
        var changed = wu.Where(b => !RestoreRules.StillOurs(b.SetTo, ReadMachineString(UpdateUx, b.Name))).ToList();
        var ours = wu.Except(changed).ToList();
        var drop = changed.ToList();
        if (ours.Count > 0 && RestoreRules.PauseOver(ours.Where(b => RestoreRules.IsPauseEnd(b.Name)).Select(b => b.SetTo), DateTime.UtcNow))
            drop.AddRange(ours);
        foreach (var b in drop) _s.Backups.Remove(b);
        if (drop.Count > 0) Save();
    }

    public void Dispose()
    {
        BoostHelper.Deferred -= OnDeferred;
        _stop.Cancel();
        try
        {
            _gate.Wait(ExitWaitMs);
            // called on the UI thread, which helper replies must not wait for
            Task.Run(async () =>
            {
                await EndAllBizModesAsync();
                await EndGameTimeAsync();
                if (_s.OneClick) await OneClickOffAsync();
                // the helper does this too as it exits; here the backup is cleared as well
                var gpu = _s.Backups.Where(b => b.Kind is "oc" or "nvlimit").ToList();
                if (gpu.Count > 0 && (_helper.Running || _plainHelper.Running)) await RestoreAsync(gpu);
                Save();
            }).Wait(ExitWaitMs);
        }
        catch (Exception) { }
        _helper.Dispose();
        _plainHelper.Dispose();
    }

    /// Velopack uninstall hook. There is no app window and no elevation here, and Velopack ends the hook after
    /// 30 seconds. First what this user may change, without any prompt: registry values, the power plan, startup
    /// programs and the "자원 분배" changes. Then, if settings changed with administrator rights are left, one
    /// message names them and offers to put them back (one administrator prompt) instead of leaving them silently.
    /// No scheduled task or service is left behind to do it later.
    public static void RestoreUserSettings()
    {
        WindowsBoostBackend? b = null;
        try
        {
            b = new WindowsBoostBackend();
            bool notifications = b.RestoreUserLevel();
            try { ShareRecord.PutBackAll(); } catch (Exception) { }
            b.DropSettledPause();
            var left = b.AdminItemsLeft();
            if (left.Count > 0 && Environment.UserInteractive && !ResourceManagers.FullscreenInFront() && AskBeforeUninstall(left))
            {
                var t = b.RestoreAdminForUninstallAsync();
                if (!t.Wait(UninstallWaitMs) || t.Result != null) TellUninstall(t.IsCompleted ? t.Result : null);
            }
            if (notifications) RestartNotifications();   // last: it may take a few seconds, and sign-in applies it anyway
        }
        catch (Exception) { }
        finally { b?.CloseForUninstall(); }
    }

    // The current user's part of the uninstall: no prompt needed.
    private bool RestoreUserLevel()
    {
        bool notifications = false;
        foreach (var b in Enumerable.Reverse(_s.Backups.ToList()))
        {
            try
            {
                switch (b.Kind)
                {
                    case "hkcu": RestoreUserValue(b); break;
                    case "startup": RestoreStartup(b); break;
                    case "power": if (RestorePower(b) != null) continue; break;
                    default: continue;
                }
                notifications |= b.Key == PushNotifications;
                _s.Backups.Remove(b);
            }
            catch (Exception) { }
        }
        Save();
        return notifications;
    }

    private async Task<string?> RestoreAdminForUninstallAsync()
    {
        var e = await _helper.EnsureAsync();
        if (e != null) return e;
        var failed = await RestoreAsync(_s.Backups.Where(IsUninstallAdminKind).ToList());
        Save();
        if (VirtualMemory.HasBackup)
        {
            var r = await VirtualMemory.RevertAsync();
            if (VirtualMemory.HasBackup) failed = (failed == null ? "" : failed + " · ") + r;
        }
        return failed;
    }

    private void CloseForUninstall()
    {
        BoostHelper.Deferred -= OnDeferred;
        _helper.Dispose();
        _plainHelper.Dispose();
    }

    // The uninstall hook runs before the app's own screen and language exist, so its two messages carry both languages here.
    private static bool English => Lang.Saved() != Lang.Korean;
    private static readonly Dictionary<string, string> UninstallTitlesEn = new()
    {
        ["하드웨어 가속 GPU 스케줄링"] = "Hardware-accelerated GPU scheduling",
        ["게임 우선순위 '보통 이상'"] = "Game priority 'Above normal'",
        ["드라이버 게임 프로필"] = "Driver game profile",
        ["네트워크 어댑터 절전 끄기"] = "Network adapter power saving off",
        ["게임 중 알림·업데이트 일시정지"] = "Pause notifications and updates during games",
        [PagefileTitle] = "Virtual memory (paging file)",
    };

    private static bool AskBeforeUninstall(IReadOnlyList<string> items)
    {
        var list = string.Join("\n", items.Select(i => "· " + (English ? UninstallTitlesEn.GetValueOrDefault(i, i) : i)));
        var text = English
            ? $"These settings that SystemBoost changed with administrator rights are still in place:\n\n{list}\n\n"
              + "Put them back before uninstalling? Choose Yes, then Yes in the Windows administrator prompt. Choose No to leave them as they are."
            : $"SystemBoost가 관리자 권한으로 바꾼 설정이 아직 남아 있어요.\n\n{list}\n\n"
              + "지우기 전에 원래대로 돌릴까요? '예'를 누른 뒤 Windows 관리자 확인 창에서도 '예'를 누르세요. '아니요'를 누르면 그대로 남아요.";
        return MessageBoxW(IntPtr.Zero, text, "SystemBoost", MbYesNo | MbIconWarning | MbTopmost | MbSetForeground) == IdYes;
    }

    private static void TellUninstall(string? why)
    {
        var text = English
            ? "Some settings could not be put back, so they stay as they are." + (why != null ? "\n\n" + why : "")
            : "일부 설정을 원래대로 돌리지 못해서 그대로 남아요." + (why != null ? "\n\n" + why : "");
        MessageBoxW(IntPtr.Zero, text, "SystemBoost", MbOk | MbIconWarning | MbTopmost | MbSetForeground);
    }

    // ------------------------------------------------------------ interface
    public Task<bool> IsAppliedAsync(string id) => Task.FromResult(id switch
    {
        "boost.oneclick" => _s.OneClick,
        "gpu.hags" => Has(id) || ReadMachineDword(GraphicsDrivers, "HwSchMode") == 2,
        _ when GameTime.Contains(id) || BizModes.Contains(id) => _s.Enabled.Contains(id),
        _ => Has(id),
    });

    public async Task<string> ApplyAsync(string id)
    {
        if (_measuring) return BusyMeasuring;
        await _gate.WaitAsync();
        try { var r = await ApplyCoreAsync(id, prompt: true); Save(); return r; }
        catch (Exception e) { return "실패했어요: " + e.Message; }
        finally { _gate.Release(); Publish(); }
    }

    public async Task<string> RevertAsync(string id)
    {
        if (_measuring && Measurable.Contains(id)) return BusyMeasuring;    // gpu.oc must always be able to go back
        await _gate.WaitAsync();
        try { var r = await RevertCoreAsync(id); Save(); return r; }
        catch (Exception e) { return "실패했어요: " + e.Message; }
        finally { _gate.Release(); Publish(); }
    }

    public async Task<string> RevertAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            bool fgLimit = FrameGenEngine.HasDriverLimits;   // frame generation's limit in a game's NVIDIA driver profile
            int count = ChangedItems().Count + (Has(GameLimit) ? 1 : 0) + (fgLimit ? 1 : 0);
            if (count == 0) return "변경된 것이 없어요";
            var fgNote = fgLimit && await FrameGenEngine.RestoreDriverSettingsAsync(mayElevate: true) is { } fgFailed
                ? " · 게임 프레임 제한(드라이버 프로필)을 되돌리지 못했어요: " + fgFailed : "";
            if (fgNote.Length > 0) count--;   // it stays on record for the next try
            _s.OneClick = false;
            _s.OneClickOwned.Clear();
            _s.Enabled.Clear();
            _profileItems.Clear();
            await EndAllBizModesAsync();
            // the "자원 분배" page: its own run lifts its CPU caps, then everything recorded goes back
            var capped = new List<string>();
            try
            {
                if (ResourceManagers.Current != null) await ResourceManagers.Current.RestoreAllAsync();
                capped = ShareRecord.PutBackAll();
            }
            catch (Exception) { }
            var cappedNote = capped.Count > 0 ? $" · {string.Join(", ", capped)}의 CPU 제한은 그 프로그램을 다시 켜면 풀려요" : "";
            DropSettledPause();
            if (_s.Backups.Any(IsAdminKind))
            {
                var e = await _helper.EnsureAsync();
                if (e != null) { await RestoreAsync(_s.Backups.Where(b => !IsAdminKind(b)).ToList()); Save(); return e + cappedNote + fgNote; }
            }
            var failed = await RestoreAsync(_s.Backups.ToList());
            Save();
            return (failed == null ? $"{count}개 항목을 원래대로 돌렸어요" : $"일부를 되돌리지 못했어요: {failed}") + cappedNote + fgNote;
        }
        catch (Exception e) { return "실패했어요: " + e.Message; }
        finally { _gate.Release(); Publish(); }
    }

    public async Task<string> AddGameAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "실행 파일(.exe)을 골라 주세요";
            var name = Path.GetFileNameWithoutExtension(path);
            if (ResourceManagers.Protected.Contains(name)) return $"{name}은(는) 시스템 프로그램이라 게임으로 추가할 수 없어요";
            if (_s.Games.Contains(path, StringComparer.OrdinalIgnoreCase)) return "이미 목록에 있어요";
            _s.Games.Add(path);
            Save();
            Publish();
            var notes = new List<string>();
            foreach (var id in PerGame.Where(Has)) notes.Add(await ApplyCoreAsync(id, prompt: true));
            Save();
            return notes.Count == 0 ? $"{name}을(를) 추가했어요" : $"{name}을(를) 추가했어요. " + string.Join(" ", notes);
        }
        finally { _gate.Release(); Publish(); }
    }

    public async Task<string> RemoveGameAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            var exe = Path.GetFileName(path);
            var mine = _s.Backups.Where(b => PerGame.Contains(b.Feature) && (b.Target == path || b.Target == exe)).ToList();
            if (mine.Any(IsAdminKind))
            {
                var e = await _helper.EnsureAsync();
                if (e != null) return e;
            }
            var failed = await RestoreAsync(mine);
            if (failed != null) return "되돌리지 못했어요: " + failed;
            _s.Games.RemoveAll(g => string.Equals(g, path, StringComparison.OrdinalIgnoreCase));
            Save();
            return $"{Path.GetFileNameWithoutExtension(path)}을(를) 뺐어요";
        }
        finally { _gate.Release(); Publish(); }
    }

    // ------------------------------------------------------------ apply
    private async Task<string> ApplyCoreAsync(string id, bool prompt)
    {
        switch (id)
        {
            case "boost.oneclick": return await OneClickOnAsync();
            case "sys.gamemode": return ApplyUserValues(id, (GameBar, "AutoGameModeEnabled", 1), (GameBar, "AllowAutoGameMode", 1));
            case "sys.dvr": return ApplyUserValues(id, (GameConfig, "GameDVR_Enabled", 0), (GameDvr, "AppCaptureEnabled", 0), (GameDvr, "HistoricalCaptureEnabled", 0));
            case "gpu.highperf": return ApplyGpuPreference();
            case "gpu.hags": return await ApplyHagsAsync();
            case "cpu.priority": return await ApplyPriorityAsync();
            case "gpu.driver": return await ApplyDriverAsync();
            case "net.adapterpower": return await ApplyAdapterPowerAsync();
            case "gpu.oc": return await ApplyGpuTuningAsync();
            case "boost.profiles" when !License.Allowed(PremiumFeature.PerGameProfiles): return $"{License.PlanOf(PremiumFeature.PerGameProfiles)} 기능이에요 (바뀐 것 없음)";
            case "biz.stream" or "biz.work": return ApplyBizMode(id);
        }
        if (!GameTime.Contains(id)) return "아직 준비 중이에요 (바뀐 것 없음)";
        if (id == "cpu.ecores" && EfficientMask() == 0) return "이 CPU는 E코어가 없어서 쓸 수 없어요 (바뀐 것 없음)";
        string? limited = null;
        if (id == "ram.standby" || (id is "sys.focus" or "net.monitor" && prompt))
        {
            var e = await _helper.EnsureAsync();
            if (e != null && id == "ram.standby") return e;
            if (e != null) limited = id == "sys.focus" ? " Windows 업데이트는 멈추지 않아요 (관리자 권한 없음)" : " 프로그램 이름은 보이지 않아요 (관리자 권한 없음)";
        }
        if (!_s.Enabled.Contains(id)) _s.Enabled.Add(id);
        Save();
        if (_gameRunning) return await ActivateAsync(id) + limited;
        return id switch
        {
            "ram.workingset" when _s.TrimTargets.Count == 0 => "켰어요. 아래에서 대상 프로그램을 고르세요",
            "boost.profiles" => "켰어요. 아래에서 측정한 게임은 다음 실행부터 효과가 확인된 항목이 켜져요",
            _ => "켰어요. 게임이 실행되면 적용돼요." + limited,
        };
    }

    private async Task<string> RevertCoreAsync(string id)
    {
        if (id == "boost.oneclick") return await OneClickOffAsync();
        if (BizModes.Contains(id))
        {
            _s.Enabled.Remove(id);
            return await EndBizModeAsync(id) ?? "껐어요";
        }
        if (GameTime.Contains(id))
        {
            _s.Enabled.Remove(id);
            if (id == "boost.profiles") await EndGameProfileAsync();
            var r = await DeactivateAsync(id);
            return r ?? "껐어요";
        }
        var mine = _s.Backups.Where(b => b.Feature == id).ToList();
        if (mine.Count == 0) return "SystemBoost가 바꾼 적이 없어서 그대로 뒀어요";
        if (mine.Any(IsAdminKind))
        {
            var e = await _helper.EnsureAsync();
            if (e != null) return e;
        }
        var failed = await RestoreAsync(mine);
        return failed == null ? DescribeRestore(id) : "되돌리지 못했어요: " + failed;
    }

    private string DescribeRestore(string id) => id switch
    {
        "gpu.hags" => $"원래대로 돌렸어요 (HwSchMode {MachineText(GraphicsDrivers, "HwSchMode")}). 재부팅하면 적용돼요",
        _ => "원래대로 돌렸어요",
    };

    private bool Has(string id) => _s.Backups.Any(b => b.Feature == id);
    private static bool IsAdminKind(BoostBackup b) => AdminKinds.Contains(b.Kind);

    private bool HasEntry(string id, string kind, string target, string key = "", string name = "") =>
        _s.Backups.Any(b => b.Feature == id && b.Kind == kind && b.Target == target && b.Key == key && b.Name == name);

    /// Puts values back, newest first. Null when all went well.
    private async Task<string?> RestoreAsync(List<BoostBackup> items)
    {
        string? failed = null;
        bool notifications = false;
        foreach (var b in Enumerable.Reverse(items))
        {
            // one value that cannot go back does not keep the others from going back
            string? e;
            try { e = await RestoreOneAsync(b); }
            catch (Exception x) { e = $"{b.Feature}: {x.Message}"; }
            if (e != null) { failed = e; continue; }
            notifications |= b.Key == PushNotifications;
            _s.Backups.Remove(b);
        }
        if (notifications && !RestartNotifications()) Log("Windows 알림 설정은 다음 로그인부터 적용돼요");
        return failed;
    }

    private async Task<string?> RestoreOneAsync(BoostBackup b)
    {
        switch (b.Kind)
        {
            case "hkcu": RestoreUserValue(b); return null;
            case "power": return RestorePower(b);
            case "ecores": ClearCores(b.Value); return null;
            case "hags": return await HelperError(b.Existed ? new[] { "hags.set", b.Value } : new[] { "hags.del" });
            case "ifeo":
                // a start priority someone changed since stays theirs
                if (b.SetTo.Length > 0 && ReadMachineDword(Ifeo + b.Target + @"\PerfOptions", "CpuPriorityClass")?.ToString(CultureInfo.InvariantCulture) != b.SetTo) return null;
                return await HelperError(b.Existed ? new[] { "ifeo.set", b.Target, b.Value } : new[] { "ifeo.del", b.Target });
            case "netpm": return await HelperError("net.pm.set", b.Target, "1");
            case "nv": return await HelperError("nv.restore", b.Target, b.Name, b.Value);
            case "nvlimit": return await EndLimitCallAsync(b);
            case "oc": return await RestoreGpuAsync(b);
            case "wu":
                if (!RestoreRules.StillOurs(b.SetTo, ReadMachineString(UpdateUx, b.Name))) return null;   // Windows or the user changed it since
                return await HelperError(b.Existed ? new[] { "wu.set", b.Name, b.Value } : new[] { "wu.del", b.Name });
            case "startup": RestoreStartup(b); return null;
            default: return null;
        }
    }

    /// The plan that was active before, unless someone picked another plan since. Null when done (or nothing to do).
    private static string? RestorePower(BoostBackup b)
    {
        if (!Guid.TryParse(b.Value, out var before)) return null;
        if (ActivePlan() is Guid now)
        {
            if (now == before) return null;
            // still the plan set here; records from before this kept no SetTo, and only a high-performance plan was ever set
            bool ours = Guid.TryParse(b.SetTo, out var set) ? now == set : IsHighPerformance(now);
            if (!ours) return null;
        }
        return PowerSetActiveScheme(IntPtr.Zero, ref before) == 0 ? null : "전원 계획";
    }

    private static Guid? ActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
        var active = Marshal.PtrToStructure<Guid>(ptr);
        LocalFree(ptr);
        return active;
    }

    private async Task<string?> HelperError(params string[] request)
    {
        var (ok, data) = await _helper.CallAsync(request);
        return ok ? null : $"{request[0]}: {data}";
    }

    // ------------------------------------------------------------ current-user registry
    private string ApplyUserValues(string id, params (string key, string name, int value)[] changes)
    {
        var report = new List<string>();
        foreach (var (key, name, value) in changes)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            var cur = k.GetValue(name);
            if (!HasEntry(id, "hkcu", "", key, name))
            {
                var kind = cur == null ? RegistryValueKind.DWord : k.GetValueKind(name);
                _s.Backups.Add(new BoostBackup { Feature = id, Kind = "hkcu", Key = key, Name = name, Dword = kind == RegistryValueKind.DWord,
                                                 ValueKind = kind.ToString(), Existed = cur != null, Value = RegText(cur),
                                                 SetTo = value.ToString(CultureInfo.InvariantCulture) });
                Save();
            }
            k.SetValue(name, value, RegistryValueKind.DWord);
            report.Add($"{name} {cur ?? "없음"} → {k.GetValue(name)}");
        }
        return string.Join(", ", report);
    }

    // A registry value as text that RestoreUserValue can turn back into the same type.
    private static string RegText(object? v) => v switch
    {
        null => "",
        byte[] bytes => Convert.ToHexString(bytes),
        string[] lines => string.Join("\0", lines),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// Puts a current-user value back, in the type it had, only while it still holds what was set here.
    private static void RestoreUserValue(BoostBackup b)
    {
        using var k = Registry.CurrentUser.CreateSubKey(b.Key);
        if (b.Key == GpuPreferences) { RestoreGpuValue(k, b); return; }
        if (!RestoreRules.StillOurs(b.SetTo, k.GetValue(b.Name) is { } now ? RegText(now) : null)) return;   // changed since: theirs now
        if (!b.Existed) { k.DeleteValue(b.Name, false); return; }
        var kind = Enum.TryParse<RegistryValueKind>(b.ValueKind, out var vk) ? vk : b.Dword ? RegistryValueKind.DWord : RegistryValueKind.String;
        switch (kind)
        {
            case RegistryValueKind.DWord when int.TryParse(b.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d):
                k.SetValue(b.Name, d, RegistryValueKind.DWord); break;
            case RegistryValueKind.QWord when long.TryParse(b.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q):
                k.SetValue(b.Name, q, RegistryValueKind.QWord); break;
            case RegistryValueKind.Binary: k.SetValue(b.Name, Convert.FromHexString(b.Value), RegistryValueKind.Binary); break;
            case RegistryValueKind.MultiString: k.SetValue(b.Name, b.Value.Split('\0'), RegistryValueKind.MultiString); break;
            case RegistryValueKind.ExpandString: k.SetValue(b.Name, b.Value, RegistryValueKind.ExpandString); break;
            default: k.SetValue(b.Name, b.Value, RegistryValueKind.String); break;   // also an old record whose "DWORD" was text
        }
    }

    // gpu.highperf: only the GpuPreference part goes back, the other settings Windows keeps in the value stay.
    private static void RestoreGpuValue(RegistryKey k, BoostBackup b)
    {
        var before = b.Existed ? RestoreRules.GpuToken(b.Value) : null;
        var (write, v) = RestoreRules.GpuBack(k.GetValue(b.Name) as string, before, b.SetTo.Length > 0 ? b.SetTo : HighPerformanceGpu);
        if (!write) return;
        if (v == null) k.DeleteValue(b.Name, false);
        else k.SetValue(b.Name, v, RegistryValueKind.String);
    }

    private const string HighPerformanceGpu = "2";

    private static int? ReadMachineDword(string key, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(name) is int v ? v : null;
    }

    private static string MachineText(string key, string name) => ReadMachineDword(key, name)?.ToString() ?? "없음";

    private static string? ReadMachineString(string key, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    // ------------------------------------------------------------ per-game settings
    private string ApplyGpuPreference()
    {
        if (_s.Games.Count == 0) return "먼저 '게임 부스트'의 내 게임에 게임을 추가하세요 (바뀐 것 없음)";
        int done = 0;
        using var k = Registry.CurrentUser.CreateSubKey(GpuPreferences);
        foreach (var game in _s.Games)
        {
            var cur = k.GetValue(game) as string;
            if (!HasEntry("gpu.highperf", "hkcu", game, GpuPreferences, game))
            {
                _s.Backups.Add(new BoostBackup { Feature = "gpu.highperf", Kind = "hkcu", Target = game, Key = GpuPreferences, Name = game,
                                                 Existed = cur != null, Value = cur ?? "", SetTo = HighPerformanceGpu });
                Save();
            }
            k.SetValue(game, RestoreRules.WithGpuToken(cur, HighPerformanceGpu));
            done++;
        }
        return $"게임 {done}개를 고성능 GPU로 지정했어요. 다음 실행부터 적용돼요";
    }

    private async Task<string> ApplyHagsAsync()
    {
        var e = await _helper.EnsureAsync();
        if (e != null) return e;
        var cur = ReadMachineDword(GraphicsDrivers, "HwSchMode");
        if (cur == 2) return "이미 켜져 있어요 (HwSchMode 2)";
        if (!HasEntry("gpu.hags", "hags", ""))
        {
            _s.Backups.Add(new BoostBackup { Feature = "gpu.hags", Kind = "hags", Existed = cur != null, Value = cur?.ToString(CultureInfo.InvariantCulture) ?? "" });
            Save();
        }
        var failed = await HelperError("hags.set", "2");
        if (failed != null) { _s.Backups.RemoveAll(b => b.Feature == "gpu.hags"); return "실패했어요: " + failed; }
        return $"HwSchMode {cur?.ToString() ?? "없음"} → {MachineText(GraphicsDrivers, "HwSchMode")}. 재부팅하면 적용돼요";
    }

    private async Task<string> ApplyPriorityAsync()
    {
        if (_s.Games.Count == 0) return "먼저 '게임 부스트'의 내 게임에 게임을 추가하세요 (바뀐 것 없음)";
        var e = await _helper.EnsureAsync();
        if (e != null) return e;
        var report = new List<string>();
        foreach (var game in _s.Games)
        {
            var exe = Path.GetFileName(game);
            var key = Ifeo + exe + @"\PerfOptions";
            var cur = ReadMachineDword(key, "CpuPriorityClass");
            var above = AboveNormalPriorityClass.ToString(CultureInfo.InvariantCulture);
            if (!HasEntry("cpu.priority", "ifeo", exe))
                _s.Backups.Add(new BoostBackup { Feature = "cpu.priority", Kind = "ifeo", Target = exe, Existed = cur != null, Value = cur?.ToString(CultureInfo.InvariantCulture) ?? "" });
            foreach (var b in _s.Backups.Where(b => b.Feature == "cpu.priority" && b.Kind == "ifeo" && b.Target == exe)) b.SetTo = above;
            Save();
            var failed = await HelperError("ifeo.set", exe, above);
            if (failed != null) return "실패했어요: " + failed;
            report.Add($"{exe} {cur?.ToString() ?? "없음"} → {MachineText(key, "CpuPriorityClass")}");
        }
        return string.Join(", ", report) + ". 다음 실행부터 '보통 이상' 우선순위로 시작해요";
    }

    // ------------------------------------------------------------ room for frame generation
    private const string GameLimit = "fg.limit";

    public async Task<string?> LimitGameAsync(string exe, int fps)
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"))) return NoLimiter(fps);
            var text = fps.ToString(CultureInfo.InvariantCulture);
            // Current drivers take this from an ordinary user, so nothing has to come up over the game.
            bool ok = false;
            string data = "";
            if (await _plainHelper.EnsureAsync() == null) (ok, data) = await _plainHelper.CallAsync("nv.limit.set", exe, text);
            if (!ok && data != "no_nvidia")
            {
                var e = await _helper.EnsureAsync();
                if (e != null) return e;
                (ok, data) = await _helper.CallAsync("nv.limit.set", exe, text);
            }
            if (!ok) return data == "no_nvidia" ? NoLimiter(fps) : "게임 프레임을 제한하지 못했어요: " + data;
            var parts = data.Split(';');
            if (parts[0] == "keep") return $"이 게임에는 이미 {parts[1]}fps 제한이 걸려 있어요. 게임 그래픽 설정을 낮춘 뒤 다시 눌러 보세요";
            if (!HasEntry(GameLimit, "nvlimit", exe))
            {
                await RestoreAsync(_s.Backups.Where(b => b.Feature == GameLimit).ToList());
                _s.Backups.Add(new BoostBackup { Feature = GameLimit, Kind = "nvlimit", Target = exe, Value = parts[1] });
            }
            Save();
            return null;
        }
        catch (Exception e) { return "게임 프레임을 제한하지 못했어요: " + e.Message; }
        finally { _gate.Release(); }
    }
    // AMD and Intel drivers give the app no per-game limit it can set without touching the game,
    // and a card-wide one would hold back the overlay too. The user gets the number to set instead.
    // atiadlxx.dll comes with every AMD display driver; Radeon Chill goes no lower than 30.
    private static string NoLimiter(int fps)
    {
        var n = fps.ToString(CultureInfo.InvariantCulture);
        return fps >= 30 && File.Exists(Path.Combine(Environment.SystemDirectory, "atiadlxx.dll"))
            ? $"라데온은 앱이 게임 프레임을 대신 제한할 수 없어요. 게임 설정에서 최대 프레임을 {n}fps로 맞추거나, AMD Software의 이 게임 설정에서 Radeon Chill을 켜고 최소·최대를 둘 다 {n}으로 맞춘 뒤 다시 눌러 보세요"
            : $"이 그래픽 카드는 앱이 게임 프레임을 대신 제한할 수 없어요. 게임 설정에서 최대 프레임을 {n}fps로 맞춘 뒤 다시 눌러 보세요";
    }

    public async Task EndGameLimitAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var mine = _s.Backups.Where(b => b.Feature == GameLimit).ToList();
            if (mine.Count == 0) return;
            var failed = await RestoreAsync(mine);
            Save();
            if (failed != null) Log("게임 프레임 제한을 되돌리지 못했어요: " + failed);
        }
        catch (Exception e) { Log("게임 프레임 제한을 되돌리지 못했어요: " + e.Message); }
        finally { _gate.Release(); }
    }

    private async Task<string?> EndLimitCallAsync(BoostBackup b)
    {
        if (_helper.Running)
        {
            var (done, _) = await _helper.CallAsync("nv.limit.restore", b.Target, b.Value);
            if (done) { if (_plainHelper.Running) await _plainHelper.CallAsync("nv.limit.restore", b.Target, b.Value); return null; }
        }
        var e = await _plainHelper.EnsureAsync();
        if (e != null) return e;
        var (ok, data) = await _plainHelper.CallAsync("nv.limit.restore", b.Target, b.Value);
        return ok ? null : data;
    }

    private async Task<string> ApplyDriverAsync()
    {
        if (_s.Games.Count == 0) return "먼저 '게임 부스트'의 내 게임에 게임을 추가하세요 (바뀐 것 없음)";
        var dll = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
        if (!File.Exists(dll)) return "NVIDIA GPU에서만 쓸 수 있어요 (바뀐 것 없음)";
        var e = await _helper.EnsureAsync();
        if (e != null) return e;
        int done = 0;
        foreach (var game in _s.Games)
        {
            var exe = Path.GetFileName(game);
            if (HasEntry("gpu.driver", "nv", exe)) { done++; continue; }
            var (ok, data) = await _helper.CallAsync("nv.set", exe);
            if (!ok) return data == "no_nvidia" ? "NVIDIA GPU에서만 쓸 수 있어요 (바뀐 것 없음)" : "실패했어요: " + data;
            var parts = data.Split(';');
            _s.Backups.Add(new BoostBackup { Feature = "gpu.driver", Kind = "nv", Target = exe, Name = parts[0], Value = parts[1] });
            Save();
            done++;
        }
        return $"게임 {done}개의 전원 관리 모드를 '최대 성능 선호'로 바꿨어요";
    }

    private async Task<string> ApplyAdapterPowerAsync()
    {
        var e = await _helper.EnsureAsync();
        if (e != null) return e;
        var (ok, data) = await _helper.CallAsync("net.pm.list");
        if (!ok) return "어댑터를 읽지 못했어요 (바뀐 것 없음)";
        var on = ParseAdapters(data).Where(a => a.on).Select(a => a.id).ToList();
        if (on.Count == 0) return "절전이 켜진 어댑터가 없어요";
        foreach (var id in on)
        {
            _s.Backups.Add(new BoostBackup { Feature = "net.adapterpower", Kind = "netpm", Target = id });
            Save();
            var failed = await HelperError("net.pm.set", id, "0");
            if (failed != null) { _s.Backups.RemoveAll(b => b.Feature == "net.adapterpower" && b.Target == id); return "실패했어요: " + failed; }
        }
        var (_, after) = await _helper.CallAsync("net.pm.list");
        return $"어댑터 {on.Count}개의 절전 허용을 껐어요 (꺼진 어댑터 {ParseAdapters(after).Count(a => !a.on)}개)";
    }

    private static List<(string id, bool on)> ParseAdapters(string data) =>
        data.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('=')).Where(p => p.Length == 2).Select(p => (p[0], p[1] == "1")).ToList();

    // ------------------------------------------------------------ one-click
    private async Task<string> OneClickOnAsync()
    {
        if (_s.Games.Count == 0 && ResourceManagers.GameProcess == null) return "먼저 아래 '내 게임'에 게임을 추가하세요 (바뀐 것 없음)";
        if (_s.Profile == "full")
        {
            var e = await _helper.EnsureAsync();
            if (e != null) return e;
        }
        var owned = new List<string>();
        var report = new List<string>();
        foreach (var id in _s.Profile == "full" ? FullProfile : BasicProfile)
        {
            if (await IsAppliedAsync(id)) continue;
            var r = await ApplyCoreAsync(id, prompt: false);
            if (await IsAppliedAsync(id)) owned.Add(id); else report.Add(r);
        }
        _s.OneClick = true;
        _s.OneClickOwned = owned;
        Save();
        var text = $"{owned.Count}개 항목을 켰어요. 게임이 끝나면 원래대로 돌려요";
        return report.Count == 0 ? text : text + " · " + string.Join(" · ", report);
    }

    private async Task<string> OneClickOffAsync()
    {
        var owned = _s.OneClickOwned.ToList();
        _s.OneClick = false;
        _s.OneClickOwned.Clear();
        foreach (var id in Enumerable.Reverse(owned)) await RevertCoreAsync(id);
        Save();
        return $"{owned.Count}개 항목을 원래대로 돌렸어요";
    }

    // ------------------------------------------------------------ watching for the game
    private async Task WatchLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await _gate.WaitAsync(_stop.Token);
                try { await TickAsync(); }
                catch (Exception e) { Log("오류: " + e.Message); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync()
    {
        await WatchGpuAsync();
        if (_s.Enabled.Count == 0 && !_s.OneClick && !_gameRunning && !_streaming && _raised.Count == 0) return;
        var names = ProcessNames();
        var game = RunningGames(names);
        if (game != null && !_gameRunning)
        {
            _gameRunning = true;
            _gameName = game;
            Log($"{game} 실행을 감지했어요");
            foreach (var id in _s.Enabled.Where(GameTime.Contains).ToList()) Log(await ActivateAsync(id));   // the Biz modes follow their own programs
            Save();
            Publish();
        }
        else if (game == null && _gameRunning)
        {
            _gameRunning = false;
            Log($"{_gameName} 종료를 감지했어요");
            _gameName = null;
            _profileItems.Clear();
            await EndGameTimeAsync();
            if (_s.OneClick) Log(await OneClickOffAsync());
            Save();
            Publish();
        }
        if (_gameRunning) await InGameAsync();
        await BizTickAsync(names);
    }

    private string? RunningGames(HashSet<string> names)
    {
        foreach (var g in _s.Games)
        {
            var n = Path.GetFileNameWithoutExtension(g);
            if (names.Contains(n)) return n;
        }
        var fg = ResourceManagers.GameProcess;
        return fg != null && names.Contains(fg) ? fg : null;
    }

    // Names come from the system's process list; no handle to the game is opened.
    private static HashSet<string> ProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { set.Add(p.ProcessName); }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return set;
    }

    private async Task<string> ActivateAsync(string id)
    {
        switch (id)
        {
            case "cpu.power": return ApplyPowerPlan();
            case "sys.focus": return await PauseInterruptionsAsync();
            case "cpu.ecores":
                if (!HasEntry(id, "ecores", ""))
                {
                    _ecoreSince = DateTime.UtcNow.Ticks;
                    _s.Backups.Add(new BoostBackup { Feature = id, Kind = "ecores", Value = RestoreRules.FormatMoved(_ecoreSince, _onECores) });
                    Save();
                }
                return $"백그라운드 프로그램 {MoveToEfficientCores()}개를 E코어로 옮겼어요";
            case "ram.workingset": return Trim();
            case "ram.standby": return $"여유 메모리가 {_s.StandbyLimitMB} MB 아래로 떨어지면 정리해요";
            case "boost.profiles": return await ApplyGameProfileAsync();
            default: return "게임 중 다른 프로그램의 큰 전송을 살펴봐요";
        }
    }

    /// Takes back what this feature changed while the game ran. Null when nothing failed.
    private async Task<string?> DeactivateAsync(string id)
    {
        var mine = _s.Backups.Where(b => b.Feature == id).ToList();
        if (mine.Count == 0) return null;
        if (mine.Any(IsAdminKind) && !_helper.Running)
        {
            var e = await _helper.EnsureAsync();
            if (e != null) return e;
        }
        var failed = await RestoreAsync(mine);
        return failed == null ? "원래대로 돌렸어요" : "되돌리지 못했어요: " + failed;
    }

    private async Task EndGameTimeAsync()
    {
        DropSettledPause();
        foreach (var id in GameTime)
        {
            // still streaming: the stream mode keeps the notifications off
            if (id == "sys.focus" && _streaming && !_stop.IsCancellationRequested) HandOverToast("sys.focus", "biz.stream");
            var mine = _s.Backups.Where(b => b.Feature == id).ToList();
            if (mine.Count == 0) continue;
            // the update pause ends by itself within a day if the helper is not there now
            var doable = mine.Where(b => !IsAdminKind(b) || _helper.Running).ToList();
            var failed = await RestoreAsync(doable);
            Log(failed == null ? $"{BoostCatalog.All.First(f => f.Id == id).Title}: 원래대로 돌렸어요" : "되돌리지 못했어요: " + failed);
        }
    }

    private async Task InGameAsync()
    {
        if (_measuring) return;     // nothing else may move while frames are compared
        var now = DateTime.UtcNow;
        if (IsOn("cpu.ecores") && now - _lastCores > TimeSpan.FromSeconds(10))
        {
            _lastCores = now;
            MoveToEfficientCores();
        }
        if (_s.Enabled.Contains("ram.workingset") && now - _lastTrim > TimeSpan.FromSeconds(60))
        {
            _lastTrim = now;
            if (_s.TrimTargets.Count > 0) Log(Trim());
        }
        if (_s.Enabled.Contains("ram.standby") && _helper.Running && now - _lastFreeCheck > TimeSpan.FromSeconds(5))
        {
            _lastFreeCheck = now;
            if (now - _lastPurge > TimeSpan.FromSeconds(60) && FreeMemoryMB() < _s.StandbyLimitMB)
            {
                _lastPurge = now;
                Log(await PurgeStandbyCoreAsync());
            }
        }
        if (_s.Enabled.Contains("net.monitor") && !_streaming) await WatchNetworkAsync();   // streaming watches it already
    }

    // ------------------------------------------------------------ RAM
    private double FreeMemoryMB()
    {
        _freeCounter ??= new PerformanceCounter("Memory", "Free & Zero Page List Bytes", true);
        return _freeCounter.NextValue() / 1048576.0;
    }

    public async Task<string> PurgeStandbyAsync()
    {
        var e = await _helper.EnsureAsync();
        if (e != null) return e;
        await _gate.WaitAsync();
        try { return await PurgeStandbyCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task<string> PurgeStandbyCoreAsync()
    {
        var (ok, data) = await _helper.CallAsync("ram.purge");
        if (!ok) return "대기 메모리를 정리하지 못했어요: " + data;
        var v = data.Split(';').Select(s => double.Parse(s, CultureInfo.InvariantCulture) / 1073741824.0).ToArray();
        return $"대기 메모리 {v[1]:0.0} GB → {v[3]:0.0} GB, 여유 메모리 {v[0]:0.0} GB → {v[2]:0.0} GB";
    }

    private string Trim()
    {
        if (_s.TrimTargets.Count == 0) return "작업 집합 정리: 대상 프로그램을 먼저 고르세요";
        long before = 0, after = 0;
        int count = 0, session = Process.GetCurrentProcess().SessionId;
        foreach (var name in _s.TrimTargets)
        {
            if (ResourceManagers.IsProtected(name)) continue;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.SessionId != session || ResourceManagers.LooksLikeGame(p.Id, name)) continue;
                    before += p.WorkingSet64;
                    var h = OpenProcess(ProcessSetQuota | ProcessQueryLimited, false, p.Id);
                    if (h == IntPtr.Zero) { after += p.WorkingSet64; continue; }
                    if (EmptyWorkingSet(h)) count++;
                    CloseHandle(h);
                    p.Refresh();
                    after += p.WorkingSet64;
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
        }
        return $"작업 집합 {count}개 정리: {before / 1048576} MB → {after / 1048576} MB";
    }

    // ------------------------------------------------------------ CPU
    /// Bit mask of the E-cores in the first processor group, 0 on a CPU without them.
    private static ulong EfficientMask()
    {
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out var len, IntPtr.Zero, 0);
        if (len == 0) return 0;
        var mem = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetSystemCpuSetInformation(mem, len, out len, IntPtr.Zero, 0)) return 0;
            var sets = new List<(int group, int index, byte eff)>();
            for (int off = 0; off < len;)
            {
                int size = Marshal.ReadInt32(mem, off);
                if (size < 24) break;
                if (Marshal.ReadInt32(mem, off + 4) == 0)   // CpuSetInformation
                    sets.Add((Marshal.ReadInt16(mem, off + 12), Marshal.ReadByte(mem, off + 14), Marshal.ReadByte(mem, off + 18)));
                off += size;
            }
            if (sets.Count == 0 || sets.Min(x => x.eff) == sets.Max(x => x.eff)) return 0;
            byte low = sets.Min(x => x.eff);
            ulong mask = 0;
            foreach (var x in sets) if (x.group == 0 && x.eff == low && x.index < 64) mask |= 1UL << x.index;
            return mask;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    /// Thread counts for the CPU page; null on a CPU without E-cores.
    public static (int performance, int efficient)? CoreCounts()
    {
        int e = System.Numerics.BitOperations.PopCount(EfficientMask());
        return e == 0 ? null : (Environment.ProcessorCount - e, e);
    }

    // Processes this put on the E-cores (pid -> start time in UTC ticks) and when this run began moving them.
    // Locked by _gate like everything else here. Kept in the cpu.ecores record too, so a later start finds them.
    private readonly Dictionary<int, long> _onECores = new();
    private long _ecoreSince;
    private readonly HashSet<int> _ecoreChecked = new();   // games whose cores were looked at once

    // Anything that looks like a game is never opened with a right that could change it, not even to undo a move
    // made before it looked like one: that stays, and the user is told. The window in front is never moved (it may
    // be a game a launcher started under another name); only a program this moved earlier gets its cores back
    // there. A program that started less than a minute ago is left alone too (a game's window may not be up yet),
    // and so are launchers and browsers, whose children take the setting over.
    private int MoveToEfficientCores()
    {
        var mask = EfficientMask();
        if (mask == 0) return 0;
        LoadMoved();
        int moved = 0, session = Process.GetCurrentProcess().SessionId;
        bool changed = false;
        var settled = DateTime.UtcNow.AddSeconds(-NewProcessGraceSeconds).Ticks;
        GetWindowThreadProcessId(GetForegroundWindow(), out var front);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != session || p.Id == Environment.ProcessId) continue;
                var name = p.ProcessName;
                if (ResourceManagers.LooksLikeGame(p.Id, name))
                {
                    if (_onECores.Remove(p.Id)) { changed = true; TellOnECores(name); }
                    else WarnIfOnECores(p.Id, name, mask, front);
                    continue;
                }
                if (p.Id == front || KeepsAllCores(name))
                {
                    if (_onECores.Remove(p.Id)) { AllCores(p.Id, mask); changed = true; }
                    continue;
                }
                long start = WinResourceManager.StartTicks(p.Id);
                if (start == 0) continue;
                if (_onECores.TryGetValue(p.Id, out var was))
                {
                    if (was == start) { moved++; continue; }
                    _onECores.Remove(p.Id);   // the number belongs to another program now
                    changed = true;
                }
                if (start > settled) continue;
                var h = OpenProcess(ProcessSetInformation | ProcessQueryLimited, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    // a process that limited itself keeps its own setting
                    if (GetProcessAffinityMask(h, out var own, out var system))
                    {
                        if ((ulong)own == mask) moved++;
                        else if (own == system && SetProcessAffinityMask(h, (UIntPtr)mask)) { moved++; _onECores[p.Id] = start; changed = true; }
                    }
                }
                finally { CloseHandle(h); }
            }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        if (changed) SaveMoved();
        return moved;
    }

    // The game itself (in front, on the list, or the one frame generation is on): if it runs on the E-cores only,
    // it took that over from whatever started it. It is read once, the way Task Manager reads it, never changed.
    private void WarnIfOnECores(int pid, string name, ulong mask, int front)
    {
        bool theGame = ResourceManagers.Games.Contains(name) || string.Equals(name, ResourceManagers.GameProcess, StringComparison.OrdinalIgnoreCase) ||
                       (pid == front && !ResourceManagers.Protected.Contains(name));
        if (!theGame || !_ecoreChecked.Add(pid)) return;
        var h = OpenProcess(ProcessQueryLimited, false, pid);
        if (h == IntPtr.Zero) return;
        try { if (GetProcessAffinityMask(h, out var own, out _) && (ulong)own == mask) TellOnECores(name); }
        finally { CloseHandle(h); }
    }

    private void TellOnECores(string name)
    {
        var line = $"{name}이(가) E코어에서만 돌고 있어요. 게임으로 보이는 프로그램은 SystemBoost가 바꾸지 않으니, 느리면 그 프로그램을 다시 켜 주세요";
        Log(line);
        Notice?.Invoke(line);
    }

    // A run that starts during a game picks up what the earlier run moved.
    private void LoadMoved()
    {
        if (_ecoreSince != 0) return;
        var rec = _s.Backups.FirstOrDefault(b => b.Kind == "ecores");
        var (since, moved) = RestoreRules.ParseMoved(rec?.Value);
        _ecoreSince = since != 0 ? since : DateTime.UtcNow.Ticks;
        foreach (var kv in moved) _onECores.TryAdd(kv.Key, kv.Value);
    }

    private void SaveMoved()
    {
        var rec = _s.Backups.FirstOrDefault(b => b.Kind == "ecores");
        if (rec == null) _s.Backups.Add(rec = new BoostBackup { Feature = "cpu.ecores", Kind = "ecores" });
        rec.Value = RestoreRules.FormatMoved(_ecoreSince, _onECores);
        Save();
    }

    // What this moved, and what started after the moving began and so inherited it, gets all cores back. What
    // already sat on the E-cores before was put there by the user or another tool and stays (RestoreRules.GiveCoresBack).
    private void ClearCores(string record)
    {
        var mask = EfficientMask();
        var (since, moved) = RestoreRules.ParseMoved(record);
        foreach (var kv in _onECores) moved[kv.Key] = kv.Value;
        if (mask != 0)
        {
            int session = Process.GetCurrentProcess().SessionId;
            GetWindowThreadProcessId(GetForegroundWindow(), out var front);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.SessionId != session || p.Id == Environment.ProcessId) continue;
                    if (_gameRunning && p.Id == front) continue;   // may be the game under another name
                    if (ResourceManagers.LooksLikeGame(p.Id, p.ProcessName)) continue;   // never opened to change, not even to undo
                    if (RestoreRules.GiveCoresBack(p.Id, WinResourceManager.StartTicks(p.Id), since, moved)) AllCores(p.Id, mask);
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
        }
        _onECores.Clear();
        _ecoreChecked.Clear();
        _ecoreSince = 0;
    }

    // Read first; a program is opened with the right to change it only when it sits on exactly the E-cores.
    private static void AllCores(int pid, ulong mask)
    {
        var q = OpenProcess(ProcessQueryLimited, false, pid);
        if (q == IntPtr.Zero) return;
        bool onE;
        try { onE = GetProcessAffinityMask(q, out var own, out _) && (ulong)own == mask; }
        finally { CloseHandle(q); }
        if (!onE) return;
        var h = OpenProcess(ProcessSetInformation | ProcessQueryLimited, false, pid);
        if (h == IntPtr.Zero) return;
        if (GetProcessAffinityMask(h, out var mine, out var system) && (ulong)mine == mask) SetProcessAffinityMask(h, system);
        CloseHandle(h);
    }

    private bool OnHighPlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return false;
        var active = Marshal.PtrToStructure<Guid>(ptr);
        LocalFree(ptr);
        return IsHighPerformance(active);
    }

    private string ApplyPowerPlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return "전원 계획을 읽지 못했어요";
        var active = Marshal.PtrToStructure<Guid>(ptr);
        LocalFree(ptr);
        if (IsHighPerformance(active)) return $"이미 고성능 계획을 쓰고 있어요 ({PlanName(active)})";
        var target = FindHighPlan();
        if (target == null) return "이 PC에는 고성능 전원 계획이 없어요";
        if (!HasEntry("cpu.power", "power", "")) _s.Backups.Add(new BoostBackup { Feature = "cpu.power", Kind = "power", Value = active.ToString() });
        foreach (var b in _s.Backups.Where(b => b.Feature == "cpu.power" && b.Kind == "power")) b.SetTo = target.Value.ToString();
        Save();   // before the plan changes, so a crash in between still finds it
        var g = target.Value;
        if (PowerSetActiveScheme(IntPtr.Zero, ref g) != 0)
        {
            _s.Backups.RemoveAll(b => b.Feature == "cpu.power");
            return "전원 계획을 바꾸지 못했어요";
        }
        return $"전원 계획: {PlanName(active)} → {PlanName(g)}";
    }

    private static readonly Guid HighPlan = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePlan = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static readonly Guid PersonalitySetting = new("245d8541-3943-4422-b025-13a784f679b7");

    private static bool IsHighPerformance(Guid scheme)
    {
        if (scheme == HighPlan || scheme == UltimatePlan) return true;
        var buffer = new byte[16];
        uint size = 16;
        var sub = NoSubgroup;
        var setting = PersonalitySetting;
        return PowerReadACValue(IntPtr.Zero, ref scheme, ref sub, ref setting, out _, buffer, ref size) == 0 && size == 16 && new Guid(buffer) == HighPlan;
    }

    private static Guid? FindHighPlan()
    {
        var plans = new List<Guid>();
        var buffer = new byte[16];
        for (uint i = 0; ; i++)
        {
            uint size = 16;
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, i, buffer, ref size) != 0) break;
            plans.Add(new Guid(buffer));
        }
        if (plans.Contains(UltimatePlan)) return UltimatePlan;
        if (plans.Contains(HighPlan)) return HighPlan;
        return plans.Where(IsHighPerformance).Cast<Guid?>().FirstOrDefault();
    }

    private static string PlanName(Guid scheme)
    {
        var buffer = new byte[256];
        uint size = (uint)buffer.Length;
        return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
            ? System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0') : scheme.ToString();
    }

    // ------------------------------------------------------------ notifications and updates
    private async Task<string> PauseInterruptionsAsync()
    {
        var done = new List<string>();
        QuietNotifications("sys.focus");   // or the stream mode has them off already
        done.Add("알림을 멈췄어요");
        if (_helper.Running)
        {
            var now = DateTime.UtcNow;
            var until = now.AddDays(1);
            var cur = ReadMachineString(UpdateUx, "PauseUpdatesExpiryTime");
            if (DateTime.TryParse(cur, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) && t > until && !_s.Backups.Any(b => b.Kind == "wu"))
                done.Add("Windows 업데이트는 이미 멈춰 있어요");
            else
            {
                foreach (var (start, end) in UpdatePauses)
                {
                    foreach (var (name, time) in new[] { (start, now), (end, until) })
                    {
                        var text = time.ToString(RestoreRules.PauseTimeFormat, CultureInfo.InvariantCulture);
                        var rec = _s.Backups.FirstOrDefault(b => b.Feature == "sys.focus" && b.Kind == "wu" && b.Name == name);
                        if (rec == null)
                        {
                            var old = ReadMachineString(UpdateUx, name);
                            _s.Backups.Add(rec = new BoostBackup { Feature = "sys.focus", Kind = "wu", Name = name, Existed = old != null, Value = old ?? "" });
                        }
                        rec.SetTo = text;   // kept before writing, so a crash in between still finds it
                        Save();
                        var failed = await HelperError("wu.set", name, text);
                        if (failed != null) return string.Join(", ", done) + ". 업데이트 일시정지는 실패했어요: " + failed;
                    }
                }
                done.Add("Windows 업데이트를 하루 동안 멈췄어요");
            }
        }
        return string.Join(", ", done);
    }

    // The setting is read when this per-user service starts. False when it could not be restarted
    // (a standard user often may not): the setting then applies from the next sign-in.
    private static bool RestartNotifications()
    {
        bool ok = true;
        try
        {
            foreach (var s in ServiceController.GetServices().Where(s => s.ServiceName.StartsWith("WpnUserService_", StringComparison.OrdinalIgnoreCase)))
            {
                using (s)
                {
                    try
                    {
                        if (s.Status == ServiceControllerStatus.Running)
                        {
                            s.Stop();
                            s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                        }
                        s.Start();
                        s.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                    }
                    catch (Exception) { ok = false; }
                }
            }
        }
        catch (Exception) { ok = false; }
        return ok;
    }

    // ------------------------------------------------------------ network
    private const long BigTransferBytesPerSecond = 5 * 1048576;
    private const int BigTransferSamples = 3;

    // The adapter total needs no rights. Which program it is comes from the helper's
    // per-connection statistics (TCP), so without the helper the notice has no name.
    private async Task WatchNetworkAsync()
    {
        long total = 0;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try { var s = n.GetIPStatistics(); total += s.BytesReceived + s.BytesSent; }
            catch (Exception) { }
        }
        var now = DateTime.UtcNow;
        double seconds = (now - _netTime).TotalSeconds;
        double rate = _netTime == default || seconds <= 0 ? 0 : (total - _netLast) / seconds;
        _netLast = total;
        _netTime = now;
        if (rate < BigTransferBytesPerSecond) { _netHigh = 0; _netByProcess = null; return; }
        var top = await TopNetworkProcessAsync();
        _netHigh++;
        if (_netHigh < BigTransferSamples || now - _netNotified < TimeSpan.FromSeconds(60)) return;
        if (top != null && (ResourceManagers.Games.Contains(top) || top == ResourceManagers.GameProcess)) return;   // the game's own traffic
        if (top != null && StreamApps.Contains(top)) return;   // the broadcast itself
        _netNotified = now;
        var text = top != null ? $"큰 네트워크 전송: {rate / 1048576:0.0} MB/s · {top}" : $"큰 네트워크 전송: {rate / 1048576:0.0} MB/s";
        Log(text);
        Notice?.Invoke(text);
    }

    private async Task<string?> TopNetworkProcessAsync()
    {
        if (!_helper.Running) return null;
        var (ok, data) = await _helper.CallAsync("net.top");
        if (!ok) return null;
        var current = new Dictionary<int, long>();
        foreach (var part in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            if (kv.Length == 2 && int.TryParse(kv[0], out var pid) && long.TryParse(kv[1], out var bytes)) current[pid] = bytes;
        }
        var last = _netByProcess;
        _netByProcess = current;
        if (last == null) return null;
        var best = current.Select(kv => (pid: kv.Key, delta: kv.Value - last.GetValueOrDefault(kv.Key))).OrderByDescending(x => x.delta).FirstOrDefault();
        if (best.delta <= 0) return null;
        try { using var p = Process.GetProcessById(best.pid); return p.ProcessName; }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------ Win32
    private const uint ProcessSetQuota = 0x0100, ProcessSetInformation = 0x0200, ProcessQueryLimited = 0x1000;
    private const uint MbOk = 0x0, MbYesNo = 0x4, MbIconWarning = 0x30, MbSetForeground = 0x10000, MbTopmost = 0x40000;
    private const int IdYes = 6;

    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32")] private static extern IntPtr LocalFree(IntPtr p);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("psapi", SetLastError = true)] private static extern bool EmptyWorkingSet(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetSystemCpuSetInformation(IntPtr info, uint length, out uint returned, IntPtr process, uint flags);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr own, out UIntPtr system);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);
    [DllImport("powrprof")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint access, uint index, byte[] buffer, ref uint size);
    [DllImport("powrprof")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, ref uint size);
    [DllImport("powrprof")] private static extern uint PowerReadACValue(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint type, byte[] buffer, ref uint size);
}
