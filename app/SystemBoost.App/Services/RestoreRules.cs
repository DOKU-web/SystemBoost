// Rules for putting system settings back. Nothing here touches the registry, a process or the screen,
// so the rules can be tested on their own. One principle runs through them: a value goes back only while
// it still holds what SystemBoost wrote. If the user, Windows or macOS changed it since, it stays as they left it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SystemBoost.App.Services;

public static class RestoreRules
{
    /// A record from before this rule has no SetTo and goes back as it always did.
    public static bool StillOurs(string setTo, string? current) =>
        setTo.Length == 0 || string.Equals(setTo, current, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ per-app graphics preference
    // HKCU\Software\Microsoft\DirectX\UserGpuPreferences\<program path> holds several settings in one string,
    // for example "SwapEffectUpgradeEnable=1;GpuPreference=1;". Only the GpuPreference part is ever changed.
    private const string GpuName = "GpuPreference=";

    private static string[] Parts(string? value) => (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
    private static bool IsGpuPart(string part) => part.TrimStart().StartsWith(GpuName, StringComparison.OrdinalIgnoreCase);

    /// The GpuPreference number in a value ("1" power saving, "2" high performance), null when there is none.
    public static string? GpuToken(string? value) =>
        Parts(value).Where(IsGpuPart).Select(p => p.Trim()[GpuName.Length..]).LastOrDefault();

    /// The value with GpuPreference set to token, or taken out when token is null. Every other setting stays as it was.
    public static string WithGpuToken(string? value, string? token)
    {
        var parts = Parts(value).Where(p => !IsGpuPart(p)).ToList();
        if (token != null) parts.Add(GpuName + token);
        return string.Concat(parts.Select(p => p + ";"));
    }

    /// What to write when SystemBoost takes its GpuPreference back.
    /// write false: leave the value alone (it is not what SystemBoost set any more). value null: delete it (nothing is left in it).
    public static (bool write, string? value) GpuBack(string? current, string? before, string ours)
    {
        if (current == null || GpuToken(current) != ours) return (false, null);
        var v = WithGpuToken(current, before);
        return (true, v.Length == 0 ? null : v);
    }

    // ------------------------------------------------------------ background programs on the E-cores
    /// "since;pid@start;pid@start": when this run began moving programs and which ones it moved
    /// (start is the process's start time in UTC ticks, so a reused process number is not mistaken for it).
    public static string FormatMoved(long since, IEnumerable<KeyValuePair<int, long>> moved) =>
        string.Join(";", new[] { since.ToString(CultureInfo.InvariantCulture) }
            .Concat(moved.OrderBy(m => m.Key).Select(m => m.Key.ToString(CultureInfo.InvariantCulture) + "@" + m.Value.ToString(CultureInfo.InvariantCulture))));

    public static (long since, Dictionary<int, long> moved) ParseMoved(string? value)
    {
        var moved = new Dictionary<int, long>();
        var parts = (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        long since = parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
        foreach (var p in parts.Skip(1))
        {
            var kv = p.Split('@');
            if (kv.Length == 2 && int.TryParse(kv[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                long.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start)) moved[pid] = start;
        }
        return (since, moved);
    }

    /// For a program that sits on exactly the E-cores: whether it gets all cores back. Yes for one this moved, and
    /// for one that started after the moving began (it took the setting over from the program that started it).
    /// One that sat there before was put there by the user or another tool and stays. since 0 is a record kept
    /// before this rule, which gave every such program its cores back.
    public static bool GiveCoresBack(int pid, long start, long since, IReadOnlyDictionary<int, long> moved) =>
        (moved.TryGetValue(pid, out var s) && s == start && start != 0) || since == 0 || (start != 0 && start >= since);

    // ------------------------------------------------------------ Windows Update pause
    public const string PauseTimeFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// The pause end times SystemBoost wrote have all passed, so the pause is over by itself.
    /// False when none of them can be read: then nobody can tell.
    public static bool PauseOver(IEnumerable<string> endsSet, DateTime nowUtc)
    {
        bool any = false;
        foreach (var e in endsSet)
        {
            if (!DateTime.TryParseExact(e, PauseTimeFormat, CultureInfo.InvariantCulture,
                                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return false;
            if (t > nowUtc) return false;
            any = true;
        }
        return any;
    }

    public static bool IsPauseEnd(string name) =>
        name.EndsWith("ExpiryTime", StringComparison.Ordinal) || name.EndsWith("EndTime", StringComparison.Ordinal);

    // ------------------------------------------------------------ macOS
    /// `pmset -g custom`: lowpowermode per power source, keyed "b" (battery), "c" (charger) and "u" (UPS).
    public static Dictionary<string, string> LowPowerBySource(string output)
    {
        var map = new Dictionary<string, string>();
        string? source = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.TrimEnd().EndsWith(':'))
            {
                var head = line.Trim().ToLowerInvariant();
                source = head.StartsWith("battery") ? "b" : head.StartsWith("ac") ? "c" : head.StartsWith("ups") ? "u" : null;
                continue;
            }
            var p = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (source != null && p.Length == 2 && p[0] == "lowpowermode" && p[1] is "0" or "1") map[source] = p[1];
        }
        return map;
    }

    /// What is kept for mac.lowpower: "b=1;c=0". Records from before hold just "1".
    public static string LowPowerRecord(IReadOnlyDictionary<string, string> bySource) =>
        bySource.Count == 0 ? "1" : string.Join(";", bySource.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));

    /// The pmset command that puts the record back, each power source to its own value.
    public static string LowPowerRestoreCommand(string record)
    {
        var parts = record.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('='))
                          .Where(p => p.Length == 2 && p[0] is "b" or "c" or "u" && p[1] is "0" or "1").ToList();
        if (parts.Count == 0) return "pmset -a lowpowermode 1";
        return string.Join(" ; ", parts.Select(p => $"pmset -{p[0]} lowpowermode {p[1]}"));
    }

    /// `mdutil -a -s`: the volumes whose indexing is on. Each volume is a line "/path:" followed by an indented status line.
    public static List<string> SpotlightOn(string output)
    {
        var on = new List<string>();
        string? volume = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (!char.IsWhiteSpace(line[0]) && line.StartsWith('/') && line.TrimEnd().EndsWith(':'))
            {
                volume = line.TrimEnd()[..^1];
                continue;
            }
            if (volume != null && line.Contains("Indexing enabled", StringComparison.OrdinalIgnoreCase) && !on.Contains(volume)) on.Add(volume);
        }
        return on;
    }

    /// One argument for /bin/sh, whatever it contains.
    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ------------------------------------------------------------ page file
    public const int PagefileKeepFreeMB = 10 * 1024;

    /// Why a page file setting must not be applied, or null when it may. entries null is "Windows manages it".
    /// budgetMB: for each drive that can hold one, the free space plus its current page file, minus what stays free.
    public static string? PagefileProblem(IReadOnlyList<(string drive, int initialMB, int maximumMB)>? entries,
                                          IReadOnlyDictionary<string, long> budgetMB)
    {
        if (entries == null) return null;
        if (entries.Count == 0) return "드라이브를 고르세요. 페이지 파일이 하나도 없게 되는 설정은 적용하지 않아요";
        if (entries.Select(e => e.drive.ToUpperInvariant()).Distinct().Count() != entries.Count) return "같은 드라이브가 두 번 있어요";
        foreach (var (drive, initial, maximum) in entries)
        {
            var budget = budgetMB.FirstOrDefault(kv => string.Equals(kv.Key, drive, StringComparison.OrdinalIgnoreCase));
            if (budget.Key == null) return $"{drive} 드라이브를 찾을 수 없어요. 다른 드라이브를 고르세요";
            if (initial == 0 && maximum == 0) continue;   // Windows sizes it on that drive
            if (initial <= 0 || maximum < initial) return "크기가 잘못됐어요";
            if (maximum < 1024) return "크기가 너무 작아요";
            if (maximum > budget.Value) return "이 드라이브의 남은 공간보다 커요 (10 GB는 남겨 둬요). 크기를 줄이세요";
        }
        return null;
    }

    /// A PagingFiles line the helper accepts: "?:\pagefile.sys" (Windows manages everything) or
    /// "C:\pagefile.sys" with an optional "initial maximum" in MB. The same rule is in native/helper/src/validate.h.
    public static bool IsPagefileLine(string s)
    {
        if (s.Length < 15 || !(s[0] == '?' || (s[0] is >= 'A' and <= 'Z') || (s[0] is >= 'a' and <= 'z')) ||
            !s.AsSpan(1).StartsWith(@":\pagefile.sys", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = s[15..];
        if (rest.Length == 0) return true;
        if (s[0] == '?') return false;
        var n = rest.Split(' ');
        if (n.Length != 3 || n[0].Length != 0) return false;
        if (!IsSize(n[1], out var a) || !IsSize(n[2], out var b)) return false;
        return (a == 0 && b == 0) || (a > 0 && a <= b);
    }

    private static bool IsSize(string s, out long v)
    {
        v = 0;
        return s.Length is > 0 and <= 8 && s.All(char.IsAsciiDigit) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v) && v <= 16777216;
    }

    /// The request for the helper. A list with no line is sent as "none", which only putting such a setting back uses.
    /// Null when a line is not one the helper accepts, or "?:\pagefile.sys" is not alone.
    public static string[]? PagefileRequest(IReadOnlyList<string> lines)
    {
        var clean = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (clean.Count == 0) return new[] { "pf.set", "none" };
        if (clean.Count > 26 || !clean.All(IsPagefileLine)) return null;
        if (clean.Count > 1 && clean.Any(l => l[0] == '?')) return null;
        if (clean.Select(l => char.ToUpperInvariant(l[0])).Distinct().Count() != clean.Count) return null;
        return new[] { "pf.set" }.Concat(clean).ToArray();
    }
}
