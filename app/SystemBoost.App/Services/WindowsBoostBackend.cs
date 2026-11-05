// Windows boost features. Every change saves the previous value first
// (boost-state.json) and can be undone one by one or all at once.
// The game process itself is never opened or modified: games are recognised by
// name in the process list, and per-game settings go through Windows or the driver.
//   Stays until turned off   gpu.highperf, gpu.hags, gpu.driver, cpu.priority,
//                            net.adapterpower, sys.gamemode, sys.dvr
//   Only while a game runs   ram.standby, ram.workingset, cpu.ecores, cpu.power,
//                            net.monitor, sys.focus, boost.profiles
//   Only while the app runs  gpu.oc
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
    public string Kind { get; set; } = "";     // hkcu, hags, ifeo, netpm, nv, power, wu, ecores, oc
    public string Target { get; set; } = "";   // game path, exe name or adapter
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public bool Dword { get; set; }
    public string Value { get; set; } = "";
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
    public GpuChoice Gpu { get; set; } = new();
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new();
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsBoostBackend : IBoostBackend, IDisposable
{
    private const string GameBar = @"Software\Microsoft\GameBar";
    private const string GameConfig = @"System\GameConfigStore";
    private const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string PushNotifications = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string GpuPreferences = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";
    private const string UpdateUx = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const int HighPriorityClass = 3;
    private const int ExitWaitMs = 5000;

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
    // whatever a launcher starts would inherit its affinity.
    private static readonly HashSet<string> KeepOnPCores = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "obs32", "obs", "Streamlabs Desktop", "Streamlabs OBS", "XSplit.Core", "PRISMLiveStudio",
        "Discord", "DiscordPTB", "DiscordCanary", "TeamSpeak", "ts3client_win64", "mumble", "Skype", "Zoom",
        "ms-teams", "Teams", "KakaoTalk", "NVIDIA Broadcast", "nvsphelper64", "NVIDIA Share", "SteelSeriesSonar",
        "steam", "steamwebhelper", "EpicGamesLauncher", "Battle.net", "RiotClientServices", "GalaxyClient",
        "upc", "UbisoftConnect", "EADesktop", "Origin", "XboxPcApp", "GameBarPresenceWriter", "Launcher",
    };

    private readonly BoostHelper _helper = new();
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
        Changed?.Invoke();
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
        var ids = _s.Backups.Select(b => b.Feature).Concat(_s.Enabled).Distinct();
        return BoostCatalog.All.Where(f => ids.Contains(f.Id)).Select(f => f.Title).ToList();
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
        try { await RecoverAsync(); Save(); }
        catch (Exception e) { Log("복구하지 못했어요: " + e.Message); }
        finally { _gate.Release(); }
        Publish();
        _ = Task.Run(WatchLoopAsync);
    }

    // After a crash or a closed PC: what was only meant for the game goes back.
    private async Task RecoverAsync()
    {
        await PutGpuBackAsync();
        if (RunningGames(ProcessNames()) != null) return;
        if (_s.OneClick) await OneClickOffAsync();
        await EndGameTimeAsync();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _gate.Wait(ExitWaitMs);
            // called on the UI thread, which helper replies must not wait for
            Task.Run(async () =>
            {
                await EndGameTimeAsync();
                if (_s.OneClick) await OneClickOffAsync();
                // the helper does this too as it exits; here the backup is cleared as well
                var gpu = _s.Backups.Where(b => b.Kind == "oc").ToList();
                if (gpu.Count > 0 && _helper.Running) await RestoreAsync(gpu);
                Save();
            }).Wait(ExitWaitMs);
        }
        catch (Exception) { }
        _helper.Dispose();
    }

    /// Velopack uninstall hook: no window and no elevation here, so this covers
    /// the current user's settings. Admin-only ones stay until "전체 되돌리기".
    public static void RestoreUserSettings()
    {
        var s = Load();
        foreach (var b in Enumerable.Reverse(s.Backups))
        {
            try
            {
                if (b.Kind == "hkcu") RestoreUserValue(b);
                else if (b.Kind == "power" && Guid.TryParse(b.Value, out var g)) PowerSetActiveScheme(IntPtr.Zero, ref g);
            }
            catch (Exception) { }
        }
        if (s.Backups.Any(b => b.Key == PushNotifications)) RestartNotifications();
    }

    // ------------------------------------------------------------ interface
    public Task<bool> IsAppliedAsync(string id) => Task.FromResult(id switch
    {
        "boost.oneclick" => _s.OneClick,
        "gpu.hags" => Has(id) || ReadMachineDword(GraphicsDrivers, "HwSchMode") == 2,
        _ when GameTime.Contains(id) => _s.Enabled.Contains(id),
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
            int count = ChangedItems().Count;
            if (count == 0) return "변경된 것이 없어요";
            _s.OneClick = false;
            _s.OneClickOwned.Clear();
            _s.Enabled.Clear();
            _profileItems.Clear();
            if (_s.Backups.Any(IsAdminKind))
            {
                var e = await _helper.EnsureAsync();
                if (e != null) { await RestoreAsync(_s.Backups.Where(b => !IsAdminKind(b)).ToList()); Save(); return e; }
            }
            var failed = await RestoreAsync(_s.Backups.ToList());
            Save();
            return failed == null ? $"{count}개 항목을 원래대로 돌렸어요" : $"일부를 되돌리지 못했어요: {failed}";
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
            case "boost.profiles" when !License.Allowed(PremiumFeature.PerGameProfiles): return "프리미엄 기능이에요 (바뀐 것 없음)";
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
            var e = await RestoreOneAsync(b);
            if (e != null) { failed = e; continue; }
            notifications |= b.Key == PushNotifications;
            _s.Backups.Remove(b);
        }
        if (notifications) RestartNotifications();
        return failed;
    }

    private async Task<string?> RestoreOneAsync(BoostBackup b)
    {
        switch (b.Kind)
        {
            case "hkcu": RestoreUserValue(b); return null;
            case "power":
                var g = Guid.Parse(b.Value);
                return PowerSetActiveScheme(IntPtr.Zero, ref g) == 0 ? null : "전원 계획";
            case "ecores": ClearCores(); return null;
            case "hags": return await HelperError(b.Existed ? new[] { "hags.set", b.Value } : new[] { "hags.del" });
            case "ifeo": return await HelperError(b.Existed ? new[] { "ifeo.set", b.Target, b.Value } : new[] { "ifeo.del", b.Target });
            case "netpm": return await HelperError("net.pm.set", b.Target, "1");
            case "nv": return await HelperError("nv.restore", b.Target, b.Name, b.Value);
            case "oc": return await RestoreGpuAsync(b);
            case "wu": return await HelperError(b.Existed ? new[] { "wu.set", b.Name, b.Value } : new[] { "wu.del", b.Name });
            default: return null;
        }
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
                _s.Backups.Add(new BoostBackup { Feature = id, Kind = "hkcu", Key = key, Name = name, Dword = true, Existed = cur != null, Value = Convert.ToString(cur, CultureInfo.InvariantCulture) ?? "" });
                Save();
            }
            k.SetValue(name, value, RegistryValueKind.DWord);
            report.Add($"{name} {cur ?? "없음"} → {k.GetValue(name)}");
        }
        return string.Join(", ", report);
    }

    private static void RestoreUserValue(BoostBackup b)
    {
        using var k = Registry.CurrentUser.CreateSubKey(b.Key);
        if (!b.Existed) k.DeleteValue(b.Name, false);
        else if (b.Dword) k.SetValue(b.Name, int.Parse(b.Value, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        else k.SetValue(b.Name, b.Value, RegistryValueKind.String);
    }

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
                _s.Backups.Add(new BoostBackup { Feature = "gpu.highperf", Kind = "hkcu", Target = game, Key = GpuPreferences, Name = game, Existed = cur != null, Value = cur ?? "" });
                Save();
            }
            var others = (cur ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Where(t => !t.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase));
            k.SetValue(game, string.Concat(others.Select(t => t + ";")) + "GpuPreference=2;");
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
            if (!HasEntry("cpu.priority", "ifeo", exe))
            {
                _s.Backups.Add(new BoostBackup { Feature = "cpu.priority", Kind = "ifeo", Target = exe, Existed = cur != null, Value = cur?.ToString(CultureInfo.InvariantCulture) ?? "" });
                Save();
            }
            var failed = await HelperError("ifeo.set", exe, HighPriorityClass.ToString(CultureInfo.InvariantCulture));
            if (failed != null) return "실패했어요: " + failed;
            report.Add($"{exe} {cur?.ToString() ?? "없음"} → {MachineText(key, "CpuPriorityClass")}");
        }
        return string.Join(", ", report) + ". 다음 실행부터 적용돼요";
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
        if (_s.Enabled.Count == 0 && !_s.OneClick && !_gameRunning) return;
        var game = RunningGames(ProcessNames());
        if (game != null && !_gameRunning)
        {
            _gameRunning = true;
            _gameName = game;
            Log($"{game} 실행을 감지했어요");
            foreach (var id in _s.Enabled.ToList()) Log(await ActivateAsync(id));
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
                if (!HasEntry(id, "ecores", "")) { _s.Backups.Add(new BoostBackup { Feature = id, Kind = "ecores" }); Save(); }
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
        foreach (var id in GameTime)
        {
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
        if (_s.Enabled.Contains("net.monitor")) await WatchNetworkAsync();
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
                    if (p.SessionId != session) continue;
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

    private int MoveToEfficientCores()
    {
        var mask = EfficientMask();
        if (mask == 0) return 0;
        int moved = 0, session = Process.GetCurrentProcess().SessionId;
        // A launcher on the game list can start the real game under another name, so the
        // window in front always keeps all cores.
        GetWindowThreadProcessId(GetForegroundWindow(), out var front);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != session || p.Id == Environment.ProcessId) continue;
                var name = p.ProcessName;
                if (ResourceManagers.IsProtected(name) || KeepOnPCores.Contains(name)) continue;
                var h = OpenProcess(ProcessSetInformation | ProcessQueryLimited, false, p.Id);
                if (h == IntPtr.Zero) continue;
                // a process that limited itself keeps its own setting
                if (GetProcessAffinityMask(h, out var own, out var system))
                {
                    if (p.Id == front) { if ((ulong)own == mask) SetProcessAffinityMask(h, system); }
                    else if ((ulong)own == mask) moved++;
                    else if (own == system && SetProcessAffinityMask(h, (UIntPtr)mask)) moved++;
                }
                CloseHandle(h);
            }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return moved;
    }

    private static void ClearCores()
    {
        var mask = EfficientMask();
        if (mask == 0) return;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var h = OpenProcess(ProcessSetInformation | ProcessQueryLimited, false, p.Id);
                if (h == IntPtr.Zero) continue;
                if (GetProcessAffinityMask(h, out var own, out var system) && (ulong)own == mask) SetProcessAffinityMask(h, system);
                CloseHandle(h);
            }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
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
        if (!HasEntry("cpu.power", "power", "")) { _s.Backups.Add(new BoostBackup { Feature = "cpu.power", Kind = "power", Value = active.ToString() }); Save(); }
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
        if (!HasEntry("sys.focus", "hkcu", "", PushNotifications, "ToastEnabled"))
        {
            ApplyUserValues("sys.focus", (PushNotifications, "ToastEnabled", 0));
            RestartNotifications();
        }
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
                        if (!HasEntry("sys.focus", "wu", "", "", name))
                        {
                            var old = ReadMachineString(UpdateUx, name);
                            _s.Backups.Add(new BoostBackup { Feature = "sys.focus", Kind = "wu", Name = name, Existed = old != null, Value = old ?? "" });
                            Save();
                        }
                        var failed = await HelperError("wu.set", name, time.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                        if (failed != null) return string.Join(", ", done) + ". 업데이트 일시정지는 실패했어요: " + failed;
                    }
                }
                done.Add("Windows 업데이트를 하루 동안 멈췄어요");
            }
        }
        return string.Join(", ", done);
    }

    // The setting is read when this per-user service starts.
    private static void RestartNotifications()
    {
        try
        {
            foreach (var s in ServiceController.GetServices().Where(s => s.ServiceName.StartsWith("WpnUserService_", StringComparison.OrdinalIgnoreCase)))
            {
                using (s)
                {
                    if (s.Status == ServiceControllerStatus.Running)
                    {
                        s.Stop();
                        s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                    }
                    s.Start();
                    s.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (Exception) { /* the setting still applies from the next sign-in */ }
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
