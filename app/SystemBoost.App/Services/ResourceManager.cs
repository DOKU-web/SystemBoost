// Per-app resource sharing: measure CPU / RAM / GPU per app and decide who gets more.
// Principle: the game process itself is never opened or modified (anti-cheat
// safety). The game gets more by lowering everything else.
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

    // Never touch these (system, the game we capture, ourselves).
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "dwm",
        "explorer", "audiodg", "fontdrvhost", "SystemBoost", "sb_framegen", "sb-framegen-mac",
        "kernel_task", "launchd", "WindowServer", "loginwindow", "Dock", "Finder",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "vgc", "StarCitizen", "RSI Launcher",
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
    private readonly Dictionary<int, (ProcessPriorityClass prio, string name, string? exe)> _changed = new();
    private readonly Dictionary<int, IntPtr> _jobs = new();
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
                if (p.Id == me || p.SessionId != session || ResourceManagers.Protected.Contains(p.ProcessName)) continue;
                var cpu = p.TotalProcessorTime;
                double pct = 0;
                if (_last.TryGetValue(p.Id, out var prev))
                    pct = (cpu - prev.cpu).TotalMilliseconds / Math.Max(1, (now - prev.t).TotalMilliseconds) / Environment.ProcessorCount * 100;
                _last[p.Id] = (cpu, now);
                list.Add(new AppSample { Pid = p.Id, Name = p.ProcessName, Cpu = pct, RamMB = p.PrivateMemorySize64 / 1048576.0,
                                         Gpu = gpu.TryGetValue(p.Id, out var g) ? g : (_gpu != null ? 0 : -1) });
            }
            catch (Exception) { /* access denied / exited */ }
            finally { p.Dispose(); }
        }
        // merge multi-process apps (chrome, msedge, discord...) by name
        return list.GroupBy(a => a.Name).Select(g => new AppSample
        {
            Pid = g.OrderByDescending(a => a.RamMB).First().Pid, Name = g.Key,
            Cpu = g.Sum(a => a.Cpu), RamMB = g.Sum(a => a.RamMB), Gpu = g.Any(a => a.Gpu >= 0) ? g.Sum(a => Math.Max(0, a.Gpu)) : -1,
        }).ToList();
    });

    public Task<string> SetLevelAsync(AppSample app, ShareLevel level, int cap = 25) => Task.Run(() =>
    {
        int done = 0, failed = 0;
        foreach (var p in Process.GetProcessesByName(app.Name))
        {
            try { Apply(p, level, cap); done++; }
            catch (Exception) { failed++; }
            finally { p.Dispose(); }
        }
        string what = level switch
        {
            ShareLevel.Priority => "우선 (CPU 우선순위 높음)",
            ShareLevel.Normal => "보통 (원래대로)",
            ShareLevel.Lower => "낮춤 (우선순위·메모리 우선순위 낮춤, 효율 모드, 다음 실행부터 내장 GPU)",
            _ => $"제한 (CPU 최대 {cap}%)",
        };
        return failed > 0 && done == 0 ? $"{app.Name}: 권한이 없어 바꾸지 못했어요" : $"{app.Name}: {what}";
    });

    private void Apply(Process p, ShareLevel level, int cap)
    {
        if (!_changed.ContainsKey(p.Id))
        {
            string? exe = null;
            try { exe = p.MainModule?.FileName; } catch (Exception) { }
            _changed[p.Id] = (p.PriorityClass, p.ProcessName, exe);
        }
        var orig = _changed[p.Id];
        IntPtr h = p.Handle;
        switch (level)
        {
            case ShareLevel.Priority:
                p.PriorityClass = ProcessPriorityClass.AboveNormal;   // never High/Realtime: input & audio would suffer
                SetEco(h, false); SetMemPrio(h, 5); SetCpuCap(p, 0);
                break;
            case ShareLevel.Normal:
                p.PriorityClass = orig.prio;
                SetEco(h, false); SetMemPrio(h, 5); SetCpuCap(p, 0); SetGpuPref(orig.exe, null);
                break;
            case ShareLevel.Lower:
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
                SetEco(h, true); SetMemPrio(h, 2); SetCpuCap(p, 0); SetGpuPref(orig.exe, 1);
                break;
            case ShareLevel.Limit:
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
                SetEco(h, true); SetMemPrio(h, 2); SetCpuCap(p, Math.Clamp(cap, 5, 90)); SetGpuPref(orig.exe, 1);
                break;
        }
    }

    public Task RestoreAllAsync() => Task.Run(() =>
    {
        foreach (var (pid, orig) in _changed.ToList())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.PriorityClass = orig.prio;
                SetEco(p.Handle, false); SetMemPrio(p.Handle, 5); SetCpuCap(p, 0);
            }
            catch (Exception) { }
            SetGpuPref(orig.exe, null);
        }
        _changed.Clear();
        foreach (var j in _jobs.Values) CloseHandle(j);
        _jobs.Clear();
    });

    public void Dispose() { RestoreAllAsync().Wait(3000); _gpu?.Dispose(); }

    // ---- Win32 -----------------------------------------------------------
    private void SetCpuCap(Process p, int percent)
    {
        if (!_jobs.TryGetValue(p.Id, out var job))
        {
            if (percent == 0) return;
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero || !AssignProcessToJobObject(job, p.Handle)) { if (job != IntPtr.Zero) CloseHandle(job); throw new InvalidOperationException("job"); }
            _jobs[p.Id] = job;
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

    // GpuPreference=1 power saving (iGPU), 2 high performance; null = remove our entry
    private static void SetGpuPref(string? exe, int? pref)
    {
        if (exe == null || !OperatingSystem.IsWindows()) return;
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        if (pref is int v) k.SetValue(exe, $"GpuPreference={v};");
        else if (k.GetValue(exe) is string cur && cur.StartsWith("GpuPreference=1")) k.DeleteValue(exe, false);
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
            if (ResourceManagers.Protected.Contains(name)) continue;
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
