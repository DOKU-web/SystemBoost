// Free / Premium / Ultra / Biz.
// Two ways to own Premium (Ultra = a key whose plan is "ultra": Premium plus what UltraOnly lists;
// Biz = a key whose plan is "biz": Ultra plus what BizOnly lists, the work and streaming features on Windows):
//  1) Steam build: the "SystemBoost Premium" DLC (checked through Steamworks;
//     ISteamOwnership is the hook - wire Steamworks.NET here).
//  2) Direct downloads (installer / portable / macOS): a license key bought on
//     the web shop. Key = base64url(payload JSON) "." base64url(ECDSA P-256 signature).
//     Only the PUBLIC key is in the app; keys are signed on the shop's server.
// Nothing here talks to a payment provider; the seller's payment page is opened in the browser.
using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SystemBoost.App.Services;

public enum PremiumFeature
{
    AutoAndHighMultiplier,   // auto, x3, x4 (x2 stays free)
    UltraMultiplier,         // x6, x8
    QualityMode,             // "quality" preset
    DualGpu,                 // frame generation on another GPU
    CpuLimit,                // resource sharing: hard CPU cap
    PerGameProfiles,
    Overclock,
    StreamMode,              // Biz: streaming app first, notifications off while streaming
    WorkMode,                // Biz: video calls and chosen work apps first
    StartupApps,             // Biz: turn programs that start with Windows off and on
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
    private static string HowFile => Path.Combine(Platform.DataDir, "license.how");

    /// How the key got here: key (typed in), paid, referral, invite or reward.
    public static string How
    {
        get
        {
            try { return File.Exists(HowFile) ? File.ReadAllText(HowFile).Trim() : "key"; }
            catch (Exception) { return "key"; }
        }
    }

    public static LicenseInfo? Info { get { Load(); return _info; } }
    public static bool IsPremium
    {
        get
        {
            Load();
            if (Steam?.OwnsPremiumDlc() == true) return true;
            return KeyActive;
        }
    }
    public static bool IsUltra { get { Load(); return KeyActive && _info!.Plan is "ultra" or "biz"; } }
    public static bool IsBiz { get { Load(); return KeyActive && _info!.Plan == "biz"; } }
    private static bool KeyActive => _info != null && (_info.Expires == null || _info.Expires > DateTime.UtcNow);

    // Which plan opens a feature is decided here and the messages follow. Change the table in PremiumPage with it.
    private static bool UltraOnly(PremiumFeature f) =>
        f is PremiumFeature.UltraMultiplier or PremiumFeature.DualGpu or PremiumFeature.PerGameProfiles or PremiumFeature.Overclock;
    private static bool BizOnly(PremiumFeature f) =>
        f is PremiumFeature.StreamMode or PremiumFeature.WorkMode or PremiumFeature.StartupApps;
    public static bool Allowed(PremiumFeature f) => BizOnly(f) ? IsBiz : UltraOnly(f) ? IsUltra : IsPremium;
    public static string PlanOf(PremiumFeature f) => BizOnly(f) ? "산업" : UltraOnly(f) ? "울트라" : "프로";

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
    public static string? Activate(string key, string how = "key")
    {
        key = Canonical(key);   // the server's spelling, so the revocation check asks about the key that was issued
        var info = Verify(key, out var error);
        if (info == null) return error;
        File.WriteAllText(KeyFile, key);
        try { File.WriteAllText(HowFile, how); }
        catch (Exception) { }   // only the wording on the account page depends on it
        _info = info; _loaded = true;
        RaiseChanged();
        return null;
    }

    private static string DeviceFile => Path.Combine(Platform.DataDir, "referral.txt");

    // Random, and not the statistics install number: a code has to work with statistics off.
    public static string ReferralDevice()
    {
        try
        {
            if (File.Exists(DeviceFile) && File.ReadAllText(DeviceFile).Trim() is { Length: 32 } saved) return saved;
        }
        catch (Exception) { }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try { File.WriteAllText(DeviceFile, id); }
        catch (Exception) { }
        return id;
    }

    /// Trades a referral code for a short key. Returns null on success, else a message.
    public static async System.Threading.Tasks.Task<string?> RedeemReferralAsync(string code)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length == 0) return "추천 코드를 적어 주세요";
        if (IsPremium) return "유료 플랜을 쓰는 중이라 추천 코드를 쓸 수 없어요";   // a paid key is never replaced by a short one
        try
        {
            var body = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { code, device = ReferralDevice() }), Encoding.UTF8, "application/json");
            var r = await Http.PostAsync($"{ServerUrl}/referral", body);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (r.IsSuccessStatusCode && doc.RootElement.TryGetProperty("key", out var key)) return Activate(key.GetString() ?? "", "referral");
            return (doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null) switch
            {
                "unknown" => "없는 코드이거나 끝난 코드예요",
                "used up" => "이 코드는 사용 횟수가 다 찼어요",
                "already" => "이 기기는 이미 추천 코드를 썼어요",
                "busy" => "오늘은 더 받을 수 없어요. 내일 다시 해 주세요",
                "signup" => "회원의 초대 코드는 회원 가입할 때 입력해요. 왼쪽 '계정'에서 가입하세요",
                _ => "코드는 영문과 숫자 4~16자예요",
            };
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException or JsonException)
        {
            return "서버에 연결하지 못했어요. 인터넷 연결을 확인해 주세요";
        }
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
    public sealed record PendingOrder(string Code, string Plan, DateTime Created, string Url = "", string Method = "manual", string Status = "requested", bool Ticket = false);
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
            var r = await PostJsonAsync("/order/paid", new { code = o.Code, payer, msg = memo });
            if (r.StatusCode == System.Net.HttpStatusCode.BadRequest)   // a worker from before 2026-11-12 reads the code from the address only
            {
                var body = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { payer, msg = memo }), Encoding.UTF8, "application/json");
                r = await Http.PostAsync($"{ServerUrl}/order/paid?code={Uri.EscapeDataString(o.Code)}", body);
            }
            if (!r.IsSuccessStatusCode) return "알리지 못했어요. 잠시 뒤 다시 시도하세요.";
            SavePending(o with { Status = "paid" });
            return null;
        }
        catch (Exception) { return "결제 서버에 연결하지 못했어요. 인터넷 연결을 확인하세요."; }
    }

    /// Gets an order code + the payment link for a plan. error is a message for the user.
    public static async System.Threading.Tasks.Task<(string? code, string? url, string? error)> CreateOrderAsync(string plan, string method, bool ticket = false)
    {
        if (!ServerConfigured) return (null, null, "결제 준비 중이에요. 결제가 열리면 여기서 바로 살 수 있어요.");
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
                $"{ServerUrl}/order?plan={Uri.EscapeDataString(plan)}&method={Uri.EscapeDataString(method)}{(ticket ? "&ticket=1" : "")}");
            if (ticket && Account.Token is { } session) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session);
            var r = await Http.SendAsync(req);
            if ((int)r.StatusCode == 503) return (null, null, "이 플랜은 아직 결제 준비 중이에요.");
            if (ticket && (int)r.StatusCode == 409)
                return (null, null, (await r.Content.ReadAsStringAsync()).Contains("ticket link")
                    ? "이 플랜은 할인 결제 링크가 아직 없어요. 할인 티켓 체크를 끄면 정가로 결제할 수 있어요."
                    : "할인 티켓을 쓸 수 없어요. 기간이 끝났거나 이미 썼어요.");
            if (!r.IsSuccessStatusCode) return (null, null, "주문을 만들지 못했어요. 잠시 뒤 다시 시도하세요.");
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var code = doc.RootElement.GetProperty("code").GetString()!;
            var url = doc.RootElement.GetProperty("url").GetString();
            var serverMethod = doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() ?? "manual" : "manual";
            SavePending(new PendingOrder(code, plan, DateTime.UtcNow, url ?? "", serverMethod, Ticket: ticket));
            WatchPending();
            return (code, url, null);
        }
        catch (Exception) { return (null, null, "결제 서버에 연결하지 못했어요. 인터넷 연결을 확인하세요."); }
    }

    /// Raised on the UI thread when the key for the pending order arrived and was registered.
    /// Arguments: key, activation error (null = premium is on), every key of the order (a bundle has one per PC;
    /// the first is the one registered here).
    public static event Action<string, string?, string[]>? KeyArrived;
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
                        string text;
                        using (var r = await PostJsonAsync("/claim", new { session = o.Code }))
                            text = r.StatusCode == System.Net.HttpStatusCode.NotFound   // a worker from before 2026-11-12: address form only
                                ? await Http.GetStringAsync($"{ServerUrl}/claim?session={Uri.EscapeDataString(o.Code)}")
                                : await r.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(text);
                        var st = doc.RootElement.GetProperty("status").GetString();
                        if (st == "ready" && doc.RootElement.GetProperty("key").GetString() is string key)
                        {
                            var keys = new System.Collections.Generic.List<string>();
                            if (doc.RootElement.TryGetProperty("keys", out var ks) && ks.ValueKind == JsonValueKind.Array)
                                foreach (var k in ks.EnumerateArray()) if (k.GetString() is { Length: > 0 } one) keys.Add(one);
                            if (!keys.Contains(key)) keys.Insert(0, key);
                            ForgetPending();
                            var err = Activate(key, "paid");
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => KeyArrived?.Invoke(key, err, keys.ToArray()));
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
        string? current = null;
        try
        {
            if (File.Exists(KeyFile))
            {
                current = Canonical(File.ReadAllText(KeyFile));
                foreach (var id in KeyIds(current))   // the key as issued, then the same key with its twin signature
                {
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{ServerUrl}/check?id={id}"));
                    if (doc.RootElement.TryGetProperty("revoked", out var rv) && rv.ValueKind == JsonValueKind.True)
                    {
                        Notice = "이 기기의 라이선스 키가 판매자에 의해 해제됐어요(환불 또는 키 공유). 문의가 필요하면 결제한 곳으로 연락하세요.";
                        Deactivate();
                        return;
                    }
                }
            }
        }
        catch (Exception) { }

        var info = Info;
        if (info?.Sub == null || current == null || info.Expires is not DateTime e || e - DateTime.UtcNow > TimeSpan.FromDays(7)) return;
        try
        {
            // the server hands a subscription's next key only to whoever shows a key of that subscription
            string j;
            using (var r = await PostJsonAsync("/renew", new { sub = info.Sub, key = current }))
                j = r.StatusCode == System.Net.HttpStatusCode.NotFound   // a worker from before 2026-11-12 renews by the number alone
                    ? await Http.GetStringAsync($"{ServerUrl}/renew?sub={Uri.EscapeDataString(info.Sub)}")
                    : await r.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(j);
            if (doc.RootElement.GetProperty("status").GetString() == "ready")
            {
                var key = doc.RootElement.GetProperty("key").GetString();
                var next = key == null ? null : Verify(key, out _);
                if (next?.Expires > e) Activate(key!, How);
            }
        }
        catch (Exception) { }
    }

    // An order code claims its key and the key in hand renews a subscription, so both travel in a POST body,
    // which addresses (and request logs) do not carry. The fallbacks above are for a license worker from before
    // 2026-11-12, which knows only the address forms; drop them once that worker is replaced.
    private static System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> PostJsonAsync(string path, object body) =>
        Http.PostAsync($"{ServerUrl}{path}", new System.Net.Http.StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    private static byte[] B64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
    private static string B64UrlEncode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The revocation list (/check) and the server's key registry name a key by the first 16 hex of SHA-256 of
    // its text as the server wrote it. One signed key can be written in other ways that verify just as well:
    // base64 with padding, spaces or stray low bits, and an ECDSA signature (r, s) is as valid as (r, n - s).
    // So a key is kept in the server's spelling (canonical base64url, no padding), and the revocation check
    // asks about both signatures. Keys the server issued are already in that spelling: their ids do not change.
    private static readonly BigInteger P256Order =
        BigInteger.Parse("00FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", NumberStyles.HexNumber);

    internal static string Canonical(string key)
    {
        key = key.Trim();
        var parts = key.Split('.');
        if (parts.Length != 2) return key;
        try { return B64UrlEncode(B64Url(parts[0])) + "." + B64UrlEncode(B64Url(parts[1])); }
        catch (FormatException) { return key; }
    }

    internal static string KeyId(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8).ToLowerInvariant();

    /// The key's id, then the id of the same key with the other valid signature (r, n - s).
    internal static string[] KeyIds(string key)
    {
        key = Canonical(key);
        var id = KeyId(key);
        var parts = key.Split('.');
        if (parts.Length != 2) return new[] { id };
        byte[] sig;
        try { sig = B64Url(parts[1]); }
        catch (FormatException) { return new[] { id }; }
        if (sig.Length != 64) return new[] { id };
        var s = new BigInteger(sig.AsSpan(32), isUnsigned: true, isBigEndian: true);
        if (s.IsZero || s >= P256Order) return new[] { id };
        var t = (P256Order - s).ToByteArray(isUnsigned: true, isBigEndian: true);
        var twin = new byte[64];
        Array.Copy(sig, twin, 32);
        Array.Copy(t, 0, twin, 64 - t.Length, t.Length);
        return new[] { id, KeyId(parts[0] + "." + B64UrlEncode(twin)) };
    }
}
