// Free / Premium.
// Two ways to own Premium:
//  1) Steam build: the "SystemBoost Premium" DLC (checked through Steamworks;
//     ISteamOwnership is the hook - wire Steamworks.NET here).
//  2) Direct downloads (installer / portable / macOS): a license key bought on
//     the web shop. Key = base64url(payload JSON) "." base64url(ECDSA P-256 signature).
//     Only the PUBLIC key is in the app; keys are signed on the shop's server.
// Nothing here talks to a payment provider; the seller's payment page is opened in the browser.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SystemBoost.App.Services;

public enum PremiumFeature
{
    AutoAndHighMultiplier,   // auto, x3, x4 (x2 stays free)
    QualityMode,             // "quality" preset
    DualGpu,                 // frame generation on another GPU
    CpuLimit,                // resource sharing: hard CPU cap
    PerGameProfiles,
    Overclock,
}

public sealed record LicenseInfo(string Plan, string Email, DateTime? Expires, string Source, string? Sub = null);

public interface ISteamOwnership { bool OwnsPremiumDlc(); }

public static class License
{
    // License server (server/license-worker).
    public const string ServerUrl = "https://systemboost-license.euning97.workers.dev";
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    // SubjectPublicKeyInfo (DER, base64) of the shop's signing key. TODO: replace.
    private const string PublicKeyB64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEY8H/jVOrF8oTVDz8WRucglw4zrjLpBkic0VMbIs3gIFypN5xTnt8EPDf5h1hKFVz6z7Jp+O/E87cxU4CADvO2Q==";

    public static ISteamOwnership? Steam { get; set; }
    public static event Action? Changed;
    private static LicenseInfo? _info;
    private static string KeyFile => Path.Combine(Platform.DataDir, "license.key");

    public static LicenseInfo? Info { get { Load(); return _info; } }
    public static bool IsPremium
    {
        get
        {
            Load();
            if (Steam?.OwnsPremiumDlc() == true) return true;
            return _info != null && (_info.Expires == null || _info.Expires > DateTime.UtcNow);
        }
    }

    public static bool Allowed(PremiumFeature f) => IsPremium;

    // Activation can happen on a background thread (order watcher, revocation check): UI handlers run on the UI thread.
    private static void RaiseChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke());

    private static bool _loaded;
    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try { if (File.Exists(KeyFile)) _info = Verify(File.ReadAllText(KeyFile).Trim(), out _); }
        catch (Exception) { _info = null; }
    }

    /// Checks and stores a key. Returns null on success, else a message.
    public static string? Activate(string key)
    {
        var info = Verify(key.Trim(), out var error);
        if (info == null) return error;
        File.WriteAllText(KeyFile, key.Trim());
        _info = info; _loaded = true;
        RaiseChanged();
        return null;
    }

    public static void Deactivate()
    {
        try { File.Delete(KeyFile); } catch (Exception) { }
        _info = null;
        RaiseChanged();
    }

    private static LicenseInfo? Verify(string key, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(PublicKeyB64)) { error = "아직 결제 연동 전이라 키를 확인할 수 없어요"; return null; }
        var parts = key.Split('.');
        if (parts.Length != 2) { error = "키 형식이 맞지 않아요"; return null; }
        byte[] payload, sig;
        try { payload = B64Url(parts[0]); sig = B64Url(parts[1]); }
        catch (FormatException) { error = "키 형식이 맞지 않아요"; return null; }
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyB64), out _);
        if (!ec.VerifyData(payload, sig, HashAlgorithmName.SHA256)) { error = "올바른 키가 아니에요"; return null; }
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
        var r = doc.RootElement;
        DateTime? exp = r.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.String ? DateTime.Parse(e.GetString()!).ToUniversalTime() : null;
        if (exp != null && exp < DateTime.UtcNow) { error = "기간이 끝난 키예요. 구독을 연장하세요"; return null; }
        return new LicenseInfo(r.GetProperty("plan").GetString() ?? "premium", r.TryGetProperty("email", out var m) ? m.GetString() ?? "" : "", exp, "key",
                               r.TryGetProperty("sub", out var sb) ? sb.GetString() : null);
    }

    // ---------------------------------------------------------- purchase flow
    // The seller takes payment on their own payment page (link set in the admin dashboard).
    // 1) app asks the server for an order code  2) buyer pays, writing the code in the payment memo
    // 3) seller confirms the payment and issues the key for that code in the dashboard
    // 4) this app, polling /claim?session=<code>, registers the key and pops it up.
    // No payment link on the server = buy button stays "결제 준비 중" and no key can come out.
    public static bool ServerConfigured => !ServerUrl.Contains("YOUR-SUBDOMAIN");

    /// Plans that can be bought right now, per payment method:
    ///   "card"   = card checkout (Lemon Squeezy); monthly/yearly save the card and renew automatically,
    ///              the key is issued automatically when the payment is verified
    ///   "manual" = the seller's own payment link (bank transfer etc.); the seller approves
    /// Both empty = sales not open yet, or offline.
    public static async System.Threading.Tasks.Task<(bool reachable, System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> methods)> GetShopStatusAsync()
    {
        var methods = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> { ["card"] = new(), ["manual"] = new() };
        if (!ServerConfigured) return (false, methods);
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{ServerUrl}/status"));
            if (doc.RootElement.TryGetProperty("methods", out var ms) && ms.ValueKind == JsonValueKind.Object)
                foreach (var m in ms.EnumerateObject())
                    if (methods.TryGetValue(m.Name, out var set) && m.Value.ValueKind == JsonValueKind.Array)
                        foreach (var p in m.Value.EnumerateArray()) if (p.GetString() is string name) set.Add(name);
            return (true, methods);
        }
        catch (Exception) { return (false, methods); }
    }

    /// Status "paid" = the buyer said they paid and the seller is checking.
    public sealed record PendingOrder(string Code, string Plan, DateTime Created, string Url = "", string Method = "manual", string Status = "requested");
    private static string PendingFile => Path.Combine(Platform.DataDir, "pending-order.json");
    public static PendingOrder? Pending
    {
        get
        {
            try
            {
                if (!File.Exists(PendingFile)) return null;
                var o = JsonSerializer.Deserialize<PendingOrder>(File.ReadAllText(PendingFile));
                if (o == null || DateTime.UtcNow - o.Created > TimeSpan.FromDays(14)) { ForgetPending(); return null; }
                return o;
            }
            catch (Exception) { return null; }
        }
    }
    public static void ForgetPending() { try { File.Delete(PendingFile); } catch (Exception) { } }
    private static void SavePending(PendingOrder o) => File.WriteAllText(PendingFile, JsonSerializer.Serialize(o));

    /// Manual payments: tell the seller "I paid", with the payer name / memo used for the payment.
    public static async System.Threading.Tasks.Task<string?> MarkPaidAsync(string payer, string memo)
    {
        if (Pending is not PendingOrder o) return "주문이 없어요";
        try
        {
            var body = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { payer, msg = memo }), Encoding.UTF8, "application/json");
            var r = await Http.PostAsync($"{ServerUrl}/order/paid?code={Uri.EscapeDataString(o.Code)}", body);
            if (!r.IsSuccessStatusCode) return "알리지 못했어요. 잠시 뒤 다시 시도하세요.";
            SavePending(o with { Status = "paid" });
            return null;
        }
        catch (Exception) { return "결제 서버에 연결하지 못했어요. 인터넷 연결을 확인하세요."; }
    }

    /// Gets an order code + the payment link for a plan. error is a message for the user.
    public static async System.Threading.Tasks.Task<(string? code, string? url, string? error)> CreateOrderAsync(string plan, string method)
    {
        if (!ServerConfigured) return (null, null, "결제 준비 중이에요. 결제가 열리면 여기서 바로 살 수 있어요.");
        try
        {
            var r = await Http.PostAsync($"{ServerUrl}/order?plan={Uri.EscapeDataString(plan)}&method={Uri.EscapeDataString(method)}", null);
            if ((int)r.StatusCode == 503) return (null, null, "이 플랜은 아직 결제 준비 중이에요.");
            if (!r.IsSuccessStatusCode) return (null, null, "주문을 만들지 못했어요. 잠시 뒤 다시 시도하세요.");
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var code = doc.RootElement.GetProperty("code").GetString()!;
            var url = doc.RootElement.GetProperty("url").GetString();
            var serverMethod = doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() ?? "manual" : "manual";
            SavePending(new PendingOrder(code, plan, DateTime.UtcNow, url ?? "", serverMethod));
            WatchPending();
            return (code, url, null);
        }
        catch (Exception) { return (null, null, "결제 서버에 연결하지 못했어요. 인터넷 연결을 확인하세요."); }
    }

    /// Raised on the UI thread when the key for the pending order arrived and was registered.
    /// Arguments: key, activation error (null = premium is on).
    public static event Action<string, string?>? KeyArrived;
    private static int _watching;

    /// Polls for the pending order's key while the app runs: every 5 s for the first 15 minutes
    /// after ordering, then every minute (the seller may confirm hours later). Survives restarts.
    public static void WatchPending()
    {
        if (System.Threading.Interlocked.Exchange(ref _watching, 1) == 1) return;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                while (Pending is PendingOrder o)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{ServerUrl}/claim?session={Uri.EscapeDataString(o.Code)}"));
                        var st = doc.RootElement.GetProperty("status").GetString();
                        if (st == "ready" && doc.RootElement.GetProperty("key").GetString() is string key)
                        {
                            ForgetPending();
                            var err = Activate(key);
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => KeyArrived?.Invoke(key, err));
                            break;
                        }
                        if (st == "rejected")
                        {
                            ForgetPending();
                            var why = doc.RootElement.TryGetProperty("reason", out var rs) ? rs.GetString() : null;
                            Notice = $"주문 {o.Code}이 거절됐어요: {(string.IsNullOrWhiteSpace(why) ? "결제를 확인하지 못했어요" : why)}. 결제했다면 결제한 곳으로 문의하세요.";
                            RaiseChanged();
                            break;
                        }
                    }
                    catch (Exception) { /* offline: keep waiting */ }
                    var fresh = DateTime.UtcNow - o.Created < TimeSpan.FromMinutes(15);
                    await System.Threading.Tasks.Task.Delay(fresh ? 5000 : 60000);
                }
            }
            finally { _watching = 0; }
        });
    }

    /// Message to show once after a key was turned off by the seller (refund, sharing).
    public static string? Notice { get; private set; }

    /// At start: ask whether this key was revoked (offline = nothing changes), and for
    /// subscription keys fetch the renewed key a week before expiry.
    public static async System.Threading.Tasks.Task RenewIfNeededAsync()
    {
        if (!ServerConfigured) return;
        try
        {
            if (File.Exists(KeyFile))
            {
                var raw = File.ReadAllText(KeyFile).Trim();
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), 0, 8).ToLowerInvariant();
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{ServerUrl}/check?id={id}"));
                if (doc.RootElement.TryGetProperty("revoked", out var rv) && rv.ValueKind == JsonValueKind.True)
                {
                    Notice = "이 기기의 프리미엄 키가 판매자에 의해 해제됐어요(환불 또는 키 공유). 문의가 필요하면 결제한 곳으로 연락하세요.";
                    Deactivate();
                    return;
                }
            }
        }
        catch (Exception) { }

        var info = Info;
        if (info?.Sub == null || info.Expires is not DateTime e || e - DateTime.UtcNow > TimeSpan.FromDays(7)) return;
        try
        {
            var j = await Http.GetStringAsync($"{ServerUrl}/renew?sub={Uri.EscapeDataString(info.Sub)}");
            using var doc = JsonDocument.Parse(j);
            if (doc.RootElement.GetProperty("status").GetString() == "ready")
            {
                var key = doc.RootElement.GetProperty("key").GetString();
                var next = key == null ? null : Verify(key, out _);
                if (next?.Expires > e) Activate(key!);
            }
        }
        catch (Exception) { }
    }

    private static byte[] B64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
