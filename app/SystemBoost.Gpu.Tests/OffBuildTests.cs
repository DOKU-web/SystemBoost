// A build without ADLX (the default: build.ps1 without -Adlx, so SB_ADLX is not defined): AmdBuild says so, the
// separate consent to Article 12-2 (AmdTerms) is never asked for, read or offered, its windows never open, and a Radeon
// PC sees AmdProtocol.OffText with AmdProtocol.OffNote under it. This project is built without SbAdlx, like the release.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SystemBoost.App.Services;

namespace SystemBoost.Gpu.Tests;

internal static partial class Program
{
    /// The repository's app folder (app/SystemBoost.App), found upwards from the test's output folder.
    private static string AppSource()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var app = Path.Combine(d.FullName, "app", "SystemBoost.App");
            if (File.Exists(Path.Combine(app, "SystemBoost.App.csproj"))) return app;
        }
        throw new CheckFailed("app/SystemBoost.App not found above " + AppContext.BaseDirectory);
    }

    private static void DefineOffBuild()
    {
        Test("off build: AmdBuild has no Radeon driver features without SB_ADLX", () =>
            True(!AmdBuild.DriverFeatures, "AmdBuild.DriverFeatures"));

        Test("off build: the AMD terms are never required, read or offered; the windows may not open", () =>
        {
            bool Never() => throw new CheckFailed("consents.json read in a build without ADLX");
            foreach (var at in new[] { Before, AmdTerms.InForceUtc.AddTicks(-1), Midnight })
            {
                True(AmdTerms.Ok(AmdBuild.DriverFeatures, at, Never), $"Ok at {at:o}");
                True(!AmdTerms.Withdrawable(AmdBuild.DriverFeatures, at, Never), $"not withdrawable at {at:o}");
            }
            True(!AmdTerms.MayAsk(AmdBuild.DriverFeatures), "MayAsk");
            bool threw = false;
            try { AmdTerms.EnsureMayAsk(AmdBuild.DriverFeatures); }
            catch (InvalidOperationException) { threw = true; }
            True(threw, "EnsureMayAsk throws");
            // a build with ADLX, for contrast: before 2027-01-01 KST it waits for the consent
            True(AmdTerms.MayAsk(true), "ADLX build may ask");
            AmdTerms.EnsureMayAsk(true);
            True(!AmdTerms.Ok(true, Before, () => false), "ADLX build without a consent waits");
            True(AmdTerms.Ok(true, Before, () => true) && AmdTerms.Ok(true, Midnight, () => false), "agreed, or the date has come");
            True(AmdTerms.Withdrawable(true, Before, () => true) && !AmdTerms.Withdrawable(true, Midnight, () => true), "withdraw offered until the date");
        });

        Test("off build: every AmdTermsDialog / AmdTermsWithdrawDialog is guarded (source)", () =>
        {
            var app = AppSource();
            var dialog = File.ReadAllText(Path.Combine(app, "Pages", "AmdTermsDialog.cs"));
            foreach (var ctor in new[] { "public AmdTermsDialog(bool ask)", "public AmdTermsWithdrawDialog()" })
            {
                int at = dialog.IndexOf(ctor, StringComparison.Ordinal);
                True(at >= 0, ctor);
                int open = dialog.IndexOf('{', at);
                var first = dialog[(open + 1)..].TrimStart().Split('\n')[0].Trim();
                True(first.StartsWith("AmdTerms.EnsureDialogAllowed();", StringComparison.Ordinal), $"{ctor}: first line [{first}]");
            }
            Eq(2, Regex.Matches(dialog, @"\b(public|internal|private|protected)\s+AmdTerms(Withdraw)?Dialog\s*\(").Count, "no other constructor");
            int found = 0;
            var sep = Path.DirectorySeparatorChar;
            foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!Regex.IsMatch(lines[i], @"new\s+AmdTerms(Withdraw)?Dialog\s*\(")) continue;
                    found++;
                    var near = string.Join("\n", lines.Skip(Math.Max(0, i - 2)).Take(Math.Min(3, i + 1)));
                    True(near.Contains("AmdTerms.DialogAllowed"), $"{Path.GetFileName(file)}:{i + 1} opens a window without AmdTerms.DialogAllowed");
                }
            }
            True(found >= 3, $"the call sites ({found})");
        });

        Test("off build: a Radeon PC sees OffText and OffNote, never the consent; nothing sent beyond amd.info", async () =>
        {
            var r = new Rig();
            r.Gpu.Status = "off";
            r.Terms = AmdTerms.Ok(AmdBuild.DriverFeatures, r.Now, () => throw new CheckFailed("consents.json read"));
            var info = await r.Amd.ReadInfoAsync();
            True(!r.Amd.WaitsForTerms, "not waiting for the consent");
            Eq(AmdProtocol.OffText, AmdProtocol.InfoProblem(r.Amd.Info!), "the card's line");
            Eq("AMD 드라이버 기능은 아직 준비 중이에요", AmdProtocol.OffText, "the text");
            Eq("라데온 드라이버 기능은 라이선스 확인이 끝날 때까지 빠졌어요. 프레임 생성은 라데온에서도 그대로 쓸 수 있어요.", AmdProtocol.OffNote, "the note");
            var caps = r.Amd.GetCapabilities();
            Eq((AmdProtocol.OffText, AmdProtocol.OffText), (caps.GameProfileNote, caps.TuningNote), "switch notes");
            True(caps.GameProfileNote != AmdProtocol.TermsText, "no consent button under the switches");
            Eq(new GpuPick(GpuVendor.Amd, -1), GpuSelection.Choose(false, false, info.Gpus, false, amdOff: true), "the AMD card is shown");
            var sent = r.Plain.Sent.Concat(r.Admin.Sent).ToList();
            True(sent.All(s => s == "amd.info"), "only amd.info: " + string.Join(", ", sent));
            Eq(0, r.Admin.Prompts + r.Gpu.Writes.Count, "no prompt, no write");
            var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lang.en.json")))!;
            True(en.ContainsKey(AmdProtocol.OffText) && en.ContainsKey(AmdProtocol.OffNote), "both in English");
        });
    }
}
