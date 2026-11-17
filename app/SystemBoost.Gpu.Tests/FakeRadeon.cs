// A pretend Radeon and its driver behind a pretend sb_helper, answering the AMD protocol the way the spec says
// (native/helper; amd_protocol.md): reads on either helper, writes only on the elevated one, journal-before-write checked
// at the moment each write arrives.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SystemBoost.App.Services;

namespace SystemBoost.Gpu.Tests;

internal sealed class FakeRadeon
{
    // what amd.info reports
    public string Status = "ok";
    public string Name = "AMD Radeon RX 7800 XT";
    public bool Integrated;
    public bool PowerOk = true, GpuOk = true, VramOk = true;
    public int PowerMin = -10, PowerMax = 15, GpuMin = 500, GpuMaxLimit = 2900, VramMin = 2400, VramMaxLimit = 2700;
    public const int FactoryPower = 0, FactoryGpu = 2430, FactoryVram = 2425;
    public int Power = FactoryPower, GpuMax = FactoryGpu, VramMax = FactoryVram;
    /// The voltage offset an auto undervolt sets: amd.tuning.revert does not touch it, amd.tuning.reset does.
    public int VoltageMv;
    public bool AutoUndervolt = true, AutoOcGpu = true, AutoOcVram = true;
    public char AntiLag = '0', Chill = '1', Boost = '0', Afmf = '0';
    public int TempC = 62;
    /// Settings the user changed in AMD Software during the game (the helper compares more than on and off).
    public readonly HashSet<string> TouchedByUser = new();

    // the helper's tuning watch
    public bool Active, Confirmed;
    public string Tripped = "0";
    public (int p, int g, int v, int mv) Backup;
    public int LimitC;

    /// The next write answers with this error word.
    public string? FailNext;
    /// Checked when a write arrives: the record must already be on disk.
    public Action<string>? BeforeWrite;
    public readonly List<string> Writes = new();

    public bool AtFactory => Power == FactoryPower && GpuMax == FactoryGpu && VramMax == FactoryVram && VoltageMv == 0;

    public string InfoJson()
    {
        if (Status != "ok") return $"{{\"status\":\"{Status}\",\"adlx\":\"\",\"driver\":\"\",\"gpus\":[]}}";
        string Range(bool ok, int min, int max, int now) => ok ? $"{{\"ok\":true,\"min\":{min},\"max\":{max},\"step\":1,\"now\":{now}}}" : "{\"ok\":false}";
        string Sw(char c) => c == '-' ? "{\"ok\":false}" : $"{{\"ok\":true,\"on\":{(c == '1' ? "true" : "false")}}}";
        string B(bool b) => b ? "true" : "false";
        var sb = new StringBuilder();
        sb.Append("{\"status\":\"ok\",\"adlx\":\"1.4\",\"driver\":\"24.10.1\",\"gpus\":[{");
        sb.Append($"\"i\":0,\"name\":\"{Name}\",\"integrated\":{B(Integrated)},\"factory\":{B(AtFactory)},");
        sb.Append($"\"power\":{Range(PowerOk, PowerMin, PowerMax, Power)},");
        sb.Append($"\"gpuMax\":{Range(GpuOk, GpuMin, GpuMaxLimit, GpuMax)},");
        sb.Append($"\"vramMax\":{Range(VramOk, VramMin, VramMaxLimit, VramMax)},");
        sb.Append($"\"auto\":{{\"undervolt\":{B(AutoUndervolt)},\"ocGpu\":{B(AutoOcGpu)},\"ocVram\":{B(AutoOcVram)}}},");
        sb.Append($"\"antiLag\":{Sw(AntiLag)},\"chill\":{Sw(Chill)},\"boost\":{Sw(Boost)},\"afmf\":{Sw(Afmf)}}}]}}");
        return sb.ToString();
    }

    private static readonly HashSet<string> ReadOnly = new() { "amd.info", "amd.metrics", "amd.profile.get" };

    public (bool ok, string data) Handle(string[] f, bool elevated)
    {
        var cmd = f[0];
        if (Status == "off" && cmd != "amd.info") return (false, "off");   // built without ADLX
        if (!ReadOnly.Contains(cmd))
        {
            if (!elevated) return (false, "refused");
            Writes.Add(string.Join(" ", f));
            BeforeWrite?.Invoke(cmd);
            if (FailNext is { } fail && cmd is not ("amd.tuning.status" or "amd.tuning.keep")) { FailNext = null; return (false, fail); }
        }
        if (Status is "no_dll" or "no_gpu" && cmd != "amd.info") return (false, "no_amd");
        if (Status == "old_driver" && cmd != "amd.info") return (false, "old_driver");
        switch (cmd)
        {
            case "amd.info": return (true, InfoJson());
            case "amd.metrics": return (true, $"{TempC};{TempC + 13};2400;{VramMax};180;97;6120");
            case "amd.profile.get": return (true, $"{AntiLag};{Chill};{Boost};{Afmf}");
            case "amd.profile.apply":
                if ((f[2] != "-" && AntiLag == '-') || (f[3] != "-" && Chill == '-')) return (false, "unsupported");
                if (f[2] != "-") AntiLag = f[2][0];
                if (f[3] != "-") Chill = f[3][0];
                return (true, $"{AntiLag};{Chill}");
            case "amd.profile.revert":
            {
                var changed = new List<string>();
                void One(string name, ref char cur, char old, char set)
                {
                    if (set == '-') return;
                    if (TouchedByUser.Contains(name)) { changed.Add(name); return; }   // the user's value stays
                    if (cur == old) return;
                    if (cur == set) cur = old;
                    else changed.Add(name);
                }
                One("antiLag", ref AntiLag, f[2][0], f[4][0]);
                One("chill", ref Chill, f[3][0], f[5][0]);
                return (true, changed.Count == 0 ? "" : "changed=" + string.Join(",", changed));
            }
            case "amd.tuning.apply":
            {
                if (Active && !Confirmed) return (false, "busy");
                var p = Opt(f[2]); var g = Opt(f[3]); var v = Opt(f[4]);
                if ((p != null && !PowerOk) || (g != null && !GpuOk) || (v != null && !VramOk)) return (false, "unsupported");
                if (Out(p, PowerMin, PowerMax) || Out(g, GpuMin, GpuMaxLimit) || Out(v, VramMin, VramMaxLimit)) return (false, "range");
                if (p > Power + 5 || g > GpuMax + 100 || v > VramMax + 50) return (false, "step");
                Backup = (Power, GpuMax, VramMax, VoltageMv);
                Power = p ?? Power; GpuMax = g ?? GpuMax; VramMax = v ?? VramMax;
                Start(int.Parse(f[5], CultureInfo.InvariantCulture));
                return (true, $"{Backup.p};{Backup.g};{Backup.v}");
            }
            case "amd.auto.apply":
            {
                if (Active && !Confirmed) return (false, "busy");
                bool ok = f[2] switch { "undervolt" => AutoUndervolt, "ocgpu" => AutoOcGpu, "ocvram" => AutoOcVram, _ => false };
                if (!ok) return (false, "unsupported");
                Backup = (Power, GpuMax, VramMax, VoltageMv);
                if (f[2] == "ocgpu") GpuMax += 60;
                if (f[2] == "ocvram") VramMax += 40;
                if (f[2] == "undervolt") VoltageMv -= 50;
                Start(AmdGpuChoice.DefaultTempLimitC);
                return (true, $"{Backup.p};{Backup.g};{Backup.v}");
            }
            case "amd.tuning.keep":
                if (!Active) return (false, "failed");
                Confirmed = true;
                return (true, "");
            case "amd.tuning.status": return (true, $"{(Active ? 1 : 0)};{(Confirmed ? 1 : 0)};{TempC};{Tripped}");
            case "amd.tuning.revert":
            {
                var p = Opt(f[2]); var g = Opt(f[3]); var v = Opt(f[4]);
                if (Out(p, PowerMin, PowerMax) || Out(g, GpuMin, GpuMaxLimit) || Out(v, VramMin, VramMaxLimit)) return (false, "range");
                Power = p ?? Power; GpuMax = g ?? GpuMax; VramMax = v ?? VramMax;
                Active = false;
                return (true, "");
            }
            case "amd.tuning.reset":
                (Power, GpuMax, VramMax, VoltageMv) = (FactoryPower, FactoryGpu, FactoryVram, 0);
                Active = false;
                return (true, "");
            default: return (false, "refused");
        }
    }

    private void Start(int limit) { Active = true; Confirmed = false; Tripped = "0"; LimitC = limit; }

    /// The helper's watch gives up: heat, a failed read and a driver fault go to the factory values, no confirm to the backup.
    public void Trip(string why)
    {
        Active = false;
        Tripped = why;
        if (why == "timeout") (Power, GpuMax, VramMax, VoltageMv) = Backup;
        else (Power, GpuMax, VramMax, VoltageMv) = (FactoryPower, FactoryGpu, FactoryVram, 0);
    }

    /// The helper exits with the app (parent gone): an active tuning goes back to its backup.
    public void HelperExitsCleanly()
    {
        if (Active) (Power, GpuMax, VramMax, VoltageMv) = Backup;
        NewHelperProcess();
    }

    /// A newly started sb_helper knows no tuning of the one before; the card keeps whatever it holds.
    public void NewHelperProcess()
    {
        Active = false;
        Confirmed = false;
        Tripped = "0";
    }

    private static int? Opt(string s) => s == "-" ? null : int.Parse(s, CultureInfo.InvariantCulture);
    private static bool Out(int? v, int min, int max) => v is int x && (x < min || x > max);
}

/// One sb_helper process as the app sees it: plain or elevated, started by EnsureAsync, killed by Kill.
internal sealed class FakeHelper : IHelperChannel
{
    private readonly FakeRadeon _gpu;
    private readonly bool _elevated;
    public FakeHelper(FakeRadeon gpu, bool elevated) { _gpu = gpu; _elevated = elevated; }

    public bool Running { get; private set; }
    /// Set to make EnsureAsync fail with this text (a declined prompt, a missing helper file).
    public string? EnsureError;
    /// A prompt would come up (elevated helper not running yet).
    public int Prompts;
    public int Starts;
    public readonly List<string> Sent = new();

    public Task<string?> EnsureAsync()
    {
        if (Running) return Task.FromResult<string?>(null);
        if (_elevated) Prompts++;
        if (EnsureError != null) return Task.FromResult<string?>(EnsureError);
        Starts++;
        Running = true;
        return Task.FromResult<string?>(null);
    }

    public Task<(bool ok, string data)> CallAsync(params string[] fields)
    {
        if (!Running) return Task.FromResult((false, "not running"));
        Sent.Add(string.Join(" ", fields));
        return Task.FromResult(_gpu.Handle(fields, _elevated));
    }

    /// Task Manager's "end task" on the helper: nothing is put back.
    public void Kill() => Running = false;
}

internal sealed class FakeJournal : IGpuJournal
{
    public List<BoostBackup> Backups { get; } = new();
    public int Saves;
    /// The kinds on disk after each save.
    public List<string> OnDisk = new();
    public void Save()
    {
        Saves++;
        OnDisk = Backups.Select(b => b.Kind).ToList();
    }
}
