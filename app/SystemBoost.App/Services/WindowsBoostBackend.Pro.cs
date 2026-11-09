// Pro: the work and streaming features (Windows).
//   pro.stream   while a streaming app runs: its priority goes from Normal to AboveNormal, Windows notifications
//                are off, SystemBoost's own notices stay out of the broadcast (MainWindow), and big transfers of
//                other programs are reported. All of it goes back when the streaming app closes.
//   pro.work     while a video call app or a work app the user picked runs: its priority Normal -> AboveNormal.
//   startup      programs that start with Windows for this user, turned off and on with the switch Task Manager
//                uses (StartupApproved). The program and its Run entry or shortcut stay where they are.
// Only a program at Normal is raised, and it goes back only if it is still the same program at the value set here:
// a priority the user or the program chose stays. Games and system programs are not opened.
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public sealed partial class WindowsBoostBackend
{
    internal static readonly string[] ProModes = { "pro.stream", "pro.work" };
    internal const string StartupFeature = "pro.startup";
    internal const string StartupTitle = "시작 프로그램 정리";
    internal static readonly HashSet<string> StreamApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "obs32", "obs", "Streamlabs Desktop", "Streamlabs OBS", "PRISMLiveStudio", "XSplit.Core",
    };
    internal static readonly HashSet<string> MeetingApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Zoom", "ms-teams", "Teams", "CiscoCollabHost", "atmgr", "webexmta", "Skype",
    };
    internal const uint NormalPriority = 0x20, AboveNormalPriority = 0x8000;

    // pid -> what was raised; kept in memory only, since a priority ends with its process anyway
    private readonly Dictionary<int, (string name, string mode, uint before)> _raised = new();
    private readonly HashSet<int> _leftAlone = new();   // looked at once and not at Normal
    private bool _streaming;

    /// A streaming app runs with the stream mode on. MainWindow keeps notices out of the broadcast then.
    public bool Streaming => _streaming;
    public IReadOnlyList<string> WorkApps => _s.WorkApps.ToList();

    public void SetWorkApps(IEnumerable<string> names)
    {
        _s.WorkApps = names.Where(n => !ResourceManagers.IsProtected(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
    }

    private HashSet<string> WorkSet()
    {
        var set = new HashSet<string>(MeetingApps, StringComparer.OrdinalIgnoreCase);
        set.UnionWith(_s.WorkApps);
        return set;
    }

    private static PremiumFeature NeedOf(string mode) => mode == "pro.stream" ? PremiumFeature.StreamMode : PremiumFeature.WorkMode;

    private string ApplyProMode(string id)
    {
        if (!License.Allowed(NeedOf(id))) return $"{License.PlanOf(NeedOf(id))} 기능이에요 (바뀐 것 없음)";
        if (!_s.Enabled.Contains(id)) _s.Enabled.Add(id);
        Save();
        return id == "pro.stream" ? "켰어요. 방송 프로그램이 켜지면 적용돼요" : "켰어요. 화상 회의나 고른 업무 프로그램이 켜지면 적용돼요";
    }

    // ------------------------------------------------------------ every 2 seconds
    private async Task ProTickAsync(HashSet<string> names)
    {
        if (_measuring) return;     // nothing may move while frames are compared
        bool stream = _s.Enabled.Contains("pro.stream") && License.Allowed(PremiumFeature.StreamMode) && StreamApps.Overlaps(names);
        if (stream && !_streaming)
        {
            _streaming = true;
            Log("방송 프로그램을 감지했어요: 방송 모드");
            Publish();
        }
        else if (!stream && (_streaming || _raised.Values.Any(r => r.mode == "pro.stream") || _s.Backups.Any(b => b.Feature == "pro.stream")))
        {
            Log(await EndProModeAsync("pro.stream") ?? "방송 모드를 끝냈어요");
            Publish();
        }
        if (stream)
        {
            int n = Raise("pro.stream", StreamApps);
            if (n > 0) Log($"방송 프로그램 {n}개의 우선순위를 '보통 이상'으로 올렸어요");
            if (QuietNotifications("pro.stream")) Log("방송 중에는 Windows 알림을 꺼요");
            await WatchNetworkAsync();
        }

        var work = WorkSet();
        bool working = _s.Enabled.Contains("pro.work") && License.Allowed(PremiumFeature.WorkMode) && work.Overlaps(names);
        if (working)
        {
            int n = Raise("pro.work", work);
            if (n > 0) Log($"업무 프로그램 {n}개의 우선순위를 '보통 이상'으로 올렸어요");
        }
        else if (_raised.Values.Any(r => r.mode == "pro.work"))
        {
            var r = await EndProModeAsync("pro.work");
            if (r != null) Log(r);
        }
        ForgetExited();
    }

    /// Raises the named programs that are at Normal. Returns how many were raised now.
    private int Raise(string mode, HashSet<string> names)
    {
        int raised = 0, session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (!names.Contains(name) || _raised.ContainsKey(p.Id) || _leftAlone.Contains(p.Id)) continue;
                if (p.SessionId != session || ResourceManagers.IsProtected(name) || ResourceManagers.LooksLikeGame(p.Id, name)) { _leftAlone.Add(p.Id); continue; }
                if (RaisePriority(p.Id) is uint before) { _raised[p.Id] = (name, mode, before); raised++; }
                else _leftAlone.Add(p.Id);
            }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return raised;
    }

    private void ForgetExited()
    {
        if (_raised.Count == 0 && _leftAlone.Count == 0) return;
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcesses()) { alive.Add(p.Id); p.Dispose(); }
        foreach (var pid in _raised.Keys.Where(k => !alive.Contains(k)).ToList()) _raised.Remove(pid);
        _leftAlone.RemoveWhere(pid => !alive.Contains(pid));
    }

    /// Puts back what a mode changed. A message when something went back or failed, else null.
    private async Task<string?> EndProModeAsync(string id)
    {
        int back = 0;
        foreach (var (pid, r) in _raised.Where(kv => kv.Value.mode == id).ToList())
        {
            if (PutBackPriority(pid, r.name, r.before)) back++;
            _raised.Remove(pid);
        }
        _leftAlone.Clear();
        var done = new List<string>();
        if (back > 0) done.Add($"우선순위 {back}개를 원래대로 돌렸어요");
        if (id == "pro.stream")
        {
            _streaming = false;
            // a game with "알림 일시정지" still running keeps the notifications off
            if (_gameRunning && _s.Enabled.Contains("sys.focus") && !_stop.IsCancellationRequested) HandOverToast("pro.stream", "sys.focus");
            var mine = _s.Backups.Where(b => b.Feature == id).ToList();
            if (mine.Count > 0)
            {
                var failed = await RestoreAsync(mine);
                done.Add(failed == null ? "Windows 알림을 원래대로 돌렸어요" : "되돌리지 못했어요: " + failed);
            }
        }
        return done.Count > 0 ? string.Join(" · ", done) : null;   // " · " so each part is translated
    }

    private async Task EndAllProModesAsync()
    {
        foreach (var id in ProModes)
        {
            var r = await EndProModeAsync(id);
            if (r != null) Log(r);
        }
    }

    // ------------------------------------------------------------ notifications
    // One backup holds the notification switch's own value, whichever feature turned it off first.
    private static bool IsToast(BoostBackup b) => b.Kind == "hkcu" && b.Key == PushNotifications && b.Name == "ToastEnabled";

    /// Turns Windows notifications off for a feature unless they are off already. True when it did now.
    private bool QuietNotifications(string feature)
    {
        if (_s.Backups.Any(IsToast)) return false;
        ApplyUserValues(feature, (PushNotifications, "ToastEnabled", 0));
        RestartNotifications();
        return true;
    }

    /// The feature that still needs quiet takes the backup over instead of putting the value back.
    private void HandOverToast(string from, string to)
    {
        var mine = _s.Backups.Where(b => b.Feature == from && IsToast(b)).ToList();
        foreach (var b in mine) b.Feature = to;
        if (mine.Count > 0) Save();
    }

    // ------------------------------------------------------------ priority
    /// Normal -> AboveNormal. The priority before, or null when it was not Normal or could not be changed.
    internal static uint? RaisePriority(int pid)
    {
        var h = OpenProcess(ProcessSetInformation | ProcessQueryLimited, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var before = GetPriorityClass(h);
            if (before != NormalPriority) return null;
            return SetPriorityClass(h, AboveNormalPriority) ? before : null;
        }
        finally { CloseHandle(h); }
    }

    /// Back to the value before, only if it is the same program and still at the value set here.
    internal static bool PutBackPriority(int pid, string name, uint before)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch (Exception) { return false; }   // gone: nothing to put back
        var h = OpenProcess(ProcessSetInformation | ProcessQueryLimited, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetPriorityClass(h) == AboveNormalPriority && SetPriorityClass(h, before); }
        finally { CloseHandle(h); }
    }

    // ------------------------------------------------------------ startup programs
    public async Task<string> SetStartupAsync(StartupApps.Item item, bool on)
    {
        if (!License.Allowed(PremiumFeature.StartupApps))
            return $"{StartupTitle}는 {License.PlanOf(PremiumFeature.StartupApps)} 기능이에요. 왼쪽 '★ 프리미엄'에서 열 수 있어요.";
        await _gate.WaitAsync();
        try
        {
            var old = _s.Backups.FirstOrDefault(b => b.Feature == StartupFeature && b.Kind == "startup" && b.Key == item.Approved && b.Name == item.Name);
            var cur = StartupApps.Read(item.Approved, item.Name);
            if (old == null && StartupApps.IsOn(cur) == on) return $"{item.Name}: 이미 {(on ? "켜져" : "꺼져")} 있어요";
            if (old != null && StartupApps.IsOn(old.Existed ? Convert.FromHexString(old.Value) : null) == on)
            {   // back to how it was: the original switch goes back exactly
                RestoreStartup(old);
                _s.Backups.Remove(old);
            }
            else
            {
                if (old == null)
                {
                    _s.Backups.Add(new BoostBackup { Feature = StartupFeature, Kind = "startup", Key = item.Approved, Name = item.Name, Target = item.Command,
                                                     Existed = cur != null, Value = cur == null ? "" : Convert.ToHexString(cur) });
                    Save();
                }
                StartupApps.Write(item.Approved, item.Name, StartupApps.Switch(on, DateTime.UtcNow));
            }
            Save();
            return StartupApps.IsOn(StartupApps.Read(item.Approved, item.Name)) == on
                ? $"{item.Name}: {(on ? "다시 켰어요" : "껐어요")}. 다음 로그인부터 적용돼요"
                : $"{item.Name}: 바꾸지 못했어요";
        }
        catch (Exception e) { return "실패했어요: " + e.Message; }
        finally { _gate.Release(); Publish(); }
    }

    private static void RestoreStartup(BoostBackup b) =>
        StartupApps.Write(b.Key, b.Name, b.Existed ? Convert.FromHexString(b.Value) : null);

    [DllImport("kernel32", SetLastError = true)] private static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetPriorityClass(IntPtr h, uint priority);
}

/// Programs that start with Windows for this user: the Run key and the Startup folder. Turning one off writes the
/// switch Task Manager and Settings write (Explorer\StartupApproved); nothing is deleted.
[SupportedOSPlatform("windows")]
public static class StartupApps
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    /// Where to look. Tests pass their own keys and folder.
    public sealed record Places(string Run, string ApprovedRun, string Folder, string ApprovedFolder);
    public static Places User => new(RunKey, ApprovedRun, Environment.GetFolderPath(Environment.SpecialFolder.Startup), ApprovedFolder);

    /// Name = the Run value's name or the file's name in the folder; Approved = the key holding its switch.
    public sealed record Item(string Name, string Command, string Approved, bool Enabled);

    public static List<Item> List(Places at)
    {
        var items = new List<Item>();
        using (var run = Registry.CurrentUser.OpenSubKey(at.Run))
        {
            if (run != null)
                foreach (var name in run.GetValueNames())
                    if (name.Length > 0 && run.GetValue(name) is string cmd) items.Add(new(name, cmd, at.ApprovedRun, IsOn(Read(at.ApprovedRun, name))));
        }
        if (at.Folder.Length > 0 && Directory.Exists(at.Folder))
            foreach (var f in Directory.GetFiles(at.Folder))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new(name, f, at.ApprovedFolder, IsOn(Read(at.ApprovedFolder, name))));
            }
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// No switch, or an even first byte, is on: Task Manager writes 02 for on and 03 for off.
    public static bool IsOn(byte[]? v) => v is not { Length: > 0 } || (v[0] & 1) == 0;

    public static byte[]? Read(string approved, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(approved);
        return k?.GetValue(name) as byte[];
    }

    /// The 12-byte switch: 02 = on; 03 followed by the time it was turned off = off.
    public static byte[] Switch(bool on, DateTime utc)
    {
        var v = new byte[12];
        v[0] = on ? (byte)2 : (byte)3;
        if (!on) BitConverter.GetBytes(utc.ToFileTimeUtc()).CopyTo(v, 4);
        return v;
    }

    /// Writes a switch, or removes it (null) so the program is on as it was before any switch existed.
    public static void Write(string approved, string name, byte[]? value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(approved);
        if (value == null) k.DeleteValue(name, false);
        else k.SetValue(name, value, RegistryValueKind.Binary);
    }
}
