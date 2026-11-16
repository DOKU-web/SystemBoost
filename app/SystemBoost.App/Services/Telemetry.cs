// Usage statistics for the seller's dashboard. Disclosed in 제8조 of Assets/eula.txt and in Settings:
// keep those two and server/license-worker/src/stats.js in step with what is sent here.
// Sent while the app runs: a random install number made on this PC, app version, operating system, plan,
// frame generation multiplier, one-click boost on or off. Never a key, mail address, game name or hardware detail.
// Optional and off until the user turns it on (0.8.14): the agreement dialog (EulaDialog) has its own box
// "사용 통계 보내기", unticked, and nothing is sent before that dialog is done (Start waits for Eula.WhenAccepted).
// A choice made before 0.8.14 (when the box started ticked) counts as off until the user chooses again (Asked).
// Off sends nothing at all. Turning it off also forgets the install number.
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public static class Telemetry
{
    // The server sets the pace and slows apps down on busy days; these only bound what it may ask for.
    private const int DefaultSeconds = 300, MinSeconds = 60, MaxSeconds = 3600;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static bool _started;

    // Asked: the round of the question the saved choice answers. 1 = 0.8.14, where statistics start off; files
    // from before have 0 (the field is missing) and send nothing until the user chooses again.
    private const int Round = 1;
    private sealed record Prefs(bool On = false, string Id = "", int Asked = 0);
    private static string PrefsFile => Path.Combine(Platform.DataDir, "telemetry.json");

    private static Prefs Read()
    {
        try { return File.Exists(PrefsFile) ? JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsFile)) ?? new Prefs() : new Prefs(); }
        catch (Exception) { return new Prefs(); }   // a damaged file: off
    }

    private static bool IsOn(Prefs p) => p.On && p.Asked >= Round;

    private static bool Write(Prefs p)
    {
        try { File.WriteAllText(PrefsFile, JsonSerializer.Serialize(p)); return true; }
        catch (Exception) { return false; }
    }

    // This run's choice, so "off" holds even when the file cannot be written (a read-only folder).
    private static bool? _choice;

    public static bool Enabled
    {
        get => _choice ?? IsOn(Read());
        set { _choice = value; Write(new Prefs(value, "", Round)); }
    }

    /// The agreement dialog's answer. Writes only what changes: a new install number would count as a new install,
    /// so someone who sent statistics before and says yes again keeps theirs.
    public static void Choose(bool on)
    {
        var p = Read();
        if (IsOn(p) == on && (p.Asked >= Round || !File.Exists(PrefsFile))) { _choice = on; return; }
        if (on && p.On && p.Id.Length > 0) { _choice = true; Write(p with { Asked = Round }); }
        else Enabled = on;
    }

    /// Call once, after the agreement is accepted. usageNow reports the frame generation multiplier (0 = off) and the one-click boost.
    public static void Start(Func<Task<(int frameGen, bool boost)>> usageNow)
    {
        if (_started || !License.ServerConfigured) return;
        _started = true;
        _ = Task.Run(async () =>
        {
            await Eula.WhenAccepted;   // the agreement dialog, with its statistics box, comes first
            while (true)
                await Task.Delay(TimeSpan.FromSeconds(await PingAsync(usageNow)));
        });
    }

    /// Returns the seconds until the next ping.
    private static async Task<int> PingAsync(Func<Task<(int frameGen, bool boost)>> usageNow)
    {
        var prefs = Read();
        if (!(_choice ?? IsOn(prefs))) return DefaultSeconds;
        if (prefs.Id.Length == 0)
        {
            prefs = prefs with { On = true, Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), Asked = Round };
            if (!Write(prefs)) return DefaultSeconds;   // a new number every start would count as a new install each time
        }
        try
        {
            var (frameGen, boost) = await usageNow();
            var body = JsonSerializer.Serialize(new
            {
                id = prefs.Id,
                ver = Updater.CurrentVersion.Split('+')[0],
                os = Platform.IsMac ? "mac" : "win",
                plan = License.IsBiz ? "biz" : License.IsUltra ? "ultra" : License.IsPremium ? "premium" : "free",
                fg = frameGen,
                boost = boost ? 1 : 0,
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync($"{License.ServerUrl}/ping", content);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return Math.Clamp(doc.RootElement.GetProperty("next").GetInt32(), MinSeconds, MaxSeconds);
        }
        catch (Exception) { return DefaultSeconds; }   // offline or server trouble: the app does not care
    }
}
