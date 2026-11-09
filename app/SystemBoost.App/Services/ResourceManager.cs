// Per-app resource sharing: measure CPU / RAM / GPU per app and decide who gets more.
// Principle: the game process itself is never opened or modified (anti-cheat
// safety). The game gets more by lowering everything else. A process that may be a
// game (LooksLikeGame) is only ever read the way Task Manager reads it.
//
// Windows (same-user processes, no admin needed):
//   Priority   High-ish/Normal/BelowNormal/Idle priority class
//   Efficiency EcoQoS (ProcessPowerThrottling) for lowered apps
//   CPU cap    Job Object CPU rate control (hard limit, e.g. 20 %)
//   Memory     ProcessMemoryPriority low -> its pages are trimmed first
//   GPU        per-app graphics preference "power saving" (iGPU) for lowered apps
//              (HKCU\Software\Microsoft\DirectX\UserGpuPreferences, applies on the app's next start)
// macOS (user processes):
//   CPU        taskpolicy -b (background QoS: CPU + I/O throttled) / -B to undo
//   RAM, GPU   measured only - macOS gives apps no control over other apps
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

public enum ShareLevel { Priority, Normal, Lower, Limit }

public sealed class AppSample
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public double Cpu { get; set; }       // % of all cores
    public double RamMB { get; set; }
    public double Gpu { get; set; } = -1; // % (-1 = not available)
    public bool Game { get; set; }        // may be a game: shown, never changed
}

public interface IResourceManager : IDisposable
{
    bool CanCapCpu { get; }
    bool CanControlRam { get; }
    bool CanControlGpu { get; }
    bool MeasuresGpu { get; }
    Task<List<AppSample>> SampleAsync();
    /// cpuCapPercent only for ShareLevel.Limit. Returns a short result text.
    Task<string> SetLevelAsync(AppSample app, ShareLevel level, int cpuCapPercent = 25);
    /// Undo everything this session changed (also called on exit).
    Task RestoreAllAsync();
}

public static class ResourceManagers
{
    public static IResourceManager Create() =>
        OperatingSystem.IsWindows() ? new WinResourceManager() : new MacResourceManager();

    /// Process of the window frame generation is attached to, while it runs.
    public static string? GameProcess { get; set; }

    /// Process names of the games on the boost page.
    public static HashSet<string> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsProtected(string name) =>
        Protected.Contains(name) || Games.Contains(name) || string.Equals(name, GameProcess, StringComparison.OrdinalIgnoreCase);

    public static string? ProcessOfWindow(GameWindow w)
    {
        if (!OperatingSystem.IsWindows()) return string.IsNullOrEmpty(w.App) ? null : w.App;
        try
        {
            GetWindowThreadProcessId((IntPtr)w.Id, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception) { return null; }
    }

    /// Folders of the games on the boost page, each ending in a backslash.
    public static List<string> GameFolders { get; set; } = new();

    /// True for a game or anything that may belong to one: the games on the boost page and
    /// what lives next to them, anti-cheat programs, whatever covers a whole screen, programs
    /// in the folders game stores install into, and every game Windows itself has recognised
    /// (Game Bar's list). Such a process is never opened with a right that could change it.
    /// Finding out reads only its path, which Task Manager reads too.
    public static bool LooksLikeGame(int pid, string name)
    {
        if (IsProtected(name)) return true;
        return OperatingSystem.IsWindows() && LooksLikeGameWin(pid);
    }

    [SupportedOSPlatform("windows")]
    private static bool LooksLikeGameWin(int pid)
    {
        if (FullscreenPids().Contains(pid)) return true;
        var path = ImagePath(pid);
        if (path == null) return true;   // even its path is closed to us: leave it alone
        if (GameFolderMarks.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase))) return true;
        if (GameFolders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase))) return true;
        return KnownGames().Contains(path);
    }

    // Folders that game stores and publishers install into.
    private static readonly string[] GameFolderMarks =
    {
        @"\steamapps\", @"\Epic Games\", @"\Riot Games\", @"\XboxGames\", @"\Ubisoft Game Launcher\games\",
        @"\EA Games\", @"\Electronic Arts\", @"\GOG Galaxy\Games\", @"\Rockstar Games\", @"\Roblox\Versions\",
        @"\Roberts Space Industries\", @"\Nexon\", @"\Kakao Games\", @"\Smilegate\", @"\Netmarble\", @"\NCSOFT\",
        @"\PlayNC\", @"\Pearl Abyss\", @"\Games\",
    };

    private static readonly object CacheLock = new();
    private static readonly Dictionary<int, string?> Paths = new();
    private static DateTime _pathsAt = DateTime.MinValue;
    private static HashSet<int> _fullscreen = new();
    private static DateTime _fullscreenAt = DateTime.MinValue;
    private static HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _knownAt = DateTime.MinValue;

    /// The program file of a process, null when Windows does not show it to us.
    /// The handle can only query, the same right Task Manager uses.
    public static string? ImagePath(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        lock (CacheLock)
        {
            if (DateTime.UtcNow - _pathsAt > TimeSpan.FromSeconds(30)) { Paths.Clear(); _pathsAt = DateTime.UtcNow; }
            if (Paths.TryGetValue(pid, out var cached)) return cached;
        }
        string? path = null;
        var h = OpenProcess(QueryLimited, false, pid);
        if (h != IntPtr.Zero)
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref len)) path = sb.ToString(0, len);
            CloseHandle(h);
        }
        lock (CacheLock) Paths[pid] = path;
        return path;
    }

    // Processes with a window over a whole monitor: borderless and fullscreen games. A
    // maximised window with a title bar is an ordinary program.
    [SupportedOSPlatform("windows")]
    private static HashSet<int> FullscreenPids()
    {
        lock (CacheLock) if (DateTime.UtcNow - _fullscreenAt < TimeSpan.FromSeconds(2)) return _fullscreen;
        var set = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || !GetWindowRect(hwnd, out var r)) return true;
            if (IsZoomed(hwnd) && (GetWindowLong(hwnd, GwlStyle) & WsCaption) == WsCaption) return true;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var mon = MonitorFromWindow(hwnd, 0 /* MONITOR_DEFAULTTONULL */);
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return true;
            if (r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom)
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                set.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        set.Remove(Environment.ProcessId);
        lock (CacheLock) { _fullscreen = set; _fullscreenAt = DateTime.UtcNow; }
        return set;
    }

    // Windows keeps the games it has recognised for Game Mode and Game Bar here.
    [SupportedOSPlatform("windows")]
    private static HashSet<string> KnownGames()
    {
        lock (CacheLock) if (DateTime.UtcNow - _knownAt < TimeSpan.FromMinutes(1)) return _known;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (k != null)
                foreach (var sub in k.GetSubKeyNames())
                {
                    using var c = k.OpenSubKey(sub);
                    if (c?.GetValue("MatchedExeFullPath") is string p && p.Length > 0) set.Add(p);
                }
        }
        catch (Exception) { }
        lock (CacheLock) { _known = set; _knownAt = DateTime.UtcNow; }
        return set;
    }

    private const uint QueryLimited = 0x1000;          // PROCESS_QUERY_LIMITED_INFORMATION
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr h);

    // Never touch these (system, the game we capture, ourselves, anti-cheat programs).
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "dwm",
        "explorer", "audiodg", "fontdrvhost", "SystemBoost", "sb_framegen", "sb-framegen-mac",
        "kernel_task", "launchd", "WindowServer", "loginwindow", "Dock", "Finder",
        "StarCitizen", "RSI Launcher", "sb_helper",
        // anti-cheat: Easy Anti-Cheat, BattlEye, Vanguard, FACEIT, ESEA, EA, PunkBuster
        "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_Setup", "start_protected_game", "BEService", "BEService_x64",
        "vgc", "vgtray", "vgm", "FACEITClient", "faceit", "FACEITService", "ESEAClient",
        "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher", "PnkBstrA", "PnkBstrB",
        // nProtect GameGuard, XIGNCODE3, Nexon BlackCipher, Tencent ACE, NetEase
        "GameMon", "GameMon.des", "GameMon64", "GameMon64.des", "GameGuard.des", "npggNT.des",
        "xcoronahost.xem", "xxd-0.xem", "x3.xem", "BlackCipher.aes", "BlackCipher64.aes", "BlackCall.aes", "BlackCall64.aes",
        "SGuard64", "SGuardSvc64", "ACE-Tray", "NeacClient",
    };
}

// =============================================================== Windows
public sealed class WinResourceManager : IResourceManager
{
    public bool CanCapCpu => true;
    public bool CanControlRam => true;
    public bool CanControlGpu => true;
    public bool MeasuresGpu => _gpu != null;

    private readonly Dictionary<int, (TimeSpan cpu, DateTime t)> _last = new();
    private readonly Dictionary<int, (uint prio, string name, string? exe)> _changed = new();
    private readonly Dictionary<int, IntPtr> _jobs = new();
    private readonly Dictionary<string, string?> _gpuOrig = new(StringComparer.OrdinalIgnoreCase);
    private readonly GpuCounters? _gpu = GpuCounters.TryCreate();

    public Task<List<AppSample>> SampleAsync() => Task.Run(() =>
    {
        var now = DateTime.UtcNow;
        var gpu = _gpu?.Read() ?? new Dictionary<int, double>();
        var list = new List<AppSample>();
        int me = Environment.ProcessId, session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == me || p.SessionId != session || ResourceManagers.IsProtected(p.ProcessName)) continue;
                var cpu = p.TotalProcessorTime;
                double pct = 0;
                if (_last.TryGetValue(p.Id, out var prev))
                    pct = (cpu - prev.cpu).TotalMilliseconds / Math.Max(1, (now - prev.t).TotalMilliseconds) / Environment.ProcessorCount * 100;
                _last[p.Id] = (cpu, now);
                list.Add(new AppSample { Pid = p.Id, Name = p.ProcessName, Cpu = pct, RamMB = p.PrivateMemorySize64 / 1048576.0,
                                         Gpu = gpu.TryGetValue(p.Id, out var g) ? g : (_gpu != null ? 0 : -1),
                                         Game = ResourceManagers.LooksLikeGame(p.Id, p.ProcessName) });
            }
            catch (Exception) { /* access denied / exited */ }
            finally { p.Dispose(); }
        }
        // merge multi-process apps (chrome, msedge, discord...) by name
        return list.GroupBy(a => a.Name).Select(g => new AppSample
        {
            Pid = g.OrderByDescending(a => a.RamMB).First().Pid, Name = g.Key,
            Cpu = g.Sum(a => a.Cpu), RamMB = g.Sum(a => a.RamMB), Gpu = g.Any(a => a.Gpu >= 0) ? g.Sum(a => Math.Max(0, a.Gpu)) : -1,
            Game = g.Any(a => a.Game),
        }).ToList();
    });

    public Task<string> SetLevelAsync(AppSample app, ShareLevel level, int cap = 25) => Task.Run(() =>
    {
        const string Leave = "{0}: 게임과 시스템 프로세스는 바꾸지 않아요";
        if (app.Game || ResourceManagers.IsProtected(app.Name)) return string.Format(Leave, app.Name);
        int done = 0, failed = 0, games = 0;
        foreach (var p in Process.GetProcessesByName(app.Name))
        {
            try
            {
                if (ResourceManagers.LooksLikeGame(p.Id, app.Name)) { games++; continue; }
                Apply(p, level, cap);
                done++;
            }
            catch (Exception) { failed++; }
            finally { p.Dispose(); }
        }
        if (done == 0 && games > 0) return string.Format(Leave, app.Name);
        string what = level switch
        {
            ShareLevel.Priority => "우선 (CPU 우선순위 높음)",
            ShareLevel.Normal => "보통 (원래대로)",
            ShareLevel.Lower => "낮춤 (우선순위·메모리 우선순위 낮춤, 효율 모드, 다음 실행부터 내장 GPU)",
            _ => $"제한 (CPU 최대 {cap}%)",
        };
        return failed > 0 && done == 0 ? $"{app.Name}: 권한이 없어 바꾸지 못했어요" : $"{app.Name}: {what}";
    });

    // Only the rights each change needs: no memory access, and no right to end the program
    // except for the CPU cap, where Windows asks for it to put the program in a job.
    private const uint SetInformation = 0x0200, SetQuota = 0x0100, Terminate = 0x0001, QueryLimited = 0x1000;
    private const uint AboveNormalClass = 0x8000, BelowNormalClass = 0x4000;

    private void Apply(Process p, ShareLevel level, int cap)
    {
        uint rights = SetInformation | QueryLimited | (level == ShareLevel.Limit ? SetQuota | Terminate : 0);
        var h = OpenProcess(rights, false, p.Id);
        if (h == IntPtr.Zero) throw new InvalidOperationException("open");
        try
        {
            if (!_changed.ContainsKey(p.Id))
            {
                uint prio = GetPriorityClass(h);
                if (prio == 0) throw new InvalidOperationException("priority");
                _changed[p.Id] = (prio, p.ProcessName, ResourceManagers.ImagePath(p.Id));
            }
            var orig = _changed[p.Id];
            switch (level)
            {
                case ShareLevel.Priority:
                    SetPriorityClass(h, AboveNormalClass);   // never High/Realtime: input & audio would suffer
                    SetEco(h, false); SetMemPrio(h, 5); SetCpuCap(p.Id, h, 0);
                    break;
                case ShareLevel.Normal:
                    SetPriorityClass(h, orig.prio);
                    SetEco(h, false); SetMemPrio(h, 5); SetCpuCap(p.Id, h, 0); SetGpuPref(orig.exe, false);
                    break;
                case ShareLevel.Lower:
                    SetPriorityClass(h, BelowNormalClass);
                    SetEco(h, true); SetMemPrio(h, 2); SetCpuCap(p.Id, h, 0); SetGpuPref(orig.exe, true);
                    break;
                case ShareLevel.Limit:
                    SetPriorityClass(h, BelowNormalClass);
                    SetEco(h, true); SetMemPrio(h, 2); SetCpuCap(p.Id, h, Math.Clamp(cap, 5, 90)); SetGpuPref(orig.exe, true);
                    break;
            }
        }
        finally { CloseHandle(h); }
    }

    public Task RestoreAllAsync() => Task.Run(() =>
    {
        foreach (var (pid, orig) in _changed.ToList())
        {
            try
            {
                // the number may belong to another program by now
                using var p = Process.GetProcessById(pid);
                if (string.Equals(p.ProcessName, orig.name, StringComparison.OrdinalIgnoreCase))
                {
                    var h = OpenProcess(SetInformation | QueryLimited, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        SetPriorityClass(h, orig.prio); SetEco(h, false); SetMemPrio(h, 5);
                        CloseHandle(h);
                    }
                    SetCpuCap(pid, IntPtr.Zero, 0);
                }
            }
            catch (Exception) { }
            SetGpuPref(orig.exe, false);
        }
        _changed.Clear();
        foreach (var j in _jobs.Values) CloseHandle(j);
        _jobs.Clear();
    });

    public void Dispose() { RestoreAllAsync().Wait(3000); _gpu?.Dispose(); }

    // ---- Win32 -----------------------------------------------------------
    private void SetCpuCap(int pid, IntPtr h, int percent)
    {
        if (!_jobs.TryGetValue(pid, out var job))
        {
            if (percent == 0) return;
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero || !AssignProcessToJobObject(job, h)) { if (job != IntPtr.Zero) CloseHandle(job); throw new InvalidOperationException("job"); }
            _jobs[pid] = job;
        }
        var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = percent > 0 ? 0x1u | 0x4u : 0u,   // ENABLE | HARD_CAP, or off
            CpuRate = (uint)(percent * 100),                 // 1/100 of a percent
        };
        SetInformationJobObject(job, 15 /* JobObjectCpuRateControlInformation */, ref info, (uint)Marshal.SizeOf(info));
    }

    private static void SetEco(IntPtr h, bool on)
    {
        var s = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 0x1, StateMask = on ? 0x1u : 0u };
        SetProcessInformation(h, 4 /* ProcessPowerThrottling */, ref s, (uint)Marshal.SizeOf(s));
    }

    private static void SetMemPrio(IntPtr h, uint prio)   // 5 = normal, 2 = low
    {
        var m = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = prio };
        SetProcessInformation(h, 0 /* ProcessMemoryPriority */, ref m, (uint)Marshal.SizeOf(m));
    }

    // lowered: GpuPreference=1 (power saving, iGPU), keeping the value the user had; otherwise put it back
    private void SetGpuPref(string? exe, bool lowered)
    {
        if (exe == null || !OperatingSystem.IsWindows()) return;
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        if (lowered)
        {
            _gpuOrig.TryAdd(exe, k.GetValue(exe) as string);
            k.SetValue(exe, "GpuPreference=1;");
        }
        else if (_gpuOrig.Remove(exe, out var orig))
        {
            if (orig != null) k.SetValue(exe, orig); else k.DeleteValue(exe, false);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION { public uint ControlFlags; public uint CpuRate; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)] private struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info, uint len);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, uint len);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr h, int cls, ref MEMORY_PRIORITY_INFORMATION info, uint len);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] private static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetPriorityClass(IntPtr h, uint cls);
}

/// Per-process GPU utilisation from the "GPU Engine" performance counters
/// (instance names contain pid_<n>; same data as Task Manager's GPU column).
internal sealed class GpuCounters : IDisposable
{
    private readonly PerformanceCounterCategory _cat;
    private readonly Dictionary<string, PerformanceCounter> _counters = new();
    private GpuCounters(PerformanceCounterCategory c) { _cat = c; }

    public static GpuCounters? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return PerformanceCounterCategory.Exists("GPU Engine") ? new GpuCounters(new PerformanceCounterCategory("GPU Engine")) : null; }
        catch (Exception) { return null; }
    }

    public Dictionary<int, double> Read()
    {
        var byPid = new Dictionary<int, double>();
        if (!OperatingSystem.IsWindows()) return byPid;
        try
        {
            foreach (var inst in _cat.GetInstanceNames())
            {
                if (!inst.Contains("engtype_3D")) continue;
                int i = inst.IndexOf("pid_", StringComparison.Ordinal);
                if (i < 0 || !int.TryParse(inst.AsSpan(i + 4).ToString().Split('_')[0], out int pid)) continue;
                if (!_counters.TryGetValue(inst, out var c))
                    _counters[inst] = c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                float v;
                try { v = c.NextValue(); } catch (Exception) { continue; }
                byPid[pid] = byPid.GetValueOrDefault(pid) + v;
            }
        }
        catch (Exception) { }
        return byPid;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var c in _counters.Values) c.Dispose();
    }
}

// =============================================================== macOS
public sealed class MacResourceManager : IResourceManager
{
    public bool CanCapCpu => false;
    public bool CanControlRam => false;
    public bool CanControlGpu => false;
    public bool MeasuresGpu => false;
    private readonly HashSet<int> _background = new();

    public async Task<List<AppSample>> SampleAsync()
    {
        var (_, o) = await Sh("/bin/ps", "-axco pid=,pcpu=,rss=,user=,comm=");
        var user = Environment.UserName;
        int cores = Environment.ProcessorCount;
        var list = new List<AppSample>();
        foreach (var line in o.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Trim().Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 5 || p[3] != user || !int.TryParse(p[0], out var pid) || pid == Environment.ProcessId) continue;
            var name = p[4].Trim();
            if (ResourceManagers.IsProtected(name)) continue;
            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu);
            double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var rss);
            list.Add(new AppSample { Pid = pid, Name = name, Cpu = cpu / cores, RamMB = rss / 1024 });
        }
        return list.GroupBy(a => a.Name).Select(g => new AppSample
        { Pid = g.First().Pid, Name = g.Key, Cpu = g.Sum(a => a.Cpu), RamMB = g.Sum(a => a.RamMB) }).ToList();
    }

    public async Task<string> SetLevelAsync(AppSample app, ShareLevel level, int cap = 25)
    {
        var (_, o) = await Sh("/usr/bin/pgrep", $"-x \"{app.Name.Replace("\"", "")}\"");
        var pids = o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var v) ? v : 0).Where(v => v > 0).ToList();
        if (pids.Count == 0) pids.Add(app.Pid);
        bool lower = level is ShareLevel.Lower or ShareLevel.Limit;
        foreach (var pid in pids)
        {
            await Sh("/usr/sbin/taskpolicy", $"{(lower ? "-b" : "-B")} -p {pid}");
            if (lower) _background.Add(pid); else _background.Remove(pid);
        }
        return level switch
        {
            ShareLevel.Priority => $"{app.Name}: macOS에서는 다른 앱을 올릴 수 없어서 '보통'으로 뒀어요",
            ShareLevel.Normal => $"{app.Name}: 원래대로",
            _ => $"{app.Name}: 백그라운드 모드 (CPU·디스크 사용을 뒤로 미뤄요)",
        };
    }

    public async Task RestoreAllAsync()
    {
        foreach (var pid in _background.ToList()) await Sh("/usr/sbin/taskpolicy", $"-B -p {pid}");
        _background.Clear();
    }

    public void Dispose() => RestoreAllAsync().Wait(3000);

    private static async Task<(int, string)> Sh(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var o = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, o);
        }
        catch (Exception e) { return (-1, e.Message); }
    }
}
