// The consent to the overclocking notice (OcConsent, OverclockNotice): when it is asked, consents.json, the server hook,
// the English text, and the AMD tuning refused without it over the pretend helper. Each test that writes gets its own
// folder under the temp folder, removed afterwards.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SystemBoost.App.Services;

namespace SystemBoost.Gpu.Tests;

internal static partial class Program
{
    private const string Card = "NVIDIA GeForce RTX 4070";
    private static readonly DateTime At = new(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);

    private static ConsentRecord Rec(string version = OverclockNotice.Version, string gpu = Card, bool withdrawn = false, string kind = OcConsent.Kind) =>
        new() { Kind = kind, Version = version, At = OcConsent.Stamp(At), Gpu = gpu, App = "0.8.16", Withdrawn = withdrawn };

    private static async Task WithStore(Func<ConsentStore, Task> run)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb-consent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { await run(new ConsentStore(Path.Combine(dir, OcConsent.FileName))); }
        finally { try { Directory.Delete(dir, recursive: true); } catch (Exception) { } }
    }

    private static Task WithStore(Action<ConsentStore> run) => WithStore(s => { run(s); return Task.CompletedTask; });

    private static void DefineConsent()
    {
        // ---------------------------------------------------------------- when the notice is asked
        Test("consent: asked when none is on record; not asked again for the same card", () =>
        {
            Eq(ConsentNeed.NoRecord, OcConsent.Needs(new List<ConsentRecord>(), Card, fromPreset: false), "no record");
            Eq(ConsentNeed.None, OcConsent.Needs(new[] { Rec() }, Card, false), "agreed for this card");
            Eq(ConsentNeed.None, OcConsent.Needs(new[] { Rec() }, " nvidia  GeForce rtx 4070", false), "same card: case and spacing do not count");
            Eq(ConsentNeed.NoRecord, OcConsent.Needs(new[] { Rec(kind: "other") }, Card, false), "a record of another kind does not count");
            Eq(null, OcConsent.WhyAgain(ConsentNeed.NoRecord), "the first time: no extra line");
            True(!OcConsent.InForce(new List<ConsentRecord>()), "nothing to withdraw");
            True(OcConsent.InForce(new[] { Rec() }), "withdraw offered");
        });

        Test("consent: asked again for another version of the notice", () =>
        {
            Eq(ConsentNeed.OtherVersion, OcConsent.Needs(new[] { Rec(version: "2026-01-01") }, Card, false), "consent to an older notice");
            Eq(ConsentNeed.OtherVersion, OcConsent.Needs(new[] { Rec() }, Card, false, version: "2027-01-01"), "the notice moved on");
            Eq("안내 문구가 바뀌어서 다시 동의를 받아요.", OcConsent.WhyAgain(ConsentNeed.OtherVersion), "line");
            True(OcConsent.InForce(new[] { Rec(version: "2026-01-01") }), "still withdrawable");
        });

        Test("consent: asked again for another graphics card", () =>
        {
            Eq(ConsentNeed.OtherGpu, OcConsent.Needs(new[] { Rec() }, "AMD Radeon RX 7800 XT", false), "new card");
            Eq(ConsentNeed.OtherGpu, OcConsent.Needs(new[] { Rec() }, "NVIDIA GeForce RTX 4070 SUPER", false), "a close name is another card");
            Eq(ConsentNeed.OtherGpu, OcConsent.Needs(new[] { Rec() }, "", false), "a card with no name");
            Eq("그래픽카드가 바뀌어서 다시 동의를 받아요.", OcConsent.WhyAgain(ConsentNeed.OtherGpu), "line");
        });

        Test("consent: a preset with overclock values always asks", () =>
        {
            Eq(ConsentNeed.Preset, OcConsent.Needs(new[] { Rec() }, Card, fromPreset: true), "even with a valid consent");
            Eq(ConsentNeed.Preset, OcConsent.Needs(new List<ConsentRecord>(), Card, fromPreset: true), "and without one");
            Eq("가져온 프리셋에 오버클럭 값이 있어서 다시 동의를 받아요.", OcConsent.WhyAgain(ConsentNeed.Preset), "line");
        });

        Test("consent: withdrawn asks again; the latest record decides", () =>
        {
            var withdrawn = new[] { Rec(), Rec(withdrawn: true) };
            Eq(ConsentNeed.Withdrawn, OcConsent.Needs(withdrawn, Card, false), "withdrawn");
            True(!OcConsent.InForce(withdrawn), "nothing left to withdraw");
            Eq(ConsentNeed.None, OcConsent.Needs(new[] { Rec(), Rec(withdrawn: true), Rec() }, Card, false), "agreed again");
            Eq(ConsentNeed.OtherGpu, OcConsent.Needs(new[] { Rec(), Rec(gpu: "AMD Radeon RX 7800 XT") }, Card, false), "the latest card counts");
            Eq(ConsentNeed.Withdrawn, OcConsent.Needs(new[] { Rec(), Rec(withdrawn: true), Rec(kind: "other") }, Card, false), "other kinds are passed over");
            Eq("동의를 철회했어요. 다시 쓰려면 아래 안내를 읽고 다시 동의해 주세요.", OcConsent.WhyAgain(ConsentNeed.Withdrawn), "line");
        });

        // ---------------------------------------------------------------- consents.json
        Test("consents.json: agree and withdraw appended, written whole, read back", () => WithStore(store =>
        {
            Eq(0, store.Load().Count, "missing file: no consent");
            True(!File.Exists(store.BadPath), "a missing file is not a broken one");
            int changed = 0;
            void Count() => changed++;
            OcConsent.Changed += Count;
            try
            {
                OcConsent.Agree(store, Card, "0.8.16", At);
                OcConsent.Withdraw(store, "", "0.8.16", At.AddMinutes(5));
            }
            finally { OcConsent.Changed -= Count; }
            Eq(2, changed, "screens told twice");
            var all = store.Load();
            Eq(2, all.Count, "two records");
            Eq(("oc", OverclockNotice.Version, "2026-11-18T03:00:00Z", Card, "0.8.16", false),
               (all[0].Kind, all[0].Version, all[0].At, all[0].Gpu, all[0].App, all[0].Withdrawn), "agree");
            Eq(("oc", OverclockNotice.Version, "2026-11-18T03:05:00Z", Card, "0.8.16", true),
               (all[1].Kind, all[1].Version, all[1].At, all[1].Gpu, all[1].App, all[1].Withdrawn), "withdraw: the card of the consent it ends");
            Eq<DateTime?>(At, all[0].AtUtc, "UTC read back");
            using (var doc = JsonDocument.Parse(File.ReadAllText(store.FilePath)))
            {
                Eq(JsonValueKind.Array, doc.RootElement.ValueKind, "a list");
                Eq("kind,version,at,gpu,app,withdrawn", string.Join(",", doc.RootElement[0].EnumerateObject().Select(p => p.Name)), "the fields, nothing else");
            }
            True(!File.Exists(store.FilePath + ".tmp"), "no temporary file left");
            Eq(ConsentNeed.Withdrawn, OcConsent.Needs(all, Card, false), "withdrawn");
            OcConsent.Agree(store, Card, "0.8.16", At.AddMinutes(10));
            Eq(ConsentNeed.None, OcConsent.Needs(store.Load(), Card, false), "agreed again");
            Eq(3, store.Load().Count, "appended: the history stays");
            var other = new ConsentStore(store.FilePath);   // another run of the app reads the same
            Eq(3, other.Load().Count, "read by a new store");
        }));

        Test("consents.json: a broken file is no consent and is kept as .bad", () => WithStore(store =>
        {
            foreach (var text in new[] { "{not json", "{\"kind\":\"oc\"}", "", "null", "[{\"kind\":\"oc\",\"withdrawn\":\"maybe\"}]" })
            {
                File.WriteAllText(store.FilePath, text);
                Eq(0, store.Load().Count, $"[{text}]: no consent");
                Eq(ConsentNeed.NoRecord, OcConsent.Needs(store.Load(), Card, false), $"[{text}]: asked");
                True(File.Exists(store.BadPath) && File.ReadAllText(store.BadPath) == text, $"[{text}]: kept as .bad");
                True(!File.Exists(store.FilePath), $"[{text}]: moved aside");
                OcConsent.Agree(store, Card, "0.8.16", At);
                Eq(1, store.Load().Count, $"[{text}]: a new file after it");
                File.Delete(store.FilePath);
                File.Delete(store.BadPath);
            }
            File.WriteAllText(store.FilePath, "[null, {\"kind\":\"oc\",\"version\":\"" + OverclockNotice.Version + "\",\"gpu\":\"" + Card + "\"}]");
            Eq(ConsentNeed.None, OcConsent.Needs(store.Load(), Card, false), "an empty entry is passed over, missing fields are empty");
            True(!File.Exists(store.BadPath), "readable: not moved");
        }));

        // ---------------------------------------------------------------- the server hook
        Test("server hook: told after agree and withdraw, never waited for, its errors ignored", async () =>
        {
            var calls = new List<(string kind, string version, string gpu, string app, bool withdraw)>();
            var both = new TaskCompletionSource();
            try
            {
                await WithStore(async store =>
                {
                    OcConsent.Agree(store, Card, "0.8.16", At);   // no hook: nothing leaves the PC
                    OcConsent.ServerRecord = async (k, v, g, a, w) =>
                    {
                        await Task.Yield();
                        lock (calls) { calls.Add((k, v, g, a, w)); if (calls.Count == 2) both.TrySetResult(); }
                    };
                    OcConsent.Agree(store, Card, "0.8.16", At);
                    OcConsent.Withdraw(store, Card, "0.8.16", At);
                    await Task.WhenAny(both.Task, Task.Delay(5000));
                    lock (calls)
                    {
                        Eq(2, calls.Count, "two calls");
                        Has(calls.Select(c => c.ToString()), ("oc", OverclockNotice.Version, Card, "0.8.16", false).ToString(), "agree");
                        Has(calls.Select(c => c.ToString()), ("oc", OverclockNotice.Version, Card, "0.8.16", true).ToString(), "withdraw");
                    }
                    OcConsent.ServerRecord = (_, _, _, _, _) => throw new InvalidOperationException("server down");
                    OcConsent.Agree(store, Card, "0.8.16", At);
                    OcConsent.ServerRecord = async (_, _, _, _, _) => { await Task.Yield(); throw new InvalidOperationException("server down"); };
                    OcConsent.Withdraw(store, Card, "0.8.16", At);
                    OcConsent.ServerRecord = (_, _, _, _, _) => new TaskCompletionSource().Task;   // never answers
                    OcConsent.Agree(store, Card, "0.8.16", At);
                    await Task.Delay(100);
                    Eq(6, store.Load().Count, "every record on disk whatever the server did");
                    Eq(ConsentNeed.None, OcConsent.Needs(store.Load(), Card, false), "the last one counts");
                });
            }
            finally { OcConsent.ServerRecord = null; }
        });

        // ---------------------------------------------------------------- the texts
        Test("English: lang.en.json gives the owner's English line for every line of the notice", () =>
        {
            var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lang.en.json")))!;
            Eq(7, OverclockNotice.English.Length, "headline, five lines, checkbox");
            foreach (var (ko, english) in OverclockNotice.English) Eq<string?>(english, en.GetValueOrDefault(ko), $"[{ko}]");
            var shown = OverclockNotice.Paragraphs.SelectMany(p => p).Prepend(OverclockNotice.Headline).Append(OverclockNotice.Agreement).ToList();
            Eq(7, shown.Count, "seven lines on screen");
            foreach (var line in shown) True(OverclockNotice.English.Any(e => e.Korean == line), $"[{line}] has the owner's English");
            True(en.ContainsKey(OverclockNotice.NotAgreed + U), "the refusal");
            foreach (var need in new[] { ConsentNeed.Withdrawn, ConsentNeed.OtherVersion, ConsentNeed.OtherGpu, ConsentNeed.Preset })
                True(en.ContainsKey(OcConsent.WhyAgain(need)!), $"{need} line");
        });

        // ---------------------------------------------------------------- the gate, over the pretend helper
        Test("gate: AMD tuning refused without a valid consent before any prompt; allowed with one", () => WithStore(async store =>
        {
            var r = new Rig();
            r.Consent = name => OcConsent.Needs(store.Load(), name, fromPreset: false) == ConsentNeed.None;
            r.Choice.GpuMaxMHz = 2530;
            var refused = OverclockNotice.NotAgreed + U;
            Eq(refused, await r.Amd.ApplyTuningAsync(), "nothing on record");
            foreach (var kind in new[] { GpuAutoTuning.Undervolt, GpuAutoTuning.OverclockGpu, GpuAutoTuning.OverclockVram })
                Eq(refused, await r.Amd.ApplyAutoTuningAsync(kind), $"nothing on record: {kind}");
            Eq(0, r.Admin.Prompts, "no administrator prompt");
            Eq(0, r.Journal.Backups.Count + r.Journal.Saves + r.Gpu.Writes.Count, "nothing written");

            OcConsent.Agree(store, Card, "0.8.16", At);
            Eq(refused, await r.Amd.ApplyTuningAsync(), "a consent for another card");
            store.Append(Rec(version: "2026-01-01", gpu: r.Gpu.Name));
            Eq(refused, await r.Amd.ApplyTuningAsync(), "a consent to an older notice");
            Eq(0, r.Admin.Prompts + r.Gpu.Writes.Count, "still nothing");

            OcConsent.Agree(store, r.Gpu.Name, "0.8.16", At);
            Eq("적용했어요. 25초 안에 아래 '유지'를 누르지 않으면 원래대로 돌아가요", await r.Amd.ApplyTuningAsync(), "agreed for this card");
            Eq(1, r.Applies, "one write");
            Eq("공장 초기값으로 돌렸어요", await r.Amd.ResetToFactoryAsync(), "putting the card back needs no consent");

            OcConsent.Withdraw(store, r.Gpu.Name, "0.8.16", At);
            Eq(refused, await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.Undervolt), "withdrawn");
            Eq(refused, await r.Amd.ApplyTuningAsync(), "withdrawn, manual");
            Eq(1, r.Applies, "still one write");
        }));
    }
}
