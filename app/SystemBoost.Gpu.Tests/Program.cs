// The AMD (ADLX) half of the app without a Radeon: AmdGpuBackend and AmdProtocol over a pretend sb_helper (FakeRadeon.cs);
// the consent to the overclocking notice in ConsentTests.cs.
// dotnet run --project app/SystemBoost.Gpu.Tests -c Release   ->  exit code 1 when a check fails.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SystemBoost.App.Services;

namespace SystemBoost.Gpu.Tests;

internal sealed class CheckFailed : Exception
{
    public CheckFailed(string message) : base(message) { }
}

/// One app run: the backend over a plain and an elevated pretend helper, the journal, and what reached the user.
internal sealed class Rig
{
    public readonly FakeRadeon Gpu;
    public readonly FakeJournal Journal;
    public readonly FakeHelper Plain, Admin;
    public readonly List<string> Notices = new(), Logs = new();
    public AmdGpuChoice Choice = new();
    /// Which card names have a consent to the overclocking notice (OcConsent.Valid in the app).
    public Func<string, bool> Consent = _ => true;
    public bool Dll = true, Laptop, Allowed = true;
    public int Changed;
    public DateTime Now = new(2026, 11, 17, 3, 0, 0, DateTimeKind.Utc);
    public readonly AmdGpuBackend Amd;

    public Rig(FakeRadeon? gpu = null, FakeJournal? journal = null)
    {
        Gpu = gpu ?? new FakeRadeon();
        Journal = journal ?? new FakeJournal();
        Plain = new FakeHelper(Gpu, elevated: false);
        Admin = new FakeHelper(Gpu, elevated: true);
        Amd = new AmdGpuBackend(Plain, Admin, Journal, new AmdHost
        {
            DllPresent = () => Dll,
            IsLaptop = () => Laptop,
            TuningAllowed = () => Allowed,
            Choice = () => Choice,
            Consented = name => Consent(name),
            Log = Logs.Add,
            Notice = Notices.Add,
            Changed = () => Changed++,
            UtcNow = () => Now,
        });
    }

    public int Sent(FakeHelper h, string cmd) => h.Sent.Count(s => s == cmd || s.StartsWith(cmd + " ", StringComparison.Ordinal));
    public int Applies => Gpu.Writes.Count(w => w.StartsWith("amd.tuning.apply", StringComparison.Ordinal) || w.StartsWith("amd.auto.apply", StringComparison.Ordinal));
}

internal static partial class Program
{
    private const string U = AmdProtocol.Unchanged;
    private static readonly string[] Reads = { "amd.info", "amd.metrics", "amd.profile.get" };

    private static void Eq<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new CheckFailed($"{what}: expected [{expected}] got [{actual}]");
    }

    private static void True(bool ok, string what)
    {
        if (!ok) throw new CheckFailed(what);
    }

    private static void Has(IEnumerable<string> list, string item, string what)
    {
        if (!list.Contains(item)) throw new CheckFailed($"{what}: [{item}] not in [{string.Join(" | ", list)}]");
    }

    private static readonly List<(string name, Func<Task> run)> Tests = new();
    private static void Test(string name, Func<Task> run) => Tests.Add((name, run));
    private static void Test(string name, Action run) => Tests.Add((name, () => { run(); return Task.CompletedTask; }));

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Define();
        int failed = 0;
        foreach (var (name, run) in Tests)
        {
            try
            {
                await run();
                Console.WriteLine($"ok    {name}");
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"FAIL  {name}: {(e is CheckFailed ? e.Message : e.ToString())}");
            }
        }
        Console.WriteLine($"{Tests.Count - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void Define()
    {
        // ---------------------------------------------------------------- the required cases
        Test("dll missing: no helper starts, nothing changes", async () =>
        {
            var r = new Rig { Dll = false };
            r.Choice.GpuMaxMHz = 2500;
            const string text = "AMD 그래픽 드라이버를 찾지 못했어요. AMD Software(드라이버)를 설치해 주세요";
            Eq("no_dll", (await r.Amd.ReadInfoAsync()).Status, "status");
            True(!await r.Amd.DetectAsync(), "detect false");
            Eq(text + U, await r.Amd.ApplyTuningAsync(), "tuning");
            Eq(text + U, await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.OverclockGpu), "auto");
            Eq(text + U, await r.Amd.ApplyGameProfileAsync("game.exe"), "profile");
            Eq(text + U, await r.Amd.ResetToFactoryAsync(), "reset");
            True(await r.Amd.ReadTelemetryAsync() == null, "no telemetry");
            var caps = r.Amd.GetCapabilities();
            True(!caps.GameProfile && !caps.AnyTuning && !caps.ResetToFactory, "nothing enabled");
            Eq(GpuCapabilities.NotOnThisCard, caps.TuningNote, "tuning note");
            Eq(GpuCapabilities.NotOnThisCard, caps.GameProfileNote, "profile note");
            Eq(0, r.Plain.Starts + r.Admin.Starts + r.Admin.Prompts, "no helper started");
            Eq(0, r.Journal.Backups.Count + r.Journal.Saves, "journal untouched");
        });

        Test("dll missing as the helper sees it (no_dll from amd.info)", async () =>
        {
            var r = new Rig();
            r.Gpu.Status = "no_dll";
            r.Choice.PowerPct = 3;
            Eq("AMD 그래픽 드라이버를 찾지 못했어요. AMD Software(드라이버)를 설치해 주세요" + U, await r.Amd.ApplyTuningAsync(), "text");
            Eq(0, r.Admin.Prompts, "no prompt");
            Eq(0, r.Gpu.Writes.Count, "no writes");
        });

        Test("old driver: told to update, no administrator prompt", async () =>
        {
            var r = new Rig();
            r.Gpu.Status = "old_driver";
            r.Choice.GpuMaxMHz = 2500;
            const string text = "AMD 드라이버가 오래돼서 쓸 수 없어요. AMD Software에서 드라이버를 업데이트해 주세요";
            Eq(text, AmdProtocol.InfoProblem(await r.Amd.ReadInfoAsync()), "info problem");
            Eq(text + U, await r.Amd.ApplyTuningAsync(), "tuning");
            Eq(text + U, await r.Amd.ApplyGameProfileAsync("game.exe"), "profile");
            Eq(0, r.Admin.Prompts, "no prompt");
            Eq(0, r.Journal.Backups.Count, "no record");
            True(r.Plain.Sent.All(s => s == "amd.info"), "only amd.info on the plain helper");
        });

        Test("unsupported: no tuning on this card, shown off with the note", async () =>
        {
            var r = new Rig();
            r.Gpu.PowerOk = r.Gpu.GpuOk = r.Gpu.VramOk = false;
            r.Gpu.AutoUndervolt = r.Gpu.AutoOcGpu = r.Gpu.AutoOcVram = false;
            r.Choice.GpuMaxMHz = 2500;
            await r.Amd.ReadInfoAsync();
            var caps = r.Amd.GetCapabilities();
            True(!caps.AnyTuning && !caps.ResetToFactory, "tuning off");
            Eq(GpuCapabilities.NotOnThisCard, caps.TuningNote, "note");
            True(caps.GameProfile, "game profile still on");
            Eq(GpuCapabilities.NotOnThisCard + U, await r.Amd.ApplyTuningAsync(), "tuning");
            Eq(GpuCapabilities.NotOnThisCard + U, await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.Undervolt), "auto");
            Eq(GpuCapabilities.NotOnThisCard + U, await r.Amd.ResetToFactoryAsync(), "reset");
            Eq(0, r.Admin.Prompts, "no prompt");
            Eq(0, r.Gpu.Writes.Count, "no writes");
        });

        Test("unsupported: one value missing, one auto tuning missing", async () =>
        {
            var r = new Rig();
            r.Gpu.GpuOk = false;
            r.Gpu.AutoUndervolt = false;
            r.Choice.GpuMaxMHz = 2500;   // for the value the card cannot tune: dropped, so nothing is left to send
            await r.Amd.ReadInfoAsync();
            var caps = r.Amd.GetCapabilities();
            True(caps.PowerLimit && !caps.CoreClock && caps.MemClock, "per value");
            True(!caps.AutoUndervolt && caps.AutoOverclockGpu, "per auto tuning");
            Eq(null, caps.TuningNote, "no card-wide note");
            Eq("아래에서 값을 바꾼 뒤 켜 주세요" + U, await r.Amd.ApplyTuningAsync(), "nothing to send");
            Eq(GpuCapabilities.NotOnThisCard + U, await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.Undervolt), "auto");
            Eq(0, r.Admin.Prompts, "no prompt");
        });

        Test("unsupported: no Anti-Lag and no Chill, game profile off with the note", async () =>
        {
            var r = new Rig();
            r.Gpu.AntiLag = r.Gpu.Chill = '-';
            await r.Amd.ReadInfoAsync();
            var caps = r.Amd.GetCapabilities();
            True(!caps.GameProfile, "profile off");
            Eq(GpuCapabilities.NotOnThisCard, caps.GameProfileNote, "note");
            Eq(GpuCapabilities.NotOnThisCard + U, await r.Amd.ApplyGameProfileAsync("game.exe"), "apply");
            Eq(0, r.Admin.Prompts, "no prompt");
            Eq(0, r.Journal.Backups.Count, "no record");
        });

        Test("unsupported from the helper: record taken out", async () =>
        {
            var r = new Rig();
            r.Choice.PowerPct = 3;
            r.Gpu.FailNext = "unsupported";
            Eq("적용하지 못해서 원래 값으로 뒀어요: " + GpuCapabilities.NotOnThisCard, await r.Amd.ApplyTuningAsync(), "text");
            Eq(0, r.Journal.Backups.Count, "record removed");
        });

        Test("out of range and too big a step: refused before any prompt; lowering allowed", async () =>
        {
            var r = new Rig();
            const string range = "그래픽카드가 허용하는 범위를 벗어난 값이에요" + U;
            const string step = "한 번에 올릴 수 있는 값은 전력 +5%, GPU 최대 클럭 +100 MHz, VRAM 최대 클럭 +50 MHz까지예요" + U;
            r.Choice.GpuMaxMHz = 3000;
            Eq(range, await r.Amd.ApplyTuningAsync(), "gpu above max");
            r.Choice.GpuMaxMHz = 2531;
            Eq(step, await r.Amd.ApplyTuningAsync(), "gpu +101");
            r.Choice.GpuMaxMHz = null;
            r.Choice.PowerPct = 6;
            Eq(step, await r.Amd.ApplyTuningAsync(), "power +6");
            r.Choice.PowerPct = -11;
            Eq(range, await r.Amd.ApplyTuningAsync(), "power below min");
            r.Choice.PowerPct = null;
            r.Choice.VramMaxMHz = 2390;
            Eq(range, await r.Amd.ApplyTuningAsync(), "vram below min");
            Eq(0, r.Admin.Prompts, "no prompt for any of them");
            Eq(0, r.Gpu.Writes.Count + r.Journal.Backups.Count, "nothing written");

            r.Choice.PowerPct = -10;
            r.Choice.GpuMaxMHz = 1930;
            r.Choice.VramMaxMHz = 2400;
            Eq("적용했어요. 25초 안에 아래 '유지'를 누르지 않으면 원래대로 돌아가요", await r.Amd.ApplyTuningAsync(), "lowering");
            Eq("amd.tuning.apply 0 -10 1930 2400 83", r.Gpu.Writes.Single(), "command");
            Eq((-10, 1930, 2400), (r.Gpu.Power, r.Gpu.GpuMax, r.Gpu.VramMax), "card");

            Eq("range", AmdProtocol.Check(new AmdRange(true, 500, 2900, 10, 2430), 2435, 100), "off the card's step");
            Eq(null, AmdProtocol.Check(new AmdRange(true, 500, 2900, 10, 2430), 2530, 100), "+100 on the step");
            Eq("unsupported", AmdProtocol.Check(AmdRange.None, 5, 5), "unsupported");
            Eq(null, AmdProtocol.Check(AmdRange.None, null, 5), "left alone");
        });

        Test("apply error: record on disk before the write, taken out after", async () =>
        {
            var r = new Rig();
            r.Choice.GpuMaxMHz = 2500;
            r.Gpu.FailNext = "failed";
            bool onDisk = false;
            r.Gpu.BeforeWrite = cmd => { if (cmd == "amd.tuning.apply") onDisk = r.Journal.OnDisk.Contains(AmdProtocol.TuneKind); };
            Eq("적용하지 못해서 원래 값으로 뒀어요: AMD 드라이버가 값을 받지 않았어요", await r.Amd.ApplyTuningAsync(), "text");
            True(onDisk, "amdoc saved before amd.tuning.apply");
            Eq(0, r.Journal.Backups.Count, "record removed");
            Eq(0, r.Journal.OnDisk.Count, "and saved");
            True(!r.Amd.Tuned && r.Amd.ConfirmBy == null, "nothing waits for 유지");
            Eq(FakeRadeon.FactoryGpu, r.Gpu.GpuMax, "card unchanged");
        });

        Test("temperature trip: record gone, user told, never applied again", async () =>
        {
            var r = new Rig();
            r.Choice.GpuMaxMHz = 2530;
            await r.Amd.ApplyTuningAsync();
            await r.Amd.KeepTuningAsync();
            r.Gpu.TempC = 84;
            r.Gpu.Trip("temp");
            await r.Amd.WatchTuningAsync();
            const string text = "GPU 온도가 제한 온도(83 °C)에 닿아서 AMD 그래픽카드를 공장 초기값으로 돌렸어요";
            Eq(text, r.Notices.Single(), "notice");
            Has(r.Logs, text, "log");
            Eq(0, r.Journal.Backups.Count, "record removed");
            True(r.Gpu.AtFactory, "card at factory values");
            True(r.Changed > 0, "screen told");
            int status = r.Sent(r.Admin, "amd.tuning.status");
            await r.Amd.WatchTuningAsync();
            Eq(status, r.Sent(r.Admin, "amd.tuning.status"), "no more watching");
            Eq(1, r.Applies, "applied once only");
        });

        Test("trip texts: chosen limit, driver fault, failed read, no confirm", async () =>
        {
            var r = new Rig();
            r.Choice.GpuMaxMHz = 2530;
            r.Choice.TempLimitC = 70;
            await r.Amd.ApplyTuningAsync();
            Eq("70", r.Gpu.Writes.Single().Split(' ').Last(), "limit sent");
            r.Gpu.Trip("timeout");
            await r.Amd.WatchTuningAsync();
            Eq("'유지'를 누르지 않아서 AMD 그래픽카드를 이전 값으로 돌렸어요", r.Notices.Single(), "timeout");
            Eq(FakeRadeon.FactoryGpu, r.Gpu.GpuMax, "back to the value before");
            Eq("GPU 온도가 제한 온도(70 °C)에 닿아서 AMD 그래픽카드를 공장 초기값으로 돌렸어요", AmdProtocol.TripText("temp", 70), "temp");
            Eq("그래픽 드라이버 오류가 보여서 AMD 그래픽카드를 공장 초기값으로 돌렸어요", AmdProtocol.TripText("fault", 83), "fault");
            Eq("GPU 온도를 읽지 못해서 AMD 그래픽카드를 공장 초기값으로 돌렸어요", AmdProtocol.TripText("readfail", 83), "readfail");
        });

        Test("forced kill: the next start puts the journal values back, never applies, tells the user", async () =>
        {
            var gpu = new FakeRadeon();
            var journal = new FakeJournal();
            var first = new Rig(gpu, journal);
            first.Choice.PowerPct = 5;
            first.Choice.GpuMaxMHz = 2530;
            await first.Amd.ApplyTuningAsync();
            first.Admin.Kill();   // Task Manager: app and helper gone, nothing put back
            first.Plain.Kill();
            gpu.NewHelperProcess();
            Eq((5, 2530), (gpu.Power, gpu.GpuMax), "card still tuned");

            var next = new Rig(gpu, journal);
            await next.Amd.RecoverTuningAsync();
            Has(next.Admin.Sent, "amd.tuning.revert 0 0 2430 2425", "revert with the journal values");
            Eq(0, next.Sent(next.Admin, "amd.tuning.apply") + next.Sent(next.Admin, "amd.auto.apply"), "never applied again");
            Eq((0, 2430, 2425), (gpu.Power, gpu.GpuMax, gpu.VramMax), "card back");
            Eq(0, journal.Backups.Count, "record removed");
            Eq("SystemBoost가 갑자기 꺼져서 AMD 그래픽카드 설정(전력·클럭)을 원래대로 돌렸어요. 다시 쓰려면 GPU 화면에서 직접 다시 적용해 주세요",
               next.Notices.Single(), "user told");
        });

        Test("forced kill: values of an older record go back as recorded", async () =>
        {
            var r = new Rig();
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.oc", Kind = "amdoc", Target = "0", Name = "AMD Radeon RX 7800 XT", Value = "3;2500;-" });
            r.Gpu.Power = 8;
            r.Gpu.GpuMax = 2600;
            await r.Amd.RecoverTuningAsync();
            Has(r.Admin.Sent, "amd.tuning.revert 0 3 2500 -", "command");
            Eq((3, 2500, FakeRadeon.FactoryVram), (r.Gpu.Power, r.Gpu.GpuMax, r.Gpu.VramMax), "card");
            Eq(0, r.Journal.Backups.Count, "record removed");
        });

        Test("forced kill: card already back, no prompt", async () =>
        {
            var gpu = new FakeRadeon();
            var journal = new FakeJournal();
            var first = new Rig(gpu, journal);
            first.Choice.GpuMaxMHz = 2530;
            await first.Amd.ApplyTuningAsync();
            gpu.HelperExitsCleanly();   // the helper outlived the app and put it back, but the record stayed
            var next = new Rig(gpu, journal);
            await next.Amd.RecoverTuningAsync();
            Eq(0, next.Admin.Prompts, "no prompt");
            Eq(0, journal.Backups.Count, "record removed");
            Eq(1, next.Notices.Count, "user told");
        });

        Test("forced kill: driver removed since, record dropped without a prompt", async () =>
        {
            var r = new Rig { Dll = false };
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.oc", Kind = "amdoc", Target = "0", Value = "0;2430;2425" });
            await r.Amd.RecoverTuningAsync();
            Eq(0, r.Admin.Prompts + r.Plain.Starts, "no helper");
            Eq(0, r.Journal.Backups.Count, "record dropped");
            Eq(0, r.Notices.Count, "no claim that anything was put back");
            Has(r.Logs, "AMD 그래픽카드나 드라이버가 없어져서 GPU 설정 기록만 지웠어요", "log says what happened");
        });

        Test("profile revert reports what the user changed during the game", async () =>
        {
            var r = new Rig();
            await r.Amd.ApplyGameProfileAsync("game.exe");
            r.Gpu.TouchedByUser.Add("chill");
            var rec = r.Journal.Backups.Single();
            Eq(null, await r.Amd.RevertGameProfileAsync(rec), "revert");
            Has(r.Admin.Sent, "amd.profile.revert 0 0 1 1 0", "command");
            const string note = "Radeon Chill은(는) 게임 중에 다른 값으로 바뀌어 있어서 그대로 뒀어요";
            Eq(note, r.Amd.TakeRevertNote(), "note");
            Eq(null, r.Amd.TakeRevertNote(), "note once");
            Has(r.Logs, note, "log");
            Eq('0', r.Gpu.AntiLag, "Anti-Lag back");
            Eq('0', r.Gpu.Chill, "Chill left as the user has it");

            var both = new Rig();
            await both.Amd.ApplyGameProfileAsync("game.exe");
            both.Gpu.TouchedByUser.Add("antiLag");
            both.Gpu.TouchedByUser.Add("chill");
            await both.Amd.RevertGameProfileAsync(both.Journal.Backups.Single());
            Eq("Anti-Lag, Radeon Chill은(는) 게임 중에 다른 값으로 바뀌어 있어서 그대로 뒀어요", both.Amd.TakeRevertNote(), "both");
        });

        // ---------------------------------------------------------------- tuning
        Test("apply: reads on the plain helper, the write on the elevated one, journal value format", async () =>
        {
            var r = new Rig();
            r.Choice.GpuMaxMHz = 2530;
            r.Choice.VramMaxMHz = 2475;
            r.Choice.TempLimitC = 95;
            bool onDisk = false;
            r.Gpu.BeforeWrite = cmd => { if (cmd == "amd.tuning.apply") onDisk = r.Journal.OnDisk.Contains(AmdProtocol.TuneKind); };
            Eq("적용했어요. 25초 안에 아래 '유지'를 누르지 않으면 원래대로 돌아가요", await r.Amd.ApplyTuningAsync(), "text");
            True(onDisk, "journal first");
            Eq("amd.tuning.apply 0 - 2530 2475 90", r.Gpu.Writes.Single(), "command (temperature limit at most 90)");
            var rec = r.Journal.Backups.Single();
            Eq(("gpu.oc", "amdoc", "0", "AMD Radeon RX 7800 XT", "0;2430;2425;0;1"), (rec.Feature, rec.Kind, rec.Target, rec.Name, rec.Value), "record (manual, from factory)");
            Eq(r.Now.AddSeconds(25), r.Amd.ConfirmBy, "25 s for 유지");
            True(r.Plain.Sent.Count > 0 && r.Plain.Sent.All(s => Reads.Contains(s.Split(' ')[0])), "plain helper: reads only");
            Eq(1, r.Admin.Prompts, "one prompt");
            Eq("AMD 자동 튜닝 적용: GPU 언더볼트", "AMD 자동 튜닝 적용: " + AmdGpuBackend.AutoTitle(GpuAutoTuning.Undervolt), "auto title");
        });

        Test("apply: the plain helper refuses writes", () =>
        {
            var gpu = new FakeRadeon();
            Eq((false, "refused"), gpu.Handle(new[] { "amd.tuning.reset", "0" }, elevated: false), "refused");
            Eq(0, gpu.Writes.Count, "nothing written");
        });

        Test("keep: 유지 holds the values, the watch reads the temperature", async () =>
        {
            var r = new Rig();
            r.Choice.GpuMaxMHz = 2500;
            await r.Amd.ApplyTuningAsync();
            Eq("유지할게요. 앱을 끄면 원래대로 돌아가요", await r.Amd.KeepTuningAsync(), "keep");
            True(r.Gpu.Confirmed && r.Amd.ConfirmBy == null, "confirmed");
            r.Gpu.TempC = 71;
            await r.Amd.WatchTuningAsync();
            Eq(71, r.Amd.TempC, "temperature");
            True(r.Amd.Tuned, "still tuned");
            Eq(2500, r.Gpu.GpuMax, "values hold");
            r.Gpu.Trip("timeout");
            Eq("이미 원래대로 돌아갔어요", await r.Amd.KeepTuningAsync(), "too late");
        });

        Test("watch: helper killed, values put back from the journal", async () =>
        {
            var r = new Rig();
            r.Choice.GpuMaxMHz = 2530;
            await r.Amd.ApplyTuningAsync();
            r.Admin.Kill();
            r.Gpu.NewHelperProcess();
            await r.Amd.WatchTuningAsync();
            Has(r.Admin.Sent, "amd.tuning.revert 0 0 2430 2425", "revert");
            Eq(FakeRadeon.FactoryGpu, r.Gpu.GpuMax, "card back");
            Eq(0, r.Journal.Backups.Count, "record removed");
            Has(r.Logs, "GPU 설정을 원래대로 돌렸어요", "log");
            Eq(1, r.Applies, "no re-apply");
        });

        Test("watch: a new helper never had the tuning (inactive, no reason), values put back", async () =>
        {
            var r = new Rig();
            r.Choice.PowerPct = 4;
            await r.Amd.ApplyTuningAsync();
            r.Gpu.NewHelperProcess();
            await r.Amd.WatchTuningAsync();
            Eq(FakeRadeon.FactoryPower, r.Gpu.Power, "card back");
            Eq(0, r.Journal.Backups.Count, "record removed");
            Eq(1, r.Admin.Prompts, "no second prompt");
        });

        Test("watch decisions", () =>
        {
            Eq(AmdWatchAction.PutBack, AmdProtocol.Watch(false, "not running").action, "call failed");
            Eq(AmdWatchAction.PutBack, AmdProtocol.Watch(true, "garbage").action, "bad reply");
            Eq(AmdWatchAction.PutBack, AmdProtocol.Watch(true, "0;0;60;0").action, "inactive, no reason");
            Eq(AmdWatchAction.Tripped, AmdProtocol.Watch(true, "0;1;91;temp").action, "tripped");
            Eq(AmdWatchAction.Running, AmdProtocol.Watch(true, "1;0;60;0").action, "running");
        });

        Test("busy: helper still waits for 유지 of an earlier apply", async () =>
        {
            var r = new Rig();
            r.Gpu.Active = true;
            r.Choice.GpuMaxMHz = 2500;
            Eq("적용하지 못해서 원래 값으로 뒀어요: 앞서 적용한 값이 아직 확인되지 않았어요. '유지'를 누르거나 원래대로 돌아갈 때까지 기다려 주세요",
               await r.Amd.ApplyTuningAsync(), "text");
            Eq(0, r.Journal.Backups.Count, "record removed");
            r.Gpu.Active = false;
            await r.Amd.ApplyTuningAsync();
            Eq("이미 적용 중이에요. 위 스위치를 끈 뒤 다시 해 주세요" + U, await r.Amd.ApplyTuningAsync(), "applied already");
            Eq(2, r.Applies, "the busy one and the one that worked; none for the third");
        });

        Test("auto tuning: own command, same journal and 25 s confirm", async () =>
        {
            var r = new Rig();
            Eq("적용했어요. 25초 안에 아래 '유지'를 누르지 않으면 원래대로 돌아가요", await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.OverclockVram), "text");
            Eq("amd.auto.apply 0 ocvram", r.Gpu.Writes.Single(), "command");
            Eq("0;2430;2425;1;1", r.Journal.Backups.Single().Value, "record (auto, from factory)");
            Has(r.Logs, "AMD 자동 튜닝 적용: VRAM 오버클럭", "log");
            Eq(r.Now.AddSeconds(25), r.Amd.ConfirmBy, "confirm window");
        });

        Test("auto tuning: 83 °C whatever limit was picked for the manual values", async () =>
        {
            var r = new Rig();
            r.Choice.TempLimitC = 70;
            await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.OverclockGpu);
            Eq(83, r.Gpu.LimitC, "helper limit");
            r.Gpu.Trip("temp");
            await r.Amd.WatchTuningAsync();
            Eq("GPU 온도가 제한 온도(83 °C)에 닿아서 AMD 그래픽카드를 공장 초기값으로 돌렸어요", r.Notices.Single(), "trip text");
        });

        Test("auto undervolt from factory values goes back with a reset (voltage too)", async () =>
        {
            var gpu = new FakeRadeon();
            var journal = new FakeJournal();
            var first = new Rig(gpu, journal);
            await first.Amd.ApplyAutoTuningAsync(GpuAutoTuning.Undervolt);
            Eq("0;2430;2425;1;1", journal.Backups.Single().Value, "record");
            Eq(-50, gpu.VoltageMv, "undervolted");
            first.Admin.Kill();
            first.Plain.Kill();
            gpu.NewHelperProcess();
            var next = new Rig(gpu, journal);
            await next.Amd.RecoverTuningAsync();
            Has(next.Admin.Sent, "amd.tuning.reset 0", "reset");
            Eq(0, next.Sent(next.Admin, "amd.tuning.revert"), "not the three values");
            True(gpu.AtFactory, "factory, voltage included");
            Eq(0, journal.Backups.Count, "record removed");

            var direct = new Rig();
            direct.Gpu.VoltageMv = -50;
            await direct.Admin.EnsureAsync();
            Eq(null, await direct.Amd.RevertTuningAsync(new BoostBackup { Kind = "amdoc", Target = "0", Value = "0;2430;2425;1;1" }), "전체 되돌리기");
            Has(direct.Admin.Sent, "amd.tuning.reset 0", "reset from the backup screen too");
        });

        Test("auto tuning not from factory values: the three values go back (an undervolt's voltage may stay)", async () =>
        {
            var r = new Rig();
            r.Gpu.Power = 3;
            await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.OverclockGpu);
            Eq("3;2430;2425;1;0", r.Journal.Backups.Single().Value, "record");
            r.Admin.Kill();
            r.Gpu.NewHelperProcess();
            await r.Amd.WatchTuningAsync();
            Has(r.Admin.Sent, "amd.tuning.revert 0 3 2430 2425", "three values");
            Eq(0, r.Sent(r.Admin, "amd.tuning.reset"), "no reset: it would also drop the user's own +3%");
            Eq((3, 2430), (r.Gpu.Power, r.Gpu.GpuMax), "back");

            var uv = new Rig();
            uv.Gpu.VoltageMv = -50;
            await uv.Admin.EnsureAsync();
            await uv.Amd.RevertTuningAsync(new BoostBackup { Kind = "amdoc", Target = "0", Value = "3;2430;2425;1;0" });
            Has(uv.Admin.Sent, "amd.tuning.revert 0 3 2430 2425", "undervolt record");
            Eq(-50, uv.Gpu.VoltageMv, "voltage stays (documented)");
        });

        // ---------------------------------------------------------------- a helper built without ADLX (SB_ADLX=OFF)
        Test("off build: every feature off with its own text, no prompt, nothing written", async () =>
        {
            var r = new Rig();
            r.Gpu.Status = "off";
            r.Choice.GpuMaxMHz = 2500;
            const string text = AmdProtocol.OffText + U;
            var info = await r.Amd.ReadInfoAsync();
            Eq(("off", 0), (info.Status, info.Gpus.Count), "info");
            True(r.Amd.Off, "off");
            var caps = r.Amd.GetCapabilities();
            True(!caps.GameProfile && !caps.AnyTuning && !caps.ResetToFactory, "all off");
            Eq((AmdProtocol.OffText, AmdProtocol.OffText), (caps.GameProfileNote, caps.TuningNote), "notes");
            Eq(text, await r.Amd.ApplyTuningAsync(), "tuning");
            Eq(text, await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.Undervolt), "auto");
            Eq(text, await r.Amd.ApplyGameProfileAsync("game.exe"), "profile");
            Eq(text, await r.Amd.ResetToFactoryAsync(), "reset");
            True(await r.Amd.ReadTelemetryAsync() == null && await r.Amd.ReadProfileAsync() == null, "no readings");
            Eq(0, r.Admin.Prompts + r.Gpu.Writes.Count + r.Journal.Backups.Count, "nothing");
            Eq(AmdProtocol.OffText, AmdProtocol.ErrorText("off"), "error word");
            True(!AmdProtocol.OffText.Contains("업데이트"), "not a driver update");
            Eq(new GpuPick(GpuVendor.Amd, -1), GpuSelection.Choose(false, false, Array.Empty<AmdGpuInfo>(), true, amdOff: true), "picked as AMD");
            Eq(new GpuPick(GpuVendor.Nvidia, -1), GpuSelection.Choose(false, true, Array.Empty<AmdGpuInfo>(), false, amdOff: true), "an NVIDIA driver first");
        });

        Test("off build: records of a build with ADLX are kept, no prompt, user told", async () =>
        {
            var r = new Rig();
            r.Gpu.Status = "off";
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.oc", Kind = "amdoc", Target = "0", Value = "0;2430;2425;0;1" });
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.driver", Kind = "amd3d", Key = "0", Value = "0;1;1;0" });
            await r.Amd.RecoverTuningAsync();
            await r.Amd.RecoverProfileAsync();
            Eq(2, r.Journal.Backups.Count, "both kept");
            Eq(0, r.Admin.Prompts, "no prompt");
            Eq("지난번에 바꾼 AMD 그래픽카드 설정(전력·클럭)을 이 버전에서는 되돌릴 수 없어요. AMD Software의 튜닝 화면에서 초기화해 주세요", r.Notices[0], "tuning");
            Eq("지난번 게임 중에 바꾼 AMD 3D 설정(Anti-Lag·Radeon Chill)을 이 버전에서는 되돌릴 수 없어요. AMD Software에서 직접 확인해 주세요", r.Notices[1], "profile");
            await r.Amd.WatchTuningAsync();
            Eq(0, r.Admin.Sent.Count, "the watch leaves it");
            await r.Admin.EnsureAsync();
            Eq(AmdProtocol.OffText, await r.Amd.RevertTuningAsync(r.Journal.Backups[0]), "전체 되돌리기: tuning stays listed");
            Eq(AmdProtocol.OffText, await r.Amd.RevertGameProfileAsync(r.Journal.Backups[1]), "전체 되돌리기: profile stays listed");
        });

        Test("plan: tuning is Ultra, the factory reset is on every plan", async () =>
        {
            var r = new Rig { Allowed = false };
            r.Choice.GpuMaxMHz = 2500;
            Eq("울트라 기능이에요" + U, await r.Amd.ApplyTuningAsync(), "manual");
            Eq("울트라 기능이에요" + U, await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.OverclockGpu), "auto");
            Eq(0, r.Plain.Starts + r.Admin.Prompts, "not even a read");
            r.Gpu.GpuMax = 2500;
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.oc", Kind = "amdoc", Target = "0", Value = "0;2430;2425" });
            Eq("공장 초기값으로 돌렸어요", await r.Amd.ResetToFactoryAsync(), "reset");
            True(r.Gpu.AtFactory, "card at factory");
            Eq(0, r.Journal.Backups.Count, "record gone with it");
        });

        Test("consent: every AMD tuning needs the overclocking notice agreed to for this card; the old tick counts for nothing", async () =>
        {
            var r = new Rig();
            r.Choice.GpuMaxMHz = 2400;   // even a lowering
            r.Choice.RiskAccepted = true;   // a tick from 0.8.13 to 0.8.15 in an older boost-state.json
            r.Choice.RiskTerms = 2;
            string? asked = null;
            r.Consent = name => { asked = name; return false; };
            Eq(OverclockNotice.NotAgreed + U, await r.Amd.ApplyTuningAsync(), "no consent");
            Eq(OverclockNotice.NotAgreed + U, await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.Undervolt), "no consent, auto");
            Eq(r.Gpu.Name, asked, "asked for the card being tuned");
            Eq(0, r.Admin.Prompts, "no prompt");
            Eq(0, r.Journal.Backups.Count + r.Gpu.Writes.Count, "nothing written");
        });

        Test("laptop and APU: tuning and reset off with 데스크톱 그래픽카드 전용, game profile stays", async () =>
        {
            foreach (var (laptop, integrated) in new[] { (true, false), (false, true) })
            {
                var r = new Rig { Laptop = laptop };
                r.Gpu.Integrated = integrated;
                r.Choice.GpuMaxMHz = 2500;
                await r.Amd.ReadInfoAsync();
                var caps = r.Amd.GetCapabilities();
                True(!caps.AnyTuning && !caps.ResetToFactory, "tuning off");
                Eq(GpuCapabilities.DesktopOnly, caps.TuningNote, "note");
                True(caps.GameProfile, "game profile on");
                const string text = "데스크톱 그래픽카드 전용이에요" + U;
                Eq(text, await r.Amd.ApplyTuningAsync(), "manual");
                Eq(text, await r.Amd.ApplyAutoTuningAsync(GpuAutoTuning.Undervolt), "auto");
                Eq(text, await r.Amd.ResetToFactoryAsync(), "reset");
                Eq(0, r.Admin.Prompts, "no prompt");
                True((await r.Amd.ApplyGameProfileAsync("game.exe")).StartsWith("Anti-Lag를 켰어요", StringComparison.Ordinal), "profile applies");
            }
        });

        Test("administrator prompt declined: nothing written", async () =>
        {
            var r = new Rig();
            r.Admin.EnsureError = "관리자 권한을 받지 못했어요" + U;
            r.Choice.GpuMaxMHz = 2500;
            Eq("관리자 권한을 받지 못했어요" + U, await r.Amd.ApplyTuningAsync(), "tuning");
            Eq("관리자 권한을 받지 못했어요" + U, await r.Amd.ApplyGameProfileAsync("game.exe"), "profile");
            Eq(0, r.Journal.Backups.Count + r.Gpu.Writes.Count, "nothing written");
        });

        Test("plain helper cannot start: its text, nothing kept", async () =>
        {
            var r = new Rig();
            r.Plain.EnsureError = "도우미(sb_helper.exe)를 찾지 못했어요" + U;
            var info = await r.Amd.ReadInfoAsync();
            Eq("no_helper", info.Status, "status");
            Eq(null, r.Amd.Info, "not kept");
            Eq("도우미(sb_helper.exe)를 찾지 못했어요", AmdProtocol.InfoProblem(info), "text");
        });

        Test("revert when the driver is gone: only the record goes", async () =>
        {
            var r = new Rig();
            r.Gpu.Status = "no_gpu";
            await r.Admin.EnsureAsync();
            Eq(null, await r.Amd.RevertTuningAsync(new BoostBackup { Kind = "amdoc", Target = "0", Value = "0;2430;2425" }), "tuning");
            Eq(null, await r.Amd.RevertGameProfileAsync(new BoostBackup { Kind = "amd3d", Key = "0", Value = "0;1;1;0" }), "profile");
            Eq("GPU 백업 값이 잘못됐어요", await r.Amd.RevertTuningAsync(new BoostBackup { Kind = "amdoc", Target = "x", Value = "0;1;2" }), "bad target");
            Eq("AMD 3D 설정 백업 값이 잘못됐어요", await r.Amd.RevertGameProfileAsync(new BoostBackup { Kind = "amd3d", Key = "0", Value = "bad" }), "bad value");
        });

        // ---------------------------------------------------------------- game profile
        Test("game profile: journal first, Anti-Lag on, Chill off, Boost status only, revert", async () =>
        {
            var r = new Rig();
            bool onDisk = false;
            r.Gpu.BeforeWrite = cmd => { if (cmd == "amd.profile.apply") onDisk = r.Journal.OnDisk.Contains(AmdProtocol.ProfileKind); };
            Eq("Anti-Lag를 켰어요 · Radeon Chill을 껐어요 · Radeon Boost 꺼짐 (바꾸지 않아요)", await r.Amd.ApplyGameProfileAsync("game.exe"), "text");
            True(onDisk, "amd3d saved before amd.profile.apply");
            Eq("amd.profile.apply 0 1 0", r.Gpu.Writes.Single(), "command");
            var rec = r.Journal.Backups.Single();
            Eq(("gpu.driver", "amd3d", "game.exe", "0", "0;1;1;0"), (rec.Feature, rec.Kind, rec.Target, rec.Key, rec.Value), "record");
            Eq(('1', '0', '0'), (r.Gpu.AntiLag, r.Gpu.Chill, r.Gpu.Boost), "card");

            Eq("이미 적용돼 있어요 · Anti-Lag 켜짐 · Radeon Chill 꺼짐 · Radeon Boost 꺼짐", await r.Amd.ApplyGameProfileAsync("game.exe"), "again");
            Eq(1, r.Journal.Backups.Count, "one record");
            Eq(1, r.Gpu.Writes.Count, "one write");

            Eq(null, await r.Amd.RevertGameProfileAsync(rec), "revert");
            Eq(('0', '1'), (r.Gpu.AntiLag, r.Gpu.Chill), "back");
            Eq(null, r.Amd.TakeRevertNote(), "nothing to report");
        });

        Test("game profile: nothing to change, no record", async () =>
        {
            var r = new Rig();
            r.Gpu.AntiLag = '1';
            r.Gpu.Chill = '0';
            r.Gpu.Boost = '-';
            Eq("Anti-Lag 켜짐 · Radeon Chill 꺼짐" + U, await r.Amd.ApplyGameProfileAsync("game.exe"), "text");
            Eq(0, r.Journal.Backups.Count + r.Gpu.Writes.Count, "nothing written");
        });

        Test("game profile: only Chill on this card", async () =>
        {
            var r = new Rig();
            r.Gpu.AntiLag = '-';
            Eq("Radeon Chill을 껐어요 · Radeon Boost 꺼짐 (바꾸지 않아요)", await r.Amd.ApplyGameProfileAsync("game.exe"), "text");
            Eq("-;1;-;0", r.Journal.Backups.Single().Value, "record");
            Eq("amd.profile.apply 0 - 0", r.Gpu.Writes.Single(), "command");
        });

        Test("game profile: apply error takes the record out", async () =>
        {
            var r = new Rig();
            r.Gpu.FailNext = "failed";
            bool onDisk = false;
            r.Gpu.BeforeWrite = cmd => { if (cmd == "amd.profile.apply") onDisk = r.Journal.OnDisk.Contains(AmdProtocol.ProfileKind); };
            Eq("적용하지 못했어요: AMD 드라이버가 값을 받지 않았어요" + U, await r.Amd.ApplyGameProfileAsync("game.exe"), "text");
            True(onDisk, "journal first");
            Eq(0, r.Journal.Backups.Count + r.Journal.OnDisk.Count, "record removed and saved");
            Eq(('0', '1'), (r.Gpu.AntiLag, r.Gpu.Chill), "card unchanged");
        });

        Test("game profile: an old record already put back is finished before a new one", async () =>
        {
            var r = new Rig();
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.driver", Kind = "amd3d", Target = "old.exe", Key = "0", Value = "0;1;1;0" });
            await r.Amd.ApplyGameProfileAsync("game.exe");
            var rec = r.Journal.Backups.Single();
            Eq(("game.exe", "0;1;1;0"), (rec.Target, rec.Value), "new record only");
            Eq(('1', '0'), (r.Gpu.AntiLag, r.Gpu.Chill), "applied");
        });

        Test("recover profile: put back already -> record dropped, no prompt", async () =>
        {
            var r = new Rig();
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.driver", Kind = "amd3d", Key = "0", Value = "0;1;1;0" });
            await r.Amd.RecoverProfileAsync();
            Eq(0, r.Journal.Backups.Count, "dropped");
            Eq(0, r.Admin.Prompts + r.Notices.Count, "no prompt, nothing to tell");
        });

        Test("recover profile: still set, no elevated helper -> kept, user told, no prompt", async () =>
        {
            var r = new Rig();
            r.Gpu.AntiLag = '1';
            r.Gpu.Chill = '0';
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.driver", Kind = "amd3d", Key = "0", Value = "0;1;1;0" });
            await r.Amd.RecoverProfileAsync();
            Eq(1, r.Journal.Backups.Count, "kept for later");
            Eq(0, r.Admin.Prompts + r.Gpu.Writes.Count, "no prompt, no write");
            Eq("지난번 게임 중에 바꾼 AMD 3D 설정(Anti-Lag·Radeon Chill)이 남아 있어요. 다음 게임이 끝날 때나 '백업 · 되돌리기'에서 원래대로 돌려요",
               r.Notices.Single(), "notice");
        });

        Test("recover profile: still set, elevated helper running -> put back", async () =>
        {
            var r = new Rig();
            r.Gpu.AntiLag = '1';
            r.Gpu.Chill = '0';
            r.Journal.Backups.Add(new BoostBackup { Feature = "gpu.driver", Kind = "amd3d", Key = "0", Value = "0;1;1;0" });
            await r.Admin.EnsureAsync();
            await r.Amd.RecoverProfileAsync();
            Eq(0, r.Journal.Backups.Count, "removed");
            Eq(('0', '1'), (r.Gpu.AntiLag, r.Gpu.Chill), "card back");
            Eq(0, r.Notices.Count, "nothing left to tell");
            Eq(0, r.Gpu.Writes.Count(w => w.StartsWith("amd.profile.apply", StringComparison.Ordinal)), "never applied again");
        });

        Test("recover profile: driver gone or record unreadable -> dropped", async () =>
        {
            var r = new Rig { Dll = false };
            r.Journal.Backups.Add(new BoostBackup { Kind = "amd3d", Key = "0", Value = "0;1;1;0" });
            await r.Amd.RecoverProfileAsync();
            Eq(0, r.Journal.Backups.Count, "driver gone");
            var bad = new Rig();
            bad.Journal.Backups.Add(new BoostBackup { Kind = "amd3d", Key = "0", Value = "x" });
            await bad.Amd.RecoverProfileAsync();
            Eq(0, bad.Journal.Backups.Count, "unreadable");
        });

        // ---------------------------------------------------------------- choosing the card
        Test("vendor choice: discrete before integrated, NVIDIA kept beside an AMD APU", () =>
        {
            var apu = Gpu(0, integrated: true);
            var rx = Gpu(1, integrated: false);
            Eq(new GpuPick(GpuVendor.Nvidia, -1), GpuSelection.Choose(true, true, new[] { rx }, false), "NVML reads a card");
            Eq(new GpuPick(GpuVendor.Amd, 1), GpuSelection.Choose(false, false, new[] { apu, rx }, true), "discrete AMD, not the APU");
            Eq(new GpuPick(GpuVendor.Nvidia, -1), GpuSelection.Choose(false, true, new[] { apu }, false), "NVIDIA driver beside an APU");
            Eq(new GpuPick(GpuVendor.Nvidia, -1), GpuSelection.Choose(true, true, new[] { apu }, false), "NVIDIA card beside an APU");
            Eq(new GpuPick(GpuVendor.Amd, 0), GpuSelection.Choose(false, false, new[] { apu }, true), "APU only (Intel name ignored)");
            Eq(new GpuPick(GpuVendor.Intel, -1), GpuSelection.Choose(false, false, Array.Empty<AmdGpuInfo>(), true), "Intel only");
            Eq(new GpuPick(GpuVendor.None, -1), GpuSelection.Choose(false, false, Array.Empty<AmdGpuInfo>(), false), "nothing");
            Eq(1, AmdProtocol.Pick(new[] { apu, rx })!.Index, "Pick: discrete first");
        });

        Test("selected index is used for every command", async () =>
        {
            var r = new Rig();
            await r.Amd.ReadInfoAsync();
            r.Amd.Select(5);   // not in the list: falls back to the first discrete card
            Eq(0, r.Amd.Gpu!.Index, "fallback");
        });

        // ---------------------------------------------------------------- protocol
        Test("amd.info parsing", () =>
        {
            var info = AmdProtocol.ParseInfo(new FakeRadeon().InfoJson());
            Eq(("ok", "1.4", "24.10.1", 1), (info.Status, info.Adlx, info.Driver, info.Gpus.Count), "header");
            var g = info.Gpus[0];
            Eq(new AmdRange(true, 500, 2900, 1, 2430), g.GpuMax, "gpuMax");
            Eq(new AmdRange(true, -10, 15, 1, 0), g.Power, "power");
            True(g.AtFactory && !g.Integrated && g.AutoOverclockGpu, "flags");
            Eq(new AmdSwitch(true, true), g.Chill, "chill");

            var odd = AmdProtocol.ParseInfo("{\"status\":\"ok\",\"gpus\":[{\"i\":9,\"name\":\"x\"},{\"i\":1,\"name\":\"Y\",\"power\":{\"ok\":false},"
                                            + "\"gpuMax\":{\"ok\":true,\"min\":500,\"max\":2900,\"now\":2400},\"vramMax\":{\"ok\":true,\"min\":9,\"max\":1,\"now\":5}}]}");
            Eq(1, odd.Gpus.Count, "index outside 0..7 skipped");
            Eq(AmdRange.None, odd.Gpus[0].Power, "ok:false = not supported");
            Eq(1, odd.Gpus[0].GpuMax.Step, "step missing = 1");
            Eq(AmdRange.None, odd.Gpus[0].VramMax, "min above max = not supported");
            Eq(AmdSwitch.None, odd.Gpus[0].Afmf, "missing switch = not supported");
            Eq("bad_reply", AmdProtocol.ParseInfo("not json").Status, "not json");
            Eq("bad_reply", AmdProtocol.ParseInfo("{}").Status, "no status");
            Eq("bad_reply", AmdProtocol.ParseInfo("[1]").Status, "not an object");
            var none = AmdProtocol.ParseInfo("{\"status\":\"no_gpu\",\"gpus\":[]}");
            Eq(("no_gpu", 0), (none.Status, none.Gpus.Count), "no gpu");
            Eq("AMD 그래픽카드를 찾지 못했어요", AmdProtocol.InfoProblem(none), "no gpu text");
            Eq(null, AmdProtocol.InfoProblem(info), "fine");
            Eq("AMD 드라이버에서 정보를 읽지 못했어요", AmdProtocol.InfoProblem(AmdInfo.Of("bad_reply")), "bad reply text");
        });

        Test("other replies", () =>
        {
            Eq(new GpuTelemetry(62, 75, 2400, 2425, 180, 97, 6120), AmdProtocol.ParseMetrics("62;75;2400;2425;180;97;6120"), "metrics");
            Eq(new GpuTelemetry(-1, -1, 2400, -1, -1, -1, -1), AmdProtocol.ParseMetrics("-1;-1;2400;-1;-1;-1;-1"), "not reported");
            Eq(null, AmdProtocol.ParseMetrics("1;2;3"), "short");
            Eq(new AmdProfileState('1', '0', '-', '1'), AmdProtocol.ParseProfile("1;0;-;1"), "profile");
            Eq(null, AmdProtocol.ParseProfile("1;0;2;1"), "bad profile");
            Eq(new AmdTuningStatus(true, false, 70, "0"), AmdProtocol.ParseStatus("1;0;70;0"), "status");
            Eq(null, AmdProtocol.ParseStatus("1;0;70;hot"), "unknown reason");
            Eq("antiLag,chill", string.Join(",", AmdProtocol.ParseChanged("changed=antiLag,chill")), "changed");
            Eq(0, AmdProtocol.ParseChanged("").Count, "nothing changed");
        });

        Test("commands", () =>
        {
            string J(string[] f) => string.Join(" ", f);
            Eq("amd.tuning.apply 0 - 2500 - 60", J(AmdProtocol.TuningApply(0, new AmdTuningValues(null, 2500, null), 50)), "limit at least 60");
            Eq("amd.tuning.apply 1 -5 - 2450 83", J(AmdProtocol.TuningApply(1, new AmdTuningValues(-5, null, 2450), 83)), "default");
            Eq("amd.tuning.revert 0 0 2430 -", J(AmdProtocol.TuningRevert(0, new AmdTuningValues(0, 2430, null))), "revert");
            Eq("amd.profile.revert 0 0 1 1 0", J(AmdProtocol.ProfileRevert(0, new AmdProfilePlan('0', '1', '1', '0'))), "profile revert");
            Eq("amd.auto.apply 0 undervolt", J(AmdProtocol.AutoApply(0, GpuAutoTuning.Undervolt)), "undervolt");
            Eq("amd.auto.apply 0 ocgpu", J(AmdProtocol.AutoApply(0, GpuAutoTuning.OverclockGpu)), "ocgpu");
            Eq("amd.tuning.reset 2", J(AmdProtocol.TuningReset(2)), "reset");
            Eq(83, new AmdGpuChoice().TempLimitC, "83 °C by default");
            Eq(false, new AmdGpuChoice().RiskAccepted, "off by default");
        });

        Test("journal values", () =>
        {
            Eq("5;-;2450", AmdProtocol.Encode(new AmdTuningValues(5, null, 2450)), "encode");
            Eq(new AmdTuningValues(5, null, 2450), AmdProtocol.DecodeTuning("5;-;2450"), "decode");
            Eq(new AmdTuningValues(-10, 1930, 2400), AmdProtocol.DecodeTuning(AmdProtocol.Encode(new AmdTuningValues(-10, 1930, 2400))), "round trip");
            Eq(null, AmdProtocol.DecodeTuning("1;2"), "short");
            Eq(null, AmdProtocol.DecodeTuning("a;b;c"), "not numbers");
            Eq("0;1;1;0", AmdProtocol.Encode(new AmdProfilePlan('0', '1', '1', '0')), "profile encode");
            Eq(new AmdProfilePlan('-', '1', '-', '0'), AmdProtocol.DecodeProfile("-;1;-;0"), "profile decode");
            Eq(null, AmdProtocol.DecodeProfile("0;1;2;0"), "bad profile");
            var vals = new AmdTuningValues(0, 2430, 2425);
            Eq(new AmdTuningRecord(vals, false, false), AmdProtocol.DecodeRecord("0;2430;2425"), "three fields: manual, factory unknown");
            Eq(new AmdTuningRecord(vals, true, true), AmdProtocol.DecodeRecord("0;2430;2425;1;1"), "five fields");
            Eq("0;2430;2425;1;0", AmdProtocol.Encode(new AmdTuningRecord(vals, true, false)), "encode five");
            Eq(vals, AmdProtocol.DecodeTuning("0;2430;2425;0;1"), "values of either form");
            Eq(null, AmdProtocol.DecodeRecord("0;2430;2425;2;1"), "bad flag");
            Eq(null, AmdProtocol.DecodeRecord("0;2430;2425;1"), "four fields");
            Eq("amd.tuning.reset 0", string.Join(" ", AmdProtocol.RevertFor(0, new AmdTuningRecord(vals, true, true))), "auto from factory");
            Eq("amd.tuning.revert 0 0 2430 2425", string.Join(" ", AmdProtocol.RevertFor(0, new AmdTuningRecord(vals, true, false))), "auto otherwise");
            Eq("amd.tuning.revert 0 0 2430 2425", string.Join(" ", AmdProtocol.RevertFor(0, new AmdTuningRecord(vals, false, true))), "manual");
            Eq(AmdProfileCheck.StillSet, AmdProtocol.CheckProfile(new AmdProfilePlan('0', '1', '1', '0'), new AmdProfileState('1', '0', '0', '0')), "still set");
            Eq(AmdProfileCheck.AlreadyBack, AmdProtocol.CheckProfile(new AmdProfilePlan('0', '1', '1', '0'), new AmdProfileState('0', '1', '0', '0')), "back");
            Eq(AmdProfileCheck.ChangedByUser, AmdProtocol.CheckProfile(new AmdProfilePlan('0', '1', '1', '0'), new AmdProfileState('1', '1', '0', '0')), "half");
        });

        Test("picker choices stay inside the card's range and the small raises", () =>
        {
            var power = AmdProtocol.Choices(new AmdRange(true, -10, 15, 1, 0), AmdProtocol.MaxPowerRaisePct, 1, 50);
            Eq((-10, 5, 16), (power.First(), power.Last(), power.Count), "power");
            var gpu = AmdProtocol.Choices(new AmdRange(true, 500, 2900, 1, 2430), AmdProtocol.MaxGpuRaiseMHz, 25, 500);
            Eq((1930, 2530, 25), (gpu.First(), gpu.Last(), gpu.Count), "gpu");
            True(gpu.Contains(2430), "now offered");
            var vram = AmdProtocol.Choices(new AmdRange(true, 2000, 2600, 10, 2400), AmdProtocol.MaxVramRaiseMHz, 25, 200);
            True(vram.All(v => (v - 2000) % 10 == 0 && v >= 2200 && v <= 2450), "on the card's step");
            Eq((2220, 2430), (vram.First(), vram.Last()), "vram rounded to 30");
            var top = AmdProtocol.Choices(new AmdRange(true, 500, 2450, 1, 2430), 100, 25, 0);
            Eq(2430, top.Last(), "never above max");
            Eq(0, AmdProtocol.Choices(AmdRange.None, 5, 1, 50).Count, "unsupported: none");
        });

        Test("error words to text", () =>
        {
            Eq("AMD 그래픽카드나 AMD 드라이버를 찾지 못했어요", AmdProtocol.ErrorText("no_amd"), "no_amd");
            Eq(GpuCapabilities.NotOnThisCard, AmdProtocol.ErrorText("unsupported"), "unsupported");
            Eq("관리자 도우미가 이 요청을 받지 않았어요. 앱을 다시 설치해 주세요", AmdProtocol.ErrorText("refused"), "refused");
            Eq("관리자 도우미와 연결이 끊겼어요", AmdProtocol.ErrorText("not running"), "not running");
            Eq("anything else", AmdProtocol.ErrorText("anything else"), "as it came");
            Eq(("켜짐", "꺼짐", "지원하지 않음"), (AmdProtocol.SwitchText('1'), AmdProtocol.SwitchText('0'), AmdProtocol.SwitchText('-')), "switches");
        });

        DefineConsent();   // ConsentTests.cs
    }

    private static AmdGpuInfo Gpu(int index, bool integrated) => new(index, integrated ? "AMD Radeon(TM) Graphics" : "AMD Radeon RX 7800 XT", integrated, true,
        AmdRange.None, AmdRange.None, AmdRange.None, false, false, false, AmdSwitch.None, AmdSwitch.None, AmdSwitch.None, AmdSwitch.None);
}
