// PC 맞춤 추천: the byte layouts PcSpecReader reads from Windows, parsed without Windows calls so the tests can feed
// them made-up tables. Owner: the advisor part (CONTRACT.md).
//   Cores  SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX records of RelationProcessorCore (GetLogicalProcessorInformationEx).
//   Memory the SMBIOS table (GetSystemFirmwareTable 'RSMB'): memory devices (type 17) of the system memory arrays (type 16).
//   GPUs   vendor ids, the integrated-graphics guess when the engine's list is not there, the match with that list.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SystemBoost.App.Services.PcCheck;

internal static class PcSpecParse
{
    // ------------------------------------------------------------ CPU
    public sealed record CpuCores(int Cores, int Threads, int PCores, int ECores);

    private const int RelationProcessorCore = 0;

    /// One record per physical core: Relationship (4 bytes), Size (4), Flags (1), EfficiencyClass (1), reserved (20),
    /// GroupCount (2), then GroupCount GROUP_AFFINITY of 16 bytes (an 8-byte mask, the group, reserved).
    /// A hybrid CPU has cores of more than one efficiency class: P-cores are the highest class, E-cores all the others
    /// (so the low-power E-cores of some laptop CPUs count as E-cores too). Not hybrid: PCores = Cores, ECores 0.
    public static CpuCores ParseCores(byte[] buffer, int length)
    {
        length = Math.Min(length, buffer.Length);
        var classes = new List<byte>();
        int threads = 0;
        for (int off = 0; off + 8 <= length;)
        {
            int relation = BitConverter.ToInt32(buffer, off), size = BitConverter.ToInt32(buffer, off + 4);
            if (size < 8 || off + size > length) break;
            if (relation == RelationProcessorCore && size >= 32)
            {
                classes.Add(buffer[off + 9]);
                int groups = BitConverter.ToUInt16(buffer, off + 30);
                for (int g = 0; g < groups && off + 32 + g * 16 + 8 <= off + size; g++)
                    threads += System.Numerics.BitOperations.PopCount(BitConverter.ToUInt64(buffer, off + 32 + g * 16));
            }
            off += size;
        }
        if (classes.Count == 0) return new CpuCores(0, 0, 0, 0);
        byte top = classes.Max();
        int p = classes.Count(c => c == top);
        bool hybrid = classes.Min() != top;
        return new CpuCores(classes.Count, threads, hybrid ? p : classes.Count, hybrid ? classes.Count - p : 0);
    }

    // ------------------------------------------------------------ memory
    /// Speeds in MT/s (0 unknown): Configured is the slowest stick's running speed, Rated the slowest stick's own.
    /// Sticks: memory modules in slots; 0 when unknown or when the memory is soldered (a laptop's LPDDR often shows as
    /// one device although it runs on several channels, so a one-stick tip there would be wrong). TotalMB: the sum.
    public sealed record Memory(int ConfiguredMTs, int RatedMTs, int Sticks, long TotalMB);

    // SMBIOS 7.18.1 form factors that sit in a slot: SIMM, DIMM, RIMM, SODIMM, SRIMM, FB-DIMM, CAMM, CUDIMM, CSODIMM
    private static readonly HashSet<byte> Socketed = new() { 0x03, 0x09, 0x0C, 0x0D, 0x0E, 0x0F, 0x11, 0x12, 0x13 };

    /// raw: RawSMBIOSData (Used20CallingMethod, major, minor, DMI revision, a 4-byte table length, the table).
    public static Memory ParseSmbiosMemory(byte[] raw)
    {
        if (raw.Length < 8) return new Memory(0, 0, 0, 0);
        long declared = BitConverter.ToUInt32(raw, 4);
        int end = (int)Math.Min(raw.Length, 8 + declared);
        var systemArrays = new HashSet<int>();
        bool anyArray = false;
        var devices = new List<(int array, long mb, byte form, int rated, int configured)>();
        for (int p = 8; p + 4 <= end;)
        {
            byte type = raw[p], len = raw[p + 1];
            if (len < 4 || p + len > end) break;
            if (type == 16 && len >= 0x06)
            {
                anyArray = true;
                if (raw[p + 0x05] == 0x03) systemArrays.Add(U16(raw, p + 2));   // Use: system memory
            }
            else if (type == 17 && len >= 0x0F)
            {
                long mb = SizeMB(raw, p, len);
                if (mb != 0)
                {
                    int rated = Speed(raw, p, len, 0x15, 0x54), configured = Speed(raw, p, len, 0x20, 0x58);
                    devices.Add((U16(raw, p + 4), mb, raw[p + 0x0E], rated, configured));
                }
            }
            if (type == 127) break;   // end of table
            int q = p + len;   // the strings after the formatted part end with two zero bytes
            while (q + 1 < end && !(raw[q] == 0 && raw[q + 1] == 0)) q++;
            p = q + 2;
        }
        if (anyArray) devices.RemoveAll(d => !systemArrays.Contains(d.array));
        if (devices.Count == 0) return new Memory(0, 0, 0, 0);
        int ratedMin = devices.Where(d => d.rated > 0).Select(d => d.rated).DefaultIfEmpty(0).Min();
        int configuredMin = devices.Where(d => d.configured > 0).Select(d => d.configured).DefaultIfEmpty(0).Min();
        // Some firmware gives the running speed as the memory clock (half the transfer rate): 1600 for DDR4-3200.
        if (ratedMin > 0 && configuredMin > 0 && Math.Abs(configuredMin * 2 - ratedMin) <= ratedMin * 0.03) configuredMin *= 2;
        int sticks = devices.All(d => Socketed.Contains(d.form)) ? devices.Count : 0;
        long total = devices.Where(d => d.mb > 0).Sum(d => d.mb);
        return new Memory(configuredMin, ratedMin, sticks, total);
    }

    /// 0: no module in this slot. -1: a module of unknown size.
    private static long SizeMB(byte[] t, int p, int len)
    {
        int size = U16(t, p + 0x0C);
        if (size == 0) return 0;
        if (size == 0xFFFF) return -1;
        if (size == 0x7FFF) return len >= 0x20 ? BitConverter.ToUInt32(t, p + 0x1C) & 0x7FFFFFFF : -1;
        return (size & 0x8000) != 0 ? Math.Max(1, (size & 0x7FFF) / 1024) : size;   // bit 15: kilobytes
    }

    /// A WORD speed, 0xFFFF meaning "see the DWORD extended field" (SMBIOS 3.3).
    private static int Speed(byte[] t, int p, int len, int at, int extendedAt)
    {
        if (len < at + 2) return 0;
        int v = U16(t, p + at);
        if (v == 0xFFFF) return len >= extendedAt + 4 ? (int)(BitConverter.ToUInt32(t, p + extendedAt) & 0x7FFFFFFF) : 0;
        return v;
    }

    private static int U16(byte[] t, int at) => BitConverter.ToUInt16(t, at);

    // ------------------------------------------------------------ GPUs
    public static string VendorOf(uint pciVendorId) => pciVendorId switch
    {
        0x10DE => "nvidia",
        0x1002 or 0x1022 => "amd",
        0x8086 => "intel",
        _ => "other",
    };

    public static string VendorOfName(string name)
    {
        if (Regex.IsMatch(name, @"NVIDIA|GeForce|Quadro", RegexOptions.IgnoreCase)) return "nvidia";
        if (Regex.IsMatch(name, @"\bAMD\b|Radeon|\bATI\b", RegexOptions.IgnoreCase)) return "amd";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "intel";
        return "other";
    }

    /// When sb_framegen's list (which asks the driver whether the GPU shares the computer's memory) is not there.
    /// NVIDIA cards in PCs are discrete. Intel: integrated unless an Arc A/B card (the Arc graphics inside Core Ultra
    /// CPUs are named without a model letter). AMD: APUs are named "... Graphics" (Radeon Graphics, Radeon 780M Graphics).
    /// Others: under 512 MB of their own memory, like the engine's quick guess.
    public static bool GuessIntegrated(string vendor, string name, long dedicatedMB)
    {
        name = name.Trim();
        switch (vendor)
        {
            case "nvidia": return false;
            case "intel": return !Regex.IsMatch(name, @"Arc(\(TM\))?\s+(Pro\s+)?[AB]\d{2,3}|Xe\s+MAX", RegexOptions.IgnoreCase);
            case "amd":
                if (name.EndsWith("Graphics", StringComparison.OrdinalIgnoreCase) && !name.Contains("Pro ", StringComparison.OrdinalIgnoreCase)) return true;
                return dedicatedMB > 0 && dedicatedMB < 512;
            default: return dedicatedMB < 512;
        }
    }

    /// The engine's adapters (value = its --gen-gpu index, name, integrated as the driver reports it) matched to the
    /// adapters read here, by name in order (two cards of the same name pair up in order). The engine's integrated
    /// flag wins. Nothing read here: the engine's list alone (no memory size).
    public static IReadOnlyList<GpuSpec> MatchEngine(IReadOnlyList<GpuSpec> mine, IReadOnlyList<(string Value, string Name, bool Integrated)> engine)
    {
        if (engine.Count == 0) return mine;
        if (mine.Count == 0)
            return engine.Select(e => new GpuSpec(e.Name, VendorOfName(e.Name), e.Integrated, 0, e.Value)).ToList();
        var used = new bool[engine.Count];
        var list = new List<GpuSpec>();
        foreach (var g in mine)
        {
            int at = -1;
            for (int i = 0; i < engine.Count && at < 0; i++)
                if (!used[i] && string.Equals(engine[i].Name.Trim(), g.Name.Trim(), StringComparison.OrdinalIgnoreCase)) at = i;
            if (at < 0) { list.Add(g); continue; }
            used[at] = true;
            list.Add(g with { Integrated = engine[at].Integrated, EngineValue = engine[at].Value });
        }
        return list;
    }
}
