// The separate consent to Article 12-2 of the licence agreement (AmdTerms): when it is required, its records in
// consents.json beside the overclocking ones, the article read from eula.txt, the English of its texts, and the AMD
// backend sending no amd.* command at all while it is missing (AmdHost.AmdTermsOk false) over the pretend helper.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SystemBoost.App.Services;

namespace SystemBoost.Gpu.Tests;

internal static partial class Program
{
    private const string Agreement = "2026-11-20";   // Eula.Version, which the tests cannot link (Eula.cs needs Avalonia)
    private static readonly DateTime Before = new(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Midnight = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.FromHours(9)).UtcDateTime;

    private static ConsentRecord Amd(string version = Agreement, bool withdrawn = false) =>
        new() { Kind = AmdTerms.Kind, Version = version, At = OcConsent.Stamp(Before), App = "0.8.18", Withdrawn = withdrawn };

    private static string[] AssetLines(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static void DefineAmdTerms()
    {
        // ---------------------------------------------------------------- when the consent is required
        Test("amd terms: required before 2027-01-01 00:00 KST, not from that instant on", () =>
        {
            var none = new List<ConsentRecord>();
            Eq(new DateTime(2026, 12, 31, 15, 0, 0, DateTimeKind.Utc), AmdTerms.InForceUtc, "2026-12-31T15:00:00Z");
            Eq(Midnight, AmdTerms.InForceUtc, "midnight in Korea");
            True(AmdTerms.Required(Before, none, Agreement), "now (2026-11-18)");
            True(AmdTerms.Required(AmdTerms.InForceUtc.AddTicks(-1), none, Agreement), "one tick before");
            True(AmdTerms.Required(new DateTime(2026, 12, 31, 14, 59, 59, DateTimeKind.Utc), none, Agreement), "14:59:59 UTC");
            True(!AmdTerms.Required(AmdTerms.InForceUtc, none, Agreement), "at the instant");
            True(!AmdTerms.Required(new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc), none, Agreement), "after it");
            True(!AmdTerms.Required(new DateTime(2026, 12, 31, 15, 0, 0, DateTimeKind.Unspecified), none, Agreement), "an unspecified time counts as UTC");
            True(AmdTerms.Required(new DateTime(2026, 12, 31, 14, 0, 0, DateTimeKind.Unspecified), none, Agreement), "an hour before, unspecified");
            True(!AmdTerms.InForce(Before) && AmdTerms.InForce(Midnight), "InForce");
        });

        Test("amd terms: a record makes it not required; another version, a withdrawal or another kind does not count", () =>
        {
            Eq(false, AmdTerms.Required(Before, new[] { Amd() }, Agreement), "agreed to this agreement's Article 12-2");
            Eq(true, AmdTerms.Required(Before, new[] { Amd(version: "2026-11-12") }, Agreement), "agreed to another version");
            Eq(true, AmdTerms.Required(Before, new[] { Amd(), Amd(withdrawn: true) }, Agreement), "withdrawn");
            Eq(false, AmdTerms.Required(Before, new[] { Amd(), Amd(withdrawn: true), Amd() }, Agreement), "agreed again");
            Eq(true, AmdTerms.Required(Before, new[] { Rec() }, Agreement), "an overclocking consent is not this one");
            Eq(false, AmdTerms.Required(Before, new[] { Amd(), Rec() }, Agreement), "other kinds are passed over");
            Eq(ConsentNeed.NoRecord, OcConsent.Needs(new[] { Amd() }, Card, fromPreset: false), "and this one is no overclocking consent");
            Eq(ConsentNeed.None, OcConsent.Needs(new[] { Rec(), Amd(withdrawn: true) }, Card, false), "withdrawing it leaves the overclocking consent");
            True(AmdTerms.Withdrawable(Before, new[] { Amd() }, Agreement), "withdraw offered");
            True(!AmdTerms.Withdrawable(Before, new[] { Amd(withdrawn: true) }, Agreement), "nothing to withdraw");
            True(!AmdTerms.Withdrawable(Midnight, new[] { Amd() }, Agreement), "from 2027-01-01 withdrawing changes nothing: not offered");
        });

        // ---------------------------------------------------------------- consents.json
        Test("amd terms: consents.json agree and withdraw, beside the overclocking records, nothing sent anywhere", () => WithStore(store =>
        {
            int changed = 0, server = 0;
            void Count() => changed++;
            AmdTerms.Changed += Count;
            OcConsent.Agree(store, Card, "0.8.18", Before);   // before the hook: nothing to count from it
            OcConsent.ServerRecord = (_, _, _, _, _) => { Interlocked.Increment(ref server); return Task.CompletedTask; };
            try
            {
                AmdTerms.Agree(store, Agreement, "0.8.18", Before);
                var all = store.Load();
                Eq(2, all.Count, "two records");
                Eq((AmdTerms.Kind, Agreement, "2026-11-18T03:00:00Z", "", "0.8.18", false),
                   (all[1].Kind, all[1].Version, all[1].At, all[1].Gpu, all[1].App, all[1].Withdrawn), "agree");
                Eq(false, AmdTerms.Required(Before, all, Agreement), "not required now");
                Eq(ConsentNeed.None, OcConsent.Needs(all, Card, false), "the overclocking consent as it was");
                var bytes = File.ReadAllBytes(store.FilePath);
                True(bytes.Length > 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "UTF-8 without BOM");
                True(!File.Exists(store.FilePath + ".tmp"), "written whole, no temporary file left");

                AmdTerms.Withdraw(store, "2026-12-01", "0.8.18", Before.AddMinutes(5));
                all = store.Load();
                Eq((AmdTerms.Kind, Agreement, "2026-11-18T03:05:00Z", true), (all[2].Kind, all[2].Version, all[2].At, all[2].Withdrawn),
                   "withdraw: the version of the consent it ends");
                Eq(true, AmdTerms.Required(Before, all, Agreement), "required again");
                Eq(ConsentNeed.None, OcConsent.Needs(all, Card, false), "the overclocking consent still as it was");
                Eq(2, changed, "screens told twice");
                Thread.Sleep(100);   // a hook call would run on the thread pool
                Eq(0, Volatile.Read(ref server), "the server hook is for the overclocking consent only");
                Eq(3, new ConsentStore(store.FilePath).Load().Count, "another run reads the same");
            }
            finally
            {
                AmdTerms.Changed -= Count;
                OcConsent.ServerRecord = null;
            }
        }));

        // ---------------------------------------------------------------- the article
        Test("amd terms: Article 12-2 read from eula.txt and eula.en.txt, heading to the next heading", () =>
        {
            foreach (var (file, heading, next) in new[] { ("eula.txt", "## 제12조의2 (AMD 구성 요소)", "## 제13조"), ("eula.en.txt", "## Article 12-2 (AMD components)", "## Article 13") })
            {
                var lines = AssetLines(file);
                var section = AmdTerms.Section(lines);
                Eq(heading, section[0], $"{file}: starts at its heading");
                True(section.Skip(1).All(l => !l.StartsWith("## ")), $"{file}: no other heading");
                True(section[^1].Trim().Length > 0, $"{file}: no empty line at the end");
                var at = Array.IndexOf(lines, heading);
                True(lines[(at + section.Count)..].SkipWhile(l => l.Trim().Length == 0).First().StartsWith(next), $"{file}: ends right before {next}");
                var numbered = section.Skip(1).Where(l => Regex.IsMatch(l, @"^(! )?\d\. ")).Select(l => l.TrimStart('!', ' ')[0] - '0').ToList();
                Eq("1,2,3,4,5,6,7,8,9", string.Join(",", numbered), $"{file}: paragraphs 1 to 9");
            }
            Eq(0, AmdTerms.Section(new[] { "## 제12조 (외부 구성 요소)", "text" }).Count, "no Article 12-2: nothing");
            Eq(2, AmdTerms.Section(new[] { "## 제12조의2 (AMD 구성 요소)", "1. a", "", "" }).Count, "the last heading: to the end, empty lines dropped");
        });

        // ---------------------------------------------------------------- the texts
        Test("amd terms: every new text has its English, the same in docs/amd-consent and lang.en.json", () =>
        {
            Dictionary<string, string> Load(string file) =>
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)))!;
            var en = Load("lang.en.json");
            var mine = Load("lang-amd-consent.json");
            True(mine.Count >= 20, "the entries");
            foreach (var (ko, english) in mine)
            {
                Eq<string?>(english, en.GetValueOrDefault(ko), $"[{ko}] in lang.en.json");
                True(english.Length > 0 && !english.Any(c => c is >= '가' and <= '힣'), $"[{ko}] is English");
            }
            foreach (var text in new[] { AmdProtocol.TermsText, AmdGpuBackend.TuningWaitsText, AmdGpuBackend.ProfileWaitsText,
                                         "AMD 구성 요소 조건에 동의하지 않아서 GPU 설정을 되돌리지 못했어요. 기록은 남겨 둬요" })
                True(mine.ContainsKey(text), $"[{text}]");
            True(en.ContainsKey("{0} (바뀐 것 없음)"), "TermsText + (바뀐 것 없음) through the template");
            True(en.ContainsKey(AmdGpuBackend.Untested), "the untested line stays");
        });

        // ---------------------------------------------------------------- the gate, over the pretend helper
        Test("amd terms gate: without the consent no amd.* command at all, every feature off with the note", async () =>
        {
            var r = new Rig { Terms = false };
            r.Choice.GpuMaxMHz = 2500;
            const string text = AmdProtocol.TermsText + U;
            var info = await r.Amd.ReadInfoAsync();
            Eq(("terms", 0), (info.Status, info.Gpus.Count), "info, not asked");
            True(!await r.Amd.DetectAsync(), "detect false");
            True(r.Amd.WaitsForTerms && r.Amd.Gpu == null && r.Amd.Name == "", "no card from ADLX");
            var caps = r.Amd.GetCapabilities();
            True(!caps.Telemetry && !caps.GameProfile && !caps.AnyTuning && !caps.ResetToFactory, "all off");
            Eq((AmdProtocol.TermsText, AmdProtocol.TermsText), (caps.GameProfileNote, caps.TuningNote), "notes");
            Eq(AmdProtocol.TermsText, AmdProtocol.InfoProblem(r.Amd.Info!), "the card's line");
            Eq(text, await r.Amd.ApplyTuningAsync(), "tuning");
            foreach (var kind in new[] { GpuAutoTuning.Undervolt, GpuAutoTuning.OverclockGpu, GpuAutoTuning.OverclockVram })
                Eq(text, await r.Amd.ApplyAutoTuningAsync(kind), $"auto {kind}");
            Eq(text, await r.Amd.ApplyGameProfileAsync("game.exe"), "profile");
            Eq(text, await r.Amd.ResetToFactoryAsync(), "factory reset");
            Eq(text, await r.Amd.KeepTuningAsync(), "keep");
            True(await r.Amd.ReadTelemetryAsync() == null && await r.Amd.ReadProfileAsync() == null, "no readings");
            await r.Amd.WatchTuningAsync();
            await r.Amd.RecoverTuningAsync();
            await r.Amd.RecoverProfileAsync();
            Eq(AmdPutBack.Nothing, await r.Amd.PutBackAsync(), "nothing to put back");
            await r.Admin.EnsureAsync();   // the elevated helper runs for something else
            Eq(text, await r.Amd.ApplyGameProfileAsync("game.exe"), "profile, elevated helper running");
            Eq(text, await r.Amd.ResetToFactoryAsync(), "reset, elevated helper running");
            await r.Amd.WatchTuningAsync();
            Eq(0, r.Plain.Sent.Count + r.Admin.Sent.Count, "no amd.* command on either helper");
            Eq(0, r.Plain.Starts, "the plain helper not even started");
            Eq(1, r.Admin.Prompts, "only the prompt of the test itself");
            Eq(0, r.Gpu.Writes.Count + r.Journal.Backups.Count + r.Journal.Saves + r.Notices.Count, "nothing written, nobody told");
            True(r.Gpu.AtFactory, "the card as it was");
        });

        Test("amd terms gate: the consent given, the card is read with ADLX and everything works as before", async () =>
        {
            // two runs side by side: one with the consent from the start, one that gets it after a look without it
            var given = new Rig();
            var later = new Rig { Terms = false };
            await later.Amd.ReadInfoAsync();
            Eq(AmdProtocol.TermsText, later.Amd.GetCapabilities().TuningNote, "first without it");
            later.Terms = true;
            foreach (var r in new[] { given, later })
            {
                r.Choice.GpuMaxMHz = 2500;
                True(await r.Amd.DetectAsync(), "detected now");
                Eq("AMD Radeon RX 7800 XT", r.Amd.Name, "the card's name from ADLX");
                Eq(null, r.Amd.GetCapabilities().TuningNote, "tuning on");
                Eq("적용했어요. 25초 안에 아래 '유지'를 누르지 않으면 원래대로 돌아가요", await r.Amd.ApplyTuningAsync(), "applied");
                Eq("유지할게요. 앱을 끄면 원래대로 돌아가요", await r.Amd.KeepTuningAsync(), "kept");
                Eq("Anti-Lag를 켰어요 · Radeon Chill을 껐어요 · Radeon Boost 꺼짐 (바꾸지 않아요)", await r.Amd.ApplyGameProfileAsync("game.exe"), "profile");
            }
            Eq(string.Join(" | ", given.Plain.Sent), string.Join(" | ", later.Plain.Sent), "the same reads");
            Eq(string.Join(" | ", given.Admin.Sent), string.Join(" | ", later.Admin.Sent), "the same writes");
            Eq(string.Join(" | ", given.Journal.Backups.Select(b => b.Kind + "=" + b.Value)),
               string.Join(" | ", later.Journal.Backups.Select(b => b.Kind + "=" + b.Value)), "the same records");

            // withdrawn later: an earlier read is not shown any more and nothing more is sent
            later.Terms = false;
            int sent = later.Plain.Sent.Count + later.Admin.Sent.Count;
            True(later.Amd.Gpu == null && later.Amd.Name == "", "the card read before is not used");
            Eq(AmdProtocol.TermsText, later.Amd.GetCapabilities().GameProfileNote, "off with the note");
            True(await later.Amd.ReadTelemetryAsync() == null, "no readings");
            Eq(sent, later.Plain.Sent.Count + later.Admin.Sent.Count, "nothing more sent");
        });

        Test("amd terms gate: records of an earlier run kept without the consent, the user told; put back once it is given", async () =>
        {
            var r = new Rig { Terms = false };
            r.Gpu.GpuMax = 2500;   // what an earlier run set and could not put back
            r.Gpu.AntiLag = '1';
            r.Gpu.Chill = '0';
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.oc", Kind = "amdoc", Target = "0", Name = r.Gpu.Name, Value = "0;2430;2425;0;1" });
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.driver", Kind = "amd3d", Key = "0", Value = "0;1;1;0" });
            await r.Amd.RecoverTuningAsync();
            await r.Amd.RecoverProfileAsync();
            Eq(2, r.Journal.Backups.Count, "both kept");
            Eq(AmdGpuBackend.TuningWaitsText, r.Notices[0], "told: tuning");
            Eq(AmdGpuBackend.ProfileWaitsText, r.Notices[1], "told: profile");
            Has(r.Logs, "AMD 구성 요소 조건에 동의하지 않아서 GPU 설정을 되돌리지 못했어요. 기록은 남겨 둬요", "log");
            await r.Amd.WatchTuningAsync();
            await r.Admin.EnsureAsync();
            Eq(AmdProtocol.TermsText, await r.Amd.RevertTuningAsync(r.Journal.Backups[0]), "전체 되돌리기: tuning stays listed");
            Eq(AmdProtocol.TermsText, await r.Amd.RevertGameProfileAsync(r.Journal.Backups[1]), "전체 되돌리기: profile stays listed");
            Eq(AmdPutBack.WaitsForTerms, await r.Amd.PutBackAsync(), "put back waits");
            Eq(0, r.Plain.Sent.Count + r.Admin.Sent.Count, "nothing sent");
            Eq((2500, '1', '0'), (r.Gpu.GpuMax, r.Gpu.AntiLag, r.Gpu.Chill), "the card untouched");

            r.Terms = true;   // agreed: the same recovery as at the start (WindowsBoostBackend.AmdTermsAgreedAsync)
            r.Notices.Clear();
            await r.Amd.RecoverTuningAsync();
            await r.Amd.RecoverProfileAsync();
            Eq(0, r.Journal.Backups.Count, "both put back");
            Eq((2430, '0', '1'), (r.Gpu.GpuMax, r.Gpu.AntiLag, r.Gpu.Chill), "the card as before the earlier run");
            Eq("SystemBoost가 갑자기 꺼져서 AMD 그래픽카드 설정(전력·클럭)을 원래대로 돌렸어요. 다시 쓰려면 GPU 화면에서 직접 다시 적용해 주세요", r.Notices[0], "told");
            Eq(0, r.Gpu.Writes.Count(w => w.StartsWith("amd.tuning.apply") || w.StartsWith("amd.auto.apply") || w.StartsWith("amd.profile.apply")), "never applied again");
        });

        Test("amd terms gate: a record whose driver is gone is dropped without the consent, as before, nothing sent", async () =>
        {
            var r = new Rig { Terms = false, Dll = false };
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.oc", Kind = "amdoc", Target = "0", Value = "0;2430;2425" });
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.driver", Kind = "amd3d", Key = "0", Value = "0;1;1;0" });
            await r.Amd.RecoverTuningAsync();
            await r.Amd.RecoverProfileAsync();
            Eq(0, r.Journal.Backups.Count, "dropped");
            Eq(0, r.Plain.Starts + r.Admin.Prompts + r.Plain.Sent.Count + r.Admin.Sent.Count, "no helper");
            var rec = new Rig { Terms = false };
            rec.Journal.Backups.Add(new BoostBackup { Feature = "gpu.driver", Kind = "amd3d", Key = "0", Value = "broken" });
            await rec.Amd.RecoverProfileAsync();
            Eq(0, rec.Journal.Backups.Count + rec.Plain.Sent.Count, "an unreadable record dropped, nothing sent");
        });

        // ---------------------------------------------------------------- choosing the card without ADLX
        Test("vendor choice without ADLX: the adapter list finds the AMD card while the consent is missing", () =>
        {
            var none = Array.Empty<AmdGpuInfo>();
            Eq(new GpuPick(GpuVendor.Nvidia, -1), GpuSelection.Choose(true, true, none, false, amdWaiting: AmdSeen.Discrete), "NVML reads a card: NVIDIA");
            Eq(new GpuPick(GpuVendor.Amd, -1), GpuSelection.Choose(false, false, none, true, amdWaiting: AmdSeen.Discrete), "discrete Radeon beside Intel graphics");
            Eq(new GpuPick(GpuVendor.Amd, -1), GpuSelection.Choose(false, true, none, false, amdWaiting: AmdSeen.Discrete), "discrete Radeon before an NVIDIA driver");
            Eq(new GpuPick(GpuVendor.Nvidia, -1), GpuSelection.Choose(false, true, none, false, amdWaiting: AmdSeen.Integrated), "NVIDIA driver beside an AMD APU");
            Eq(new GpuPick(GpuVendor.Amd, -1), GpuSelection.Choose(false, false, none, false, amdWaiting: AmdSeen.Integrated), "AMD APU only");
            Eq(new GpuPick(GpuVendor.Intel, -1), GpuSelection.Choose(false, false, none, true, amdWaiting: AmdSeen.None), "no AMD adapter: Intel");
            Eq(new GpuPick(GpuVendor.None, -1), GpuSelection.Choose(false, false, none, false), "as before without it");

            Eq(AmdSeen.Discrete, GpuSelection.SeenInAdapters(new[] { ("Intel(R) UHD Graphics 770", true), ("AMD Radeon RX 7800 XT", false) }), "discrete");
            Eq(AmdSeen.Integrated, GpuSelection.SeenInAdapters(new[] { ("AMD Radeon(TM) Graphics", true) }), "APU");
            Eq(AmdSeen.Discrete, GpuSelection.SeenInAdapters(new[] { ("Radeon RX 580 Series", false) }), "a name without AMD");
            Eq(AmdSeen.None, GpuSelection.SeenInAdapters(new[] { ("Intel(R) Arc(TM) A770 Graphics", false) }), "Intel only (a driver file left behind)");
            Eq(AmdSeen.Integrated, GpuSelection.SeenInAdapters(Array.Empty<(string, bool)>()), "the list not read: the driver file, after an NVIDIA driver");
        });
    }
}
