// Usage statistics for the seller's dashboard. Disclosed in 제8조 of Assets/eula.txt and in Settings:
// keep those two and server/license-worker/src/stats.js in step with what is sent here.
// Sent while the app runs: a random install number made on this PC, app version, operating system, plan,
// frame generation multiplier, one-click boost on or off. Never a key, mail address, game name or hardware detail.
// Starts only after the agreement is accepted. Turning it off in Settings also forgets the install number.
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

    private sealed record Prefs(bool On = true, string Id = "");
    private static string PrefsFile => Path.Combine(Platform.DataDir, "telemetry.json");

    private static Prefs Read()
    {
        try { return File.Exists(PrefsFile) ? JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsFile)) ?? new Prefs(false) : new Prefs(); }
        catch (Exception) { return new Prefs(false); }   // a damaged file may have said "off"
    }

    private static bool Write(Prefs p)
    {
        try { File.WriteAllText(PrefsFile, JsonSerializer.Serialize(p)); return true; }
        catch (Exception) { return false; }
    }

    public static bool Enabled
    {
        get => Read().On;
        set => Write(new Prefs(value));
    }

    /// Call once, after the agreement is accepted. usageNow reports the frame generation multiplier (0 = off) and the one-click boost.
    public static void Start(Func<Task<(int frameGen, bool boost)>> usageNow)
    {
        if (_started || !License.ServerConfigured) return;
        _started = true;
        _ = Task.Run(async () =>
        {
            while (true)
                await Task.Delay(TimeSpan.FromSeconds(await PingAsync(usageNow)));
        });
    }

    /// Returns the seconds until the next ping.
    private static async Task<int> PingAsync(Func<Task<(int frameGen, bool boost)>> usageNow)
    {
        var prefs = Read();
        if (!prefs.On) return DefaultSeconds;
        if (prefs.Id.Length == 0)
        {
            prefs = prefs with { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() };
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
                plan = License.IsUltra ? "ultra" : License.IsPremium ? "premium" : "free",
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
