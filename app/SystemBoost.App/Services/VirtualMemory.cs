// Windows virtual memory (page file) - read the current setup, suggest a size,
// apply it with one UAC prompt, and undo it.
// Setting lives in HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PagingFiles
// (REG_MULTI_SZ):  "?:\pagefile.sys"              = Windows manages everything (default)
//                  "C:\pagefile.sys 0 0"          = system-managed size on C:
//                  "D:\pagefile.sys 16384 32768"  = custom, initial / maximum MB
// Takes effect after a restart. A page file does not make RAM faster or add
// real RAM; it lets games that need more memory than installed keep running
// instead of crashing with "out of memory".
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public sealed record PagefileEntry(string Drive, int InitialMB, int MaximumMB)   // 0/0 = system managed on that drive
{
    public bool SystemManaged => InitialMB == 0 && MaximumMB == 0;
    public override string ToString() => SystemManaged ? $"{Drive} 시스템 관리 크기" : $"{Drive} {InitialMB / 1024.0:0.#}~{MaximumMB / 1024.0:0.#} GB";
}

public sealed record DriveChoice(string Drive, long FreeMB, long TotalMB)
{
    public override string ToString() => $"{Drive}  (남은 공간 {FreeMB / 1024.0:0} GB / {TotalMB / 1024.0:0} GB)";
}

public static class VirtualMemory
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private static string BackupFile => Path.Combine(Platform.DataDir, "pagefile-backup.json");

    // ------------------------------------------------------------ read
    public static long InstalledRamMB()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? (long)(m.ullTotalPhys / 1048576) : 0;
    }

    /// null = Windows manages all drives automatically.
    public static List<PagefileEntry>? Current()
    {
        if (!OperatingSystem.IsWindows()) return new();
        using var k = Registry.LocalMachine.OpenSubKey(KeyPath);
        var v = k?.GetValue("PagingFiles") as string[] ?? Array.Empty<string>();
        if (v.Any(s => s.TrimStart().StartsWith("?:"))) return null;
        var list = new List<PagefileEntry>();
        foreach (var s in v)
        {
            var p = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            string drive = p[0].Length >= 2 ? p[0][..2].ToUpperInvariant() : p[0];
            int a = p.Length > 1 && int.TryParse(p[1], out var x) ? x : 0;
            int b = p.Length > 2 && int.TryParse(p[2], out var y) ? y : 0;
            list.Add(new PagefileEntry(drive, a, b));
        }
        return list;   // empty list = no page file at all
    }

    public static string Describe(List<PagefileEntry>? cur) =>
        cur == null ? "Windows 자동 관리" : cur.Count == 0 ? "꺼져 있음 (권장하지 않아요)" : string.Join(", ", cur);

    /// Size of the page file Windows actually created (MB), or 0.
    public static long ActualSizeMB(string drive)
    {
        try { var f = new FileInfo(drive + @"\pagefile.sys"); return f.Exists ? f.Length / 1048576 : 0; }
        catch (Exception) { return 0; }
    }

    public static List<DriveChoice> Drives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                 .Select(d => new DriveChoice(d.Name.TrimEnd('\\'), d.AvailableFreeSpace / 1048576, d.TotalSize / 1048576))
                 .ToList();

    // ------------------------------------------------------------ suggest
    /// Gaming suggestion: initial = RAM (8..32 GB), maximum = 2x initial, never
    /// leaving less than 15 GB or 10 % of the drive free.
    public static (int initialMB, int maxMB, string? warning) Recommend(long ramMB, DriveChoice drive)
    {
        long initial = Math.Clamp(ramMB, 8192, 32768);
        long max = initial * 2;
        long keepFree = Math.Max(15 * 1024, drive.TotalMB / 10);
        long budget = drive.FreeMB + ActualSizeMB(drive.Drive) - keepFree;
        string? warn = null;
        if (budget < max)
        {
            max = Math.Max(0, budget);
            initial = Math.Min(initial, max);
            warn = max < 4096 ? "이 드라이브는 남은 공간이 부족해요. 다른 드라이브를 고르거나 공간을 비우세요." : "남은 공간에 맞춰 크기를 줄였어요.";
        }
        return ((int)(initial / 1024 * 1024), (int)(max / 1024 * 1024), warn);
    }

    // ------------------------------------------------------------ apply / undo
    /// entries == null -> Windows automatic. Saves the current setting first. One UAC prompt.
    public static async Task<string> ApplyAsync(List<PagefileEntry>? entries)
    {
        if (!OperatingSystem.IsWindows()) return "Windows에서만 바꿀 수 있어요";
        if (!File.Exists(BackupFile))   // keep the very first original
        {
            using var k = Registry.LocalMachine.OpenSubKey(KeyPath);
            File.WriteAllText(BackupFile, JsonSerializer.Serialize(k?.GetValue("PagingFiles") as string[] ?? Array.Empty<string>()));
        }
        string[] value = entries == null
            ? new[] { @"?:\pagefile.sys" }
            : entries.Select(e => $@"{e.Drive}\pagefile.sys {e.InitialMB} {e.MaximumMB}").ToArray();
        var err = await WriteElevatedAsync(value, entries == null);
        return err ?? "저장했어요. 다시 시작하면 적용돼요.";
    }

    public static bool HasBackup => File.Exists(BackupFile);

    public static async Task<string> RevertAsync()
    {
        if (!File.Exists(BackupFile)) return "되돌릴 기록이 없어요";
        var orig = JsonSerializer.Deserialize<string[]>(File.ReadAllText(BackupFile)) ?? Array.Empty<string>();
        var err = await WriteElevatedAsync(orig, orig.Any(s => s.TrimStart().StartsWith("?:")));
        if (err != null) return err;
        File.Delete(BackupFile);
        return "원래 설정으로 돌렸어요. 다시 시작하면 적용돼요.";
    }

    /// Writes PagingFiles (and the automatic flag) through an elevated PowerShell
    /// (one UAC prompt). Values travel as Base64 JSON (only A-Z a-z 0-9 + / =),
    /// so nothing in them can be read as script.
    private static async Task<string?> WriteElevatedAsync(string[] value, bool automatic)
    {
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
        var script =
            "$ErrorActionPreference = 'Stop'; " +
            $"$v = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{payload}')) | ConvertFrom-Json; " +
            "$cs = Get-CimInstance Win32_ComputerSystem; " +
            $"Set-CimInstance -InputObject $cs -Property @{{AutomaticManagedPagefile = ${(automatic ? "true" : "false")}}}; " +
            $"Set-ItemProperty -Path 'HKLM:\\{KeyPath}' -Name PagingFiles -Type MultiString -Value ([string[]]$v)";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return "실행하지 못했어요";
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? null : $"저장하지 못했어요 (코드 {p.ExitCode})";
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return "취소했어요 (바뀐 것 없음)"; }   // UAC declined
        catch (System.ComponentModel.Win32Exception) { return "실행하지 못했어요 (바뀐 것 없음)"; }
    }

    public static void Restart()
    {
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"SystemBoost: 가상 메모리 설정 적용\"") { UseShellExecute = false, CreateNoWindow = true });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}
