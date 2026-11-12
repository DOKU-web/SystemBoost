// macOS boost features that the OS actually allows and that have a measurable
// effect. Every change saves the previous state first (mac-backup.json) and can
// be undone one by one or all at once.
//   mac.lowpower   Low Power Mode off            (pmset, admin)  stays until undone; goes back per power source
//   mac.tm         Time Machine auto backup off  (tmutil, admin) only while SystemBoost runs: it is turned back on
//                  when the app quits, and a run that ended without that (a crash, a refused prompt) is caught
//                  at the next start. Backups are never left off without the user being asked.
//   mac.spotlight  Spotlight indexing paused     (mdutil, admin) stays until undone; only the volumes that were
//                  being indexed are paused and later turned back on
// Each administrator prompt says what it is for. Not possible on macOS (closed by the OS): RAM purging that
// helps games, GPU clocks, process priority boost, core affinity.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public sealed record AppUsage(int Pid, string Name, double Cpu, double MemMB)
{
    public override string ToString() => $"{Name}  ·  CPU {Cpu:0}%  ·  메모리 {MemMB:0} MB";
}

public sealed class MacBoostBackend : IBoostBackend, IDisposable
{
    private string BackupFile => Path.Combine(Platform.DataDir, "mac-backup.json");
    private Dictionary<string, string> _backup = new();
    private readonly SemaphoreSlim _gate = new(1, 1);   // the page, the start-up check and quitting can overlap
    private const int ExitWaitMs = 60000;   // time to answer the prompt as the app quits

    /// The instance of this app run, for the status panel.
    public static MacBoostBackend? Current { get; private set; }

    /// Set while Time Machine's automatic backup is still off because turning it back on was refused.
    public string? Warning { get; private set; }

    private const string TimeMachineStillOff =
        "Time Machine 자동 백업이 아직 꺼져 있어요. 'Time Machine 자동 백업 멈추기'를 되돌리거나 '백업 · 되돌리기'에서 다시 켜세요.";

    public MacBoostBackend()
    {
        try { if (File.Exists(BackupFile)) _backup = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BackupFile)) ?? new(); }
        catch (Exception) { _backup = new(); }
        Current = this;
        if (OperatingSystem.IsMacOS() && _backup.ContainsKey("mac.tm")) _ = Task.Run(RecoverTimeMachineAsync);
    }

    public bool Connected => true;

    private void SaveBackup() => File.WriteAllText(BackupFile, JsonSerializer.Serialize(_backup));

    // An earlier run ended with Time Machine's automatic backup still off: it crashed, was forced to quit, or the
    // prompt as it quit was refused. Turned back on now, with a prompt that says why; refused again, the panel says so.
    private async Task RecoverTimeMachineAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_backup.ContainsKey("mac.tm")) return;
            if (await TimeMachineAutoOnAsync()) { _backup.Remove("mac.tm"); SaveBackup(); return; }   // someone turned it on since
            var r = await Admin("tmutil enable", "지난번에 SystemBoost가 멈춘 Time Machine 자동 백업을 다시 켜요.");
            if (r == null) { _backup.Remove("mac.tm"); SaveBackup(); Warning = null; }
            else Warning = TimeMachineStillOff;
        }
        catch (Exception) { Warning = TimeMachineStillOff; }
        finally { _gate.Release(); }
    }

    /// As the app quits: Time Machine's automatic backup goes back on (one prompt). Refused or unanswered,
    /// the record stays and the next start asks again.
    public void Dispose()
    {
        if (!_backup.ContainsKey("mac.tm")) return;
        try
        {
            Task.Run(async () =>
            {
                await _gate.WaitAsync();
                try
                {
                    if (!_backup.ContainsKey("mac.tm")) return;
                    if (await Admin("tmutil enable", "SystemBoost를 끄기 전에, 멈춰 둔 Time Machine 자동 백업을 다시 켜요.") == null)
                    {
                        _backup.Remove("mac.tm");
                        SaveBackup();
                    }
                }
                finally { _gate.Release(); }
            }).Wait(ExitWaitMs);
        }
        catch (Exception) { }
    }

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
        await _gate.WaitAsync();
        try { return await ApplyCoreAsync(id); }
        finally { _gate.Release(); }
    }

    private async Task<string> ApplyCoreAsync(string id)
    {
        switch (id)
        {
            case "mac.lowpower":
            {
                if (!await LowPowerOnAsync()) return "저전력 모드가 이미 꺼져 있어요";
                // what each power source (battery, charger) had, so each goes back to its own
                var (_, custom) = await Sh("/usr/bin/pmset", "-g custom");
                var record = RestoreRules.LowPowerRecord(RestoreRules.LowPowerBySource(custom));
                var r = await Admin("pmset -a lowpowermode 0", "SystemBoost가 저전력 모드를 끄려고 해요. 되돌릴 때까지 꺼져 있어요.");
                if (r != null) return r;
                _backup[id] = record; SaveBackup();
                return "저전력 모드를 껐어요. 되돌릴 때까지 꺼져 있어요 (SystemBoost를 꺼도 그대로)";
            }
            case "mac.tm":
            {
                if (!await TimeMachineAutoOnAsync()) return "Time Machine 자동 백업이 이미 꺼져 있어요";
                var r = await Admin("tmutil disable", "SystemBoost가 Time Machine 자동 백업을 멈추려고 해요. SystemBoost를 끌 때 다시 켜요.");
                if (r != null) return r;
                _backup[id] = "1"; SaveBackup();
                return "Time Machine 자동 백업을 멈췄어요. SystemBoost를 끌 때 다시 켜요 (관리자 암호를 한 번 물어요)";
            }
            case "mac.spotlight":
            {
                if (!await SpotlightOnAsync()) return "Spotlight 색인이 이미 꺼져 있어요";
                // only the volumes being indexed now; one that was off stays off when this is undone
                var (_, status) = await Sh("/usr/bin/mdutil", "-a -s");
                var on = RestoreRules.SpotlightOn(status);
                var cmd = on.Count == 0 ? "mdutil -a -i off" : string.Join(" ; ", on.Select(v => "mdutil -i off " + RestoreRules.ShellQuote(v)));
                var r = await Admin(cmd, "SystemBoost가 Spotlight 색인을 멈추려고 해요. 되돌릴 때까지 멈춰 있어요.");
                if (r != null) return r;
                _backup[id] = on.Count == 0 ? AllVolumes : string.Join("\n", on); SaveBackup();
                return "Spotlight 색인을 멈췄어요. 되돌릴 때까지 멈춰 있고, 그동안 새 파일은 검색에 나오지 않아요";
            }
            default: return "macOS에서는 지원하지 않는 항목이에요";
        }
    }

    private const string AllVolumes = "*";

    /// The command that puts one recorded item back. Records from before kept "1" for each.
    private static string? RestoreCommand(string id, string record) => id switch
    {
        "mac.lowpower" => RestoreRules.LowPowerRestoreCommand(record),
        "mac.tm" => "tmutil enable",
        "mac.spotlight" => record is "1" or AllVolumes
            ? "mdutil -a -i on"
            : string.Join(" ; ", record.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(v => "mdutil -i on " + RestoreRules.ShellQuote(v))),
        _ => null,
    };

    private const string RevertPrompt = "SystemBoost가 바꾼 설정을 원래대로 돌리려고 해요.";

    public async Task<string> RevertAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_backup.TryGetValue(id, out var record)) return "변경한 적이 없어요";
            var cmd = RestoreCommand(id, record);
            if (cmd != null)
            {
                var r = await Admin(cmd, RevertPrompt);
                if (r != null) return r;
            }
            _backup.Remove(id); SaveBackup();
            if (id == "mac.tm") Warning = null;
            return "원래대로 돌렸어요";
        }
        finally { _gate.Release(); }
    }

    public async Task<string> RevertAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_backup.Count == 0) return "변경된 것이 없어요";
            // one admin prompt for everything
            var cmds = _backup.Select(kv => RestoreCommand(kv.Key, kv.Value)).Where(c => c != null).ToList();
            if (cmds.Count > 0)
            {
                var r = await Admin(string.Join(" ; ", cmds), RevertPrompt);
                if (r != null) return r;
            }
            int n = _backup.Count;
            _backup.Clear(); SaveBackup();
            Warning = null;
            return $"{n}개 항목을 원래대로 돌렸어요";
        }
        finally { _gate.Release(); }
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
    /// Runs a command as admin through the standard macOS password dialog, which shows why (prompt). null = ok, else message.
    private static async Task<string?> Admin(string cmd, string prompt)
    {
        static string Quoted(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var script = $"do shell script {Quoted(cmd)} with prompt {Quoted(Lang.T(prompt))} with administrator privileges";
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
