// macOS boost features that the OS actually allows and that have a measurable
// effect. Every change saves the previous state first (mac-backup.json) and can
// be undone one by one or all at once.
//   mac.lowpower   Low Power Mode off            (pmset, admin)
//   mac.tm         Time Machine auto backup off  (tmutil, admin)
//   mac.spotlight  Spotlight indexing paused     (mdutil, admin)
// Not possible on macOS (closed by the OS): RAM purging that helps games,
// GPU clocks, process priority boost, core affinity.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public sealed record AppUsage(int Pid, string Name, double Cpu, double MemMB)
{
    public override string ToString() => $"{Name}  ·  CPU {Cpu:0}%  ·  메모리 {MemMB:0} MB";
}

public sealed class MacBoostBackend : IBoostBackend
{
    private string BackupFile => Path.Combine(Platform.DataDir, "mac-backup.json");
    private Dictionary<string, string> _backup = new();

    public MacBoostBackend()
    {
        try { if (File.Exists(BackupFile)) _backup = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BackupFile)) ?? new(); }
        catch (Exception) { _backup = new(); }
    }

    public bool Connected => true;

    private void SaveBackup() => File.WriteAllText(BackupFile, JsonSerializer.Serialize(_backup));

    // ------------------------------------------------------------ status
    public async Task<bool> IsAppliedAsync(string id) => id switch
    {
        "mac.lowpower" => _backup.ContainsKey(id) && !await LowPowerOnAsync(),
        "mac.tm" => _backup.ContainsKey(id),
        "mac.spotlight" => _backup.ContainsKey(id),
        _ => false,
    };

    public static async Task<bool> LowPowerOnAsync()
    {
        var (_, o) = await Sh("/usr/bin/pmset", "-g");
        return o.Split('\n').Any(l => l.Trim().StartsWith("lowpowermode") && l.Trim().EndsWith("1"));
    }

    private static async Task<bool> TimeMachineAutoOnAsync()
    {
        var (code, o) = await Sh("/usr/bin/defaults", "read /Library/Preferences/com.apple.TimeMachine AutoBackup");
        return code == 0 && o.Trim() == "1";
    }

    private static async Task<bool> SpotlightOnAsync()
    {
        var (_, o) = await Sh("/usr/bin/mdutil", "-s /");
        return o.Contains("Indexing enabled", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ apply / revert
    public async Task<string> ApplyAsync(string id)
    {
        switch (id)
        {
            case "mac.lowpower":
            {
                if (!await LowPowerOnAsync()) return "저전력 모드가 이미 꺼져 있어요";
                var r = await Admin("pmset -a lowpowermode 0");
                if (r != null) return r;
                _backup[id] = "1"; SaveBackup();
                return "저전력 모드를 껐어요 (되돌리면 다시 켜져요)";
            }
            case "mac.tm":
            {
                if (!await TimeMachineAutoOnAsync()) return "Time Machine 자동 백업이 이미 꺼져 있어요";
                var r = await Admin("tmutil disable");
                if (r != null) return r;
                _backup[id] = "1"; SaveBackup();
                return "게임하는 동안 Time Machine 자동 백업을 멈췄어요";
            }
            case "mac.spotlight":
            {
                if (!await SpotlightOnAsync()) return "Spotlight 색인이 이미 꺼져 있어요";
                var r = await Admin("mdutil -a -i off");
                if (r != null) return r;
                _backup[id] = "1"; SaveBackup();
                return "Spotlight 색인을 멈췄어요 (되돌리면 다시 색인해요)";
            }
            default: return "macOS에서는 지원하지 않는 항목이에요";
        }
    }

    public async Task<string> RevertAsync(string id)
    {
        if (!_backup.ContainsKey(id)) return "변경한 적이 없어요";
        string? r = id switch
        {
            "mac.lowpower" => await Admin("pmset -a lowpowermode 1"),
            "mac.tm" => await Admin("tmutil enable"),
            "mac.spotlight" => await Admin("mdutil -a -i on"),
            _ => null,
        };
        if (r != null) return r;
        _backup.Remove(id); SaveBackup();
        return "원래대로 돌렸어요";
    }

    public async Task<string> RevertAllAsync()
    {
        if (_backup.Count == 0) return "변경된 것이 없어요";
        // one admin prompt for everything
        var cmds = new List<string>();
        if (_backup.ContainsKey("mac.lowpower")) cmds.Add("pmset -a lowpowermode 1");
        if (_backup.ContainsKey("mac.tm")) cmds.Add("tmutil enable");
        if (_backup.ContainsKey("mac.spotlight")) cmds.Add("mdutil -a -i on");
        var r = await Admin(string.Join(" ; ", cmds));
        if (r != null) return r;
        int n = _backup.Count;
        _backup.Clear(); SaveBackup();
        return $"{n}개 항목을 원래대로 돌렸어요";
    }

    // ------------------------------------------------------------ monitoring
    /// Top user apps by CPU (and memory). Excludes system daemons and SystemBoost itself.
    public static async Task<List<AppUsage>> TopAppsAsync(int count = 8)
    {
        var (_, o) = await Sh("/bin/ps", "-axco pid=,pcpu=,rss=,user=,comm=");
        var me = Environment.ProcessId;
        var user = Environment.UserName;
        var list = new List<AppUsage>();
        foreach (var line in o.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Trim().Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 5 || p[3] != user) continue;
            if (!int.TryParse(p[0], out var pid) || pid == me) continue;
            var name = p[4].Trim();
            if (name.StartsWith("sb-framegen") || name is "SystemBoost" or "Finder" or "Dock" or "WindowServer" or "loginwindow" or "SystemUIServer" or "ControlCenter") continue;
            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu);
            double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var rssKb);
            list.Add(new AppUsage(pid, name, cpu, rssKb / 1024));
        }
        return list.OrderByDescending(a => a.Cpu * 10 + a.MemMB / 100).Take(count).ToList();
    }

    /// Asks the app to quit normally (it can save work / ask the user).
    public static async Task QuitAppAsync(AppUsage a)
    {
        var name = a.Name.Replace("\"", "");
        // "is running" check first: telling a non-running app would launch it
        var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardError = true };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"if application \"{name}\" is running then tell application \"{name}\" to quit");
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    /// "정상" / "높음 (macOS가 성능을 낮추는 중)" etc.
    public static async Task<string> ThermalAsync()
    {
        var (_, o) = await Sh("/usr/bin/pmset", "-g therm");
        var limit = o.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("CPU_Speed_Limit"));
        if (limit != null && int.TryParse(limit.Split('=').Last().Trim(), out var pct) && pct < 100)
            return $"뜨거움 · CPU 속도 {pct}%로 제한 중";
        if (o.Contains("thermal warning level", StringComparison.OrdinalIgnoreCase) && !o.Contains("No thermal warning", StringComparison.OrdinalIgnoreCase))
            return "발열 경고 있음";
        return "정상";
    }

    public static async Task<string> MemoryPressureAsync()
    {
        var (_, o) = await Sh("/usr/bin/memory_pressure", "-Q");
        var line = o.Split('\n').FirstOrDefault(l => l.Contains("free percentage", StringComparison.OrdinalIgnoreCase));
        if (line != null && int.TryParse(new string(line.Where(char.IsDigit).ToArray()), out var free))
            return free < 15 ? $"여유 {free}% · 부족해요" : $"여유 {free}%";
        return "알 수 없음";
    }

    // ------------------------------------------------------------ helpers
    /// Runs a command as admin through the standard macOS password dialog. null = ok, else message.
    private static async Task<string?> Admin(string cmd)
    {
        var script = $"do shell script \"{cmd.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with administrator privileges";
        var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode == 0) return null;
        return err.Contains("-128") ? "취소했어요 (바뀐 것 없음)" : "실패했어요: " + err.Trim();
    }

    private static async Task<(int code, string output)> Sh(string exe, string args)
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
