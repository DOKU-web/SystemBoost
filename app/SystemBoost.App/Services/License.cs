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
using System.Linq;
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

public sealed record LicenseInfo(string Plan, string Email, DateTime? Expires, string Source, string? Sub = null, DateTime? Issued = null);

/// A key typed by hand: the key in the server's spelling, its id ("lic:" + Id on the server) and what it says.
public sealed record TypedKey(string Key, string Id, LicenseInfo Info);

public interface ISteamOwnership { bool OwnsPremiumDlc(); }

public static class License
{
    // License server (server/license-worker).
    public const string ServerUrl = "https://systemboost-license.euning97.workers.dev";
    internal static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
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
        // Read also when it has run out (Info is then the key that was here; IsPremium and the rest check the end date):
        // a card subscription key renews from there, and support shows which key this PC had.
        try { if (File.Exists(KeyFile)) _info = Verify(File.ReadAllText(KeyFile).Trim(), out _, allowExpired: true); }
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

    /// A key the user typed or pasted (the refund form), with its id. Checked by its signature and read also when it has
    /// run out; not registered on this PC. Null with a message when it is not a key of this shop (empty text: no message).
    internal static TypedKey? Inspect(string? text, out string error)
    {
        var key = Canonical(System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", ""));
        if (key.Length == 0) { error = ""; return null; }
        LicenseInfo? info;
        try { info = Verify(key, out error, allowExpired: true); }
        catch (Exception) { error = "올바른 키가 아니에요"; return null; }   // a signed payload that is not ours
        return info == null ? null : new TypedKey(key, IdOf(key), info);
    }

    private static LicenseInfo? Verify(string key, out string error, bool allowExpired = false)
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
        if (!allowExpired && exp != null && exp < DateTime.UtcNow)
        {
            error = r.TryGetProperty("sub", out var s0) && s0.ValueKind == JsonValueKind.String
                ? "기간이 끝난 카드 구독 키예요. 구독이 이어지고 있다면 '등록'을 누를 때 새 키를 받아 와요"
                : "기간이 끝난 키예요. '★ 플랜'에서 새로 구매하세요";
            return null;
        }
        DateTime? issued = r.TryGetProperty("issued", out var i) && i.ValueKind == JsonValueKind.String && DateTime.TryParse(i.GetString(),
                               System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
        return new LicenseInfo(r.GetProperty("plan").GetString() ?? "premium", r.TryGetProperty("email", out var m) ? m.GetString() ?? "" : "", exp, "key",
                               r.TryGetProperty("sub", out var sb) ? sb.GetString() : null, issued);
    }

    /// The key registered on this PC as it is stored, for the server calls that need the signed key itself
    /// (the subscription portal). Null without one.
    internal static string? KeyText()
    {
        try { return File.Exists(KeyFile) && File.ReadAllText(KeyFile).Trim() is { Length: > 0 } raw ? raw : null; }
        catch (Exception) { return null; }
    }

    /// How the server names this PC's key ("lic:" + id): the first 16 hex of SHA-256 of the key text in the server's spelling.
    /// Also for a key that has run out. Null without a key.
    public static string? KeyId => KeyText() is { } raw ? IdOf(Canonical(raw)) : null;

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

    /// Status "paid" = the buyer said they paid and the seller is checking. PaidAt: when "결제했어요" was pressed (the
    /// seller usually answers soon after, so polling is quick again for a while). Extends: the end of the key this PC
    /// held, when the server noted the order as its extension (기간 연장).
    public sealed record PendingOrder(string Code, string Plan, DateTime Created, string Url = "", string Method = "manual", string Status = "requested",
                                      bool Ticket = false, DateTime? PaidAt = null, DateTime? Extends = null);
    /// An order the app no longer polls (cancelled here, or past OrderWait): asked again when the plan page opens, so a
    /// late approval still reaches this PC.
    public sealed record StoppedOrder(string Code, string Plan, DateTime Stopped, DateTime Checked);

    // How long an order is waited for: as long as the seller can still approve it (the server keeps orders 90 days).
    public static readonly TimeSpan OrderWait = TimeSpan.FromDays(90);
    private static readonly TimeSpan StoppedKeep = TimeSpan.FromDays(40);   // an approval's key is claimable 30 days
    private const int MaxOrders = 10;
    private static string OldPendingFile => Path.Combine(Platform.DataDir, "pending-order.json");   // up to 0.8.11: one order
    private static string OrdersFile => Path.Combine(Platform.DataDir, "pending-orders.json");
    private static string StoppedFile => Path.Combine(Platform.DataDir, "stopped-orders.json");
    private static readonly object OrdersLock = new();
    private static System.Collections.Generic.List<PendingOrder>? _orders;

    private static System.Collections.Generic.List<T> ReadList<T>(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<System.Collections.Generic.List<T>>(File.ReadAllText(file)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }
    private static void WriteList<T>(string file, System.Collections.Generic.List<T> list)
    {
        try { if (list.Count == 0) File.Delete(file); else File.WriteAllText(file, JsonSerializer.Serialize(list)); }
        catch (Exception) { }
    }

    // Every order still waiting for its key, oldest first. The caller holds OrdersLock.
    private static System.Collections.Generic.List<PendingOrder> Orders()
    {
        if (_orders == null)
        {
            _orders = ReadList<PendingOrder>(OrdersFile);
            try
            {   // the one order an older version kept
                if (File.Exists(OldPendingFile))
                {
                    if (JsonSerializer.Deserialize<PendingOrder>(File.ReadAllText(OldPendingFile)) is { Code.Length: > 0 } old && _orders.All(o => o.Code != old.Code))
                        _orders.Add(old);
                    File.Delete(OldPendingFile);
                    WriteList(OrdersFile, _orders);
                }
            }
            catch (Exception) { }
        }
        // past the seller's 90 days: no longer polled, still asked once in a while from the plan page
        foreach (var o in _orders.Where(o => DateTime.UtcNow - o.Created > OrderWait).ToList())
        {
            _orders.Remove(o);
            Stop(o);
            if (o.Status == "paid") Notice = $"주문 {o.Code}은 90일이 지나 더 기다리지 않아요. 결제했는데 키를 받지 못했다면 고객센터 '문의하기'로 주문 코드를 보내 주세요.";
            WriteList(OrdersFile, _orders);
        }
        return _orders;
    }
    private static void Save() => WriteList(OrdersFile, _orders ?? new());

    /// Every order waiting for its key, newest first.
    public static PendingOrder[] PendingOrders { get { lock (OrdersLock) return Orders().OrderByDescending(o => o.Created).ToArray(); } }
    /// The newest order waiting for its key (the one the plan page shows), or null.
    public static PendingOrder? Pending => PendingOrders.FirstOrDefault();
    /// The newest order that the buyer already reported as paid and that is still waiting.
    public static PendingOrder? PaidPending => PendingOrders.FirstOrDefault(o => o.Status == "paid");

    private static void AddPending(PendingOrder o)
    {
        lock (OrdersLock)
        {
            var list = Orders();
            list.RemoveAll(x => x.Code == o.Code);
            list.Add(o);
            // a few at most: unpaid ones go first (they are kept in the stopped list), an order reported paid is never dropped
            while (list.Count > MaxOrders && list.FirstOrDefault(x => x.Status != "paid" && x.Code != o.Code) is { } drop) { list.Remove(drop); Stop(drop); }
            Save();
        }
        WatchPending();
    }
    private static void UpdatePending(string code, Func<PendingOrder, PendingOrder> change)
    {
        lock (OrdersLock)
        {
            var list = Orders();
            var i = list.FindIndex(x => x.Code == code);
            if (i >= 0) { list[i] = change(list[i]); Save(); }
        }
    }
    /// Stops waiting for an order: its key arrived, it was rejected, refunded or deleted (keep: false), or the buyer
    /// cancelled it here (keep: true, so a late approval still reaches this PC when the plan page opens).
    public static void ForgetPending(string code, bool keep = true)
    {
        lock (OrdersLock)
        {
            var list = Orders();
            var o = list.FirstOrDefault(x => x.Code == code);
            if (o != null) { list.Remove(o); Save(); if (keep) Stop(o); }
            if (!keep) DropStopped(code);
        }
    }

    private static void Stop(PendingOrder o)
    {
        var s = ReadList<StoppedOrder>(StoppedFile);
        s.RemoveAll(x => x.Code == o.Code || DateTime.UtcNow - x.Stopped > StoppedKeep);
        s.Add(new StoppedOrder(o.Code, o.Plan, DateTime.UtcNow, DateTime.MinValue));
        WriteList(StoppedFile, s.TakeLast(20).ToList());
    }
    private static void DropStopped(string code)
    {
        var s = ReadList<StoppedOrder>(StoppedFile);
        if (s.RemoveAll(x => x.Code == code) > 0) WriteList(StoppedFile, s);
    }

    /// Manual payments: tell the seller "I paid", with the payer name / memo used for the payment.
    public static async System.Threading.Tasks.Task<string?> MarkPaidAsync(string payer, string memo, string? code = null)
    {
        var o = PendingOrders.FirstOrDefault(x => code == null ? x.Method is not ("card" or "auto") : x.Code == code);
        if (o == null) return "주문이 없어요";
        try
        {
            var r = await PostJsonAsync("/order/paid", new { code = o.Code, payer, msg = memo });
            if (r.StatusCode == System.Net.HttpStatusCode.BadRequest)   // a worker from before 2026-11-12 reads the code from the address only
            {
                var body = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { payer, msg = memo }), Encoding.UTF8, "application/json");
                r = await Http.PostAsync($"{ServerUrl}/order/paid?code={Uri.EscapeDataString(o.Code)}", body);
            }
            if ((int)r.StatusCode == 429)
                return (await r.Content.ReadAsStringAsync()).Contains("too many changes")
                    ? "입금자명과 메모는 더 바꿀 수 없어요. 고치려면 고객센터 '문의하기'로 주문 코드를 보내 주세요."
                    : "요청이 많아요. 잠시 뒤 다시 시도하세요.";
            if (!r.IsSuccessStatusCode) return "알리지 못했어요. 잠시 뒤 다시 시도하세요.";
            UpdatePending(o.Code, x => x with { Status = "paid", PaidAt = x.PaidAt ?? DateTime.UtcNow });
            return null;
        }
        catch (Exception) { return "결제 서버에 연결하지 못했어요. 인터넷 연결을 확인하세요."; }
    }

    // ---------------------------------------------------------- the key this PC holds, and what a new one would replace
    private static int Rank(LicenseInfo i) => i.Plan switch { "biz" => 3, "ultra" => 2, _ => 1 };
    private static int RankOfPlan(string plan) => plan.StartsWith("biz_") ? 3 : plan.StartsWith("ultra_") ? 2 : 1;
    internal static string PlanName(string plan) => plan switch { "biz" => "산업", "ultra" => "울트라", _ => "프로" };
    /// "프로 키(2027-01-31까지)" / "프로 키(기간 없음)".
    internal static string Describe(LicenseInfo i) =>
        i.Expires is DateTime e ? $"{PlanName(i.Plan)} 키({e.ToLocalTime():yyyy-MM-dd}까지)" : $"{PlanName(i.Plan)} 키(기간 없음)";

    /// A key that would replace this PC's key and lose something by it: a lower plan, an earlier end, a card
    /// subscription key replaced by another key (its billing goes on), or one key of a bundle (meant for another PC).
    public sealed record Replacement(LicenseInfo Current, LicenseInfo Next, bool Bundle);

    /// Null when registering `key` here loses nothing: no key here or one that ran out, the same key, the same
    /// subscription's next key, or a key at least as high that runs at least as long.
    public static Replacement? WouldReplace(string key, bool bundle = false)
    {
        var cur = Info;
        if (cur == null || !KeyActive || KeyText() is not { } raw) return null;
        key = Canonical(key);
        if (key == Canonical(raw)) return null;
        LicenseInfo? next;
        try { next = Verify(key, out _, allowExpired: true); }
        catch (Exception) { return null; }
        if (next == null) return null;
        if (cur.Sub != null && next.Sub == cur.Sub) return null;
        bool lower = Rank(next) < Rank(cur);
        bool shorter = cur.Expires is DateTime ce && next.Expires is DateTime ne ? ne < ce : next.Expires != null && cur.Expires == null;
        bool subGoesOn = cur.Sub != null && next.Sub != cur.Sub;
        return bundle || lower || shorter || subGoesOn ? new Replacement(cur, next, bundle) : null;
    }

    /// Asked before registering a key by hand over one that would be lost (sentences the language file knows one by one).
    public static string AskText(Replacement r) => string.Join(" ", new[]
    {
        $"이 PC의 {Describe(r.Current)}를 {Describe(r.Next)}로 바꿔요.",
        r.Bundle ? "묶음 키는 PC마다 하나씩 쓰는 키예요." : "",
        r.Current.Sub != null ? SubscriptionGoesOn : "",
        "지금 키를 다시 쓰려면 먼저 '이 PC의 키'에서 복사해 두세요.",
    }.Where(s => s.Length > 0));

    /// Shown with a key that arrived and was NOT registered here because it would replace a key worth keeping.
    public static string KeptText(Replacement r) => string.Join(" ", new[]
    {
        $"이 PC에는 지금 쓰는 {Describe(r.Current)}를 그대로 뒀어요.",
        r.Bundle ? "묶음 키는 PC마다 하나씩 등록하세요." : "",
        $"새 {Describe(r.Next)}를 이 PC에 쓰려면 '이 PC에 등록'을 누르세요. 지금 키 대신 쓰여요.",
        r.Current.Sub != null ? SubscriptionGoesOn : "",
    }.Where(s => s.Length > 0));

    /// Said wherever a card subscription key is taken off this PC: the subscription is billed until it is cancelled, and
    /// the app's cancel button ('고객센터 → 구독 관리') needs the subscription key on this PC.
    public const string SubscriptionGoesOn = "카드 구독은 키를 해제하거나 바꿔도 해지되지 않고 계속 결제돼요. 그만 쓰려면 먼저 '고객센터 → 구독 관리'에서 해지하세요. 결제 메일의 구독 관리 링크로도 해지할 수 있어요.";

    /// The order code a key arrived for in this session (KeyDialog shows it: the buyer needs it to ask about the key).
    private static readonly System.Collections.Generic.Dictionary<string, string> ArrivedFor = new();
    public static string? OrderOf(string key) { lock (ArrivedFor) return ArrivedFor.TryGetValue(Canonical(key), out var c) ? c : null; }

    // A key for an order: registered here unless that would lose the key this PC has; then the popup asks.
    private static void Deliver(string code, string key, System.Collections.Generic.List<string> keys)
    {
        lock (ArrivedFor) ArrivedFor[Canonical(key)] = code;
        var lose = WouldReplace(key, keys.Count > 1);
        var err = lose == null ? Activate(key, "paid") : KeptText(lose);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => KeyArrived?.Invoke(key, err, keys.ToArray()));
    }

    /// What typing a key into the plan page does. Error: a message, nothing changed. Confirm: a question (the key would
    /// replace one worth keeping); ask, then call again with confirmed. Both null: registered.
    public sealed record TypedResult(string? Error, string? Confirm = null);

    public static async System.Threading.Tasks.Task<TypedResult> RegisterTypedAsync(string text, bool confirmed)
    {
        if (NormalizeOrderCode(text) != null) return new("주문 코드는 아래 '주문 코드로 키 받기' 칸에 넣어 주세요.");
        // a key has one dot; a key wrapped over lines (mail) is still one key, several keys pasted at once are not
        var one = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", "").Trim('.');
        if (one.Count(c => c == '.') > 1) return new("키는 한 번에 하나만 넣어요. 묶음의 다른 키는 다른 PC에 하나씩 등록하세요.");
        var typed = Inspect(one, out var error);
        if (typed == null) return new(error.Length > 0 ? error : "키를 붙여 넣어 주세요");
        var key = typed.Key;
        if (typed.Info.Expires is DateTime end && end < DateTime.UtcNow)
        {
            if (typed.Info.Sub == null) return new("기간이 끝난 키예요. '★ 플랜'에서 새로 구매하세요");
            // a card subscription key that ran out (an old copy, a reinstall): the subscription may have renewed since
            var next = await NextSubscriptionKeyAsync(typed.Info.Sub, key);
            if (next == null) return new("기간이 끝난 카드 구독 키인데 새 키를 받지 못했어요. 구독이 해지됐거나 끝났을 수 있어요. 인터넷 연결도 확인해 주세요. 결제가 이어지고 있다면 고객센터 '문의하기'로 문의해 주세요.");
            key = next;
        }
        if (!confirmed && WouldReplace(key) is { } lose) return new(null, AskText(lose));
        return new(Activate(key));
    }

    /// The subscription's current key for the holder of one of its keys (also an expired one), or null.
    private static async System.Threading.Tasks.Task<string?> NextSubscriptionKeyAsync(string sub, string held)
    {
        try
        {
            string j;
            using (var r = await PostJsonAsync("/renew", new { sub, key = held }))
                j = r.StatusCode == System.Net.HttpStatusCode.NotFound   // a worker from before 2026-11-12 renews by the number alone
                    ? await Http.GetStringAsync($"{ServerUrl}/renew?sub={Uri.EscapeDataString(sub)}")
                    : await r.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(j);
            if (doc.RootElement.GetProperty("status").GetString() != "ready") { lock (EndedSubs) EndedSubs.Add(sub); return null; }
            var key = doc.RootElement.GetProperty("key").GetString();
            var next = key == null ? null : Verify(key, out _);
            return next?.Sub == sub ? key : null;
        }
        catch (Exception) { return null; }
    }

    // ---------------------------------------------------------- card subscription: one subscription, its plan changed
    // Subscriptions the server called ended (cancelled, refunded) in this session: a new checkout is fine for those.
    private static readonly System.Collections.Generic.HashSet<string> EndedSubs = new();

    /// This PC holds a card subscription key whose subscription may still be billed: paying again would start a second
    /// subscription next to it, so a plan change goes through ChangeSubscriptionAsync instead. Also a key that ran out
    /// lately (the next payment may be late, or its key not fetched yet) until the server says the subscription ended.
    public static bool SubscriptionActive
    {
        get
        {
            if (Info is not { Sub: { } sub } info) return false;
            if (info.Expires is DateTime end && DateTime.UtcNow - end > TimeSpan.FromDays(45)) return false;
            lock (EndedSubs) return !EndedSubs.Contains(sub);
        }
    }

    public const string ChangeInPortal = "지금은 앱에서 플랜을 바꿀 수 없어요. '고객센터 → 구독 관리'나 결제 메일의 구독 관리 링크에서 플랜을 바꿔 주세요. 새로 결제하면 구독이 두 개가 돼요.";

    /// Switches this PC's card subscription to `plan` (tier and period) at the card payment service. Null when it was
    /// asked (the new key follows by itself within minutes), else a message.
    public static async System.Threading.Tasks.Task<string?> ChangeSubscriptionAsync(string plan)
    {
        if (KeyText() is not { } raw || Info?.Sub is not { } sub) return "이 PC의 키는 카드 구독 키가 아니에요";
        try
        {
            using var r = await PostJsonAsync("/subscription/change", new { key = Canonical(raw), plan });
            if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return ChangeInPortal;   // a worker without this route
            if (r.IsSuccessStatusCode) { FollowChange(); return null; }
            string? error = null;
            try { using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null; }
            catch (JsonException) { }
            if (error == "inactive") { lock (EndedSubs) EndedSubs.Add(sub); RaiseChanged(); }
            return error switch
            {
                "same plan" => "지금 구독과 같은 플랜이에요.",
                "inactive" => "이 카드 구독은 이미 해지됐거나 끝났어요. 새로 결제할 수 있어요.",
                "plan closed" => "이 플랜은 지금 카드로 팔지 않아요. " + ChangeInPortal,
                "busy" => "요청이 많아요. 잠시 뒤 다시 시도하세요.",
                "key" => "이 PC의 키로 구독을 확인하지 못했어요. 고객센터 '문의하기'로 문의해 주세요.",
                "unavailable" => ChangeInPortal,
                _ => "플랜을 바꾸지 못했어요. 잠시 뒤 다시 시도하세요. 계속 안 되면 '고객센터 → 구독 관리'에서 바꿔 주세요.",
            };
        }
        catch (Exception) { return "결제 서버에 연결하지 못했어요. 인터넷 연결을 확인하세요."; }
    }

    // The payment service reports the change by webhook, then the server holds the new key: fetch it soon.
    private static void FollowChange()
    {
        var before = KeyText();
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            foreach (var wait in new[] { 15, 45, 120, 420, 1200 })
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(wait));
                await RenewIfNeededAsync(force: true);
                if (KeyText() != before) return;
            }
        });
    }

    // ---------------------------------------------------------- orders
    // 기간 연장: a bank-transfer order for one PC carries the key this PC holds when it still runs and is of the same or a
    // lower plan; the server then counts the new period from that key's end (servers before this ignore it).
    private static string? KeyToExtend(string plan, string method)
    {
        if (method != "manual" || plan.Contains("_x") || Info is not { Expires: not null } cur || !KeyActive) return null;
        if (Rank(cur) > RankOfPlan(plan)) return null;
        return KeyText() is { } raw ? Canonical(raw) : null;
    }
    /// The end of this PC's key when an order for `plan` paid by `method` would continue from it (기간 연장), else null.
    public static DateTime? ExtendsFrom(string plan, string method) => KeyToExtend(plan, method) != null ? Info?.Expires : null;

    /// Gets an order code + the payment link for a plan. error is a message for the user. An order that is already
    /// waiting stays in the list (PendingOrders): a new order never replaces it.
    public static async System.Threading.Tasks.Task<(string? code, string? url, string? error)> CreateOrderAsync(string plan, string method, bool ticket = false)
    {
        if (!ServerConfigured) return (null, null, "결제 준비 중이에요. 결제가 열리면 여기서 바로 살 수 있어요.");
        if (method == "card" && SubscriptionActive) return (null, null, "카드 구독을 쓰는 중이에요. 새로 결제하지 말고 지금 구독의 플랜을 바꿔 주세요.");
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
                $"{ServerUrl}/order?plan={Uri.EscapeDataString(plan)}&method={Uri.EscapeDataString(method)}{(ticket ? "&ticket=1" : "")}");
            if (ticket && Account.Token is { } session) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session);
            if (KeyToExtend(plan, method) is { } held)   // the key is a secret: in the body, never in the address
                req.Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { key = held }), Encoding.UTF8, "application/json");
            var r = await Http.SendAsync(req);
            if ((int)r.StatusCode == 503) return (null, null, "이 플랜은 아직 결제 준비 중이에요.");
            if ((int)r.StatusCode == 429) return (null, null, "주문이 많아요. 잠시 뒤 다시 시도하세요.");
            if (ticket && (int)r.StatusCode == 409)
                return (null, null, (await r.Content.ReadAsStringAsync()).Contains("ticket link")
                    ? "이 플랜은 할인 결제 링크가 아직 없어요. 할인 티켓 체크를 끄면 정가로 결제할 수 있어요."
                    : "할인 티켓을 쓸 수 없어요. 기간이 끝났거나 이미 썼어요.");
            if (!r.IsSuccessStatusCode) return (null, null, "주문을 만들지 못했어요. 잠시 뒤 다시 시도하세요.");
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var code = doc.RootElement.GetProperty("code").GetString()!;
            var url = doc.RootElement.GetProperty("url").GetString();
            var serverMethod = doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() ?? "manual" : "manual";
            DateTime? extends = doc.RootElement.TryGetProperty("extends", out var x) && x.ValueKind == JsonValueKind.String &&
                                DateTime.TryParse(x.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var xe) ? xe : null;
            AddPending(new PendingOrder(code, plan, DateTime.UtcNow, url ?? "", serverMethod, Ticket: ticket, Extends: extends));
            return (code, url, null);
        }
        catch (Exception) { return (null, null, "결제 서버에 연결하지 못했어요. 인터넷 연결을 확인하세요."); }
    }

    /// "sb abcd-efgh", "SB－ABCD－EFGH", "SBABCDEFGH" -> "SB-ABCD-EFGH"; null when the text is not an order code.
    public static string? NormalizeOrderCode(string? text)
    {
        var s = (text ?? "").Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s\-‐‑‒–—―−_.]", "");
        if (!System.Text.RegularExpressions.Regex.IsMatch(s, "^SB[A-HJ-NP-Z2-9]{8}$")) return null;
        return "SB-" + s.Substring(2, 4) + "-" + s.Substring(6, 4);
    }

    /// The plan page's "주문 코드로 키 받기": asks the server about an order code. A key that is ready is delivered like a
    /// watched order's (popup; registered here unless that would lose this PC's key); an order still waiting is
    /// watched from now on. Returns a message for the page (null: the key popup follows).
    public static async System.Threading.Tasks.Task<string?> ClaimOrderAsync(string text)
    {
        if (NormalizeOrderCode(text) is not { } code)
            return (text ?? "").Contains('.') ? "라이선스 키는 위 '라이선스 키' 칸에 넣어 주세요." : "주문 코드 형식이 맞지 않아요 (SB-XXXX-XXXX)";
        if (!ServerConfigured) return "결제 준비 중이에요.";
        var (st, note) = await PollAsync(code);
        var mine = PendingOrders.FirstOrDefault(o => o.Code == code);
        switch (st)
        {
            case Poll.Ready: return null;
            case Poll.Offline: return note;
            case Poll.Rejected: case Poll.Refunded:
                ForgetPending(code, keep: false);
                return note;
            case Poll.Gone:
                if (mine == null || DateTime.UtcNow - mine.Created > TimeSpan.FromMinutes(30)) ForgetPending(code, keep: false);
                return $"주문 {code}을 찾지 못했어요. 코드를 확인해 주세요. 주문한 지 90일이 지났거나 판매자가 지운 주문일 수 있어요.";
            case Poll.Issued:
                return $"주문 {code}의 키는 이미 발급됐지만 지금 받을 수 없어요. 잠시 뒤 다시 시도하고, 계속 이러면 고객센터 '문의하기'로 주문 코드를 보내 주세요.";
            default:
                if (mine == null) AddPending(new PendingOrder(code, "", DateTime.UtcNow, Status: st == Poll.WaitingPaid ? "paid" : "requested"));
                return st == Poll.WaitingPaid
                    ? $"주문 {code}은 판매자가 결제를 확인하는 중이에요. 확인되면 키가 자동으로 떠요."
                    : $"주문 {code}은 아직 결제 확인 전이에요. 결제했다면 판매자가 확인할 때까지 기다려요. 확인되면 키가 자동으로 떠요.";
        }
    }

    /// Raised on the UI thread when the key for an order arrived. Arguments: key, a note (null = premium is on with it;
    /// otherwise why not: the activation error, or the key this PC kept), every key of the order (a bundle has one per
    /// PC; the first is the one for this PC).
    public static event Action<string, string?, string[]>? KeyArrived;
    private static int _watching;

    // Waiting / WaitingPaid: no key yet. Ready: the key was delivered (popup). Rejected, Refunded: no key will come.
    // Gone: the server has no such order (deleted by the seller, past its 90 days, or a typo). Issued: approved, but
    // its key can no longer be claimed (claimable 30 days), or not yet (the two records arrive a moment apart).
    private enum Poll { Waiting, WaitingPaid, Ready, Rejected, Refunded, Gone, Issued, Offline }

    // One /claim for an order code. A key is delivered here; the text says why no key will come (or the error).
    private static async System.Threading.Tasks.Task<(Poll, string?)> PollAsync(string code)
    {
        string text;
        try
        {
            using (var r = await PostJsonAsync("/claim", new { session = code }))
                text = r.StatusCode == System.Net.HttpStatusCode.NotFound   // a worker from before 2026-11-12: address form only
                    ? await Http.GetStringAsync($"{ServerUrl}/claim?session={Uri.EscapeDataString(code)}")
                    : await r.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
        }
        catch (Exception) { return (Poll.Offline, "서버에 연결하지 못했어요. 인터넷 연결을 확인해 주세요"); }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var st = doc.RootElement.GetProperty("status").GetString();
            if (st == "ready" && doc.RootElement.GetProperty("key").GetString() is string key)
            {
                var keys = new System.Collections.Generic.List<string>();
                if (doc.RootElement.TryGetProperty("keys", out var ks) && ks.ValueKind == JsonValueKind.Array)
                    foreach (var k in ks.EnumerateArray()) if (k.GetString() is { Length: > 0 } one) keys.Add(one);
                if (!keys.Contains(key)) keys.Insert(0, key);
                ForgetPending(code, keep: false);
                Deliver(code, key, keys);
                return (Poll.Ready, null);
            }
            if (st == "rejected")
            {
                var why = doc.RootElement.TryGetProperty("reason", out var rs) ? rs.GetString() : null;
                return (Poll.Rejected, $"주문 {code}이 거절됐어요: {(string.IsNullOrWhiteSpace(why) ? "결제를 확인하지 못했어요" : why)}. 결제했다면 고객센터 '문의하기'로 주문 코드를 보내 주세요.");
            }
            var order = doc.RootElement.TryGetProperty("order", out var os) && os.ValueKind == JsonValueKind.String ? os.GetString() : null;
            return order switch
            {
                // a refund closed the order (support refund done, or a card refund): no key will come for it
                "refunded" => (Poll.Refunded, $"환불된 주문이에요 ({code}). 이 주문의 키는 더 기다리지 않아요."),
                "" => (Poll.Gone, $"주문 {code}을 서버에서 찾지 못해 더 기다리지 않아요. 판매자가 지운 주문일 수 있어요. 결제했다면 고객센터 '문의하기'로 주문 코드를 보내 주세요."),
                "approved" => (Poll.Issued, $"주문 {code}의 키는 발급됐지만 앱에서 받을 수 있는 기간이 지났어요. 키를 받지 못했다면 고객센터 '문의하기'로 주문 코드를 보내 주세요."),
                "paid" => (Poll.WaitingPaid, null),
                _ => (Poll.Waiting, null),   // requested; null from a worker that does not say
            };
        }
        catch (Exception) { return (Poll.Offline, "서버의 답을 읽지 못했어요. 잠시 뒤 다시 시도하세요"); }
    }

    // How often an order is asked about: often while an answer is likely, then less and less (each ask is two KV
    // reads on the server): card checkouts every 10 s for 10 minutes, then every minute for the first hour,
    // every 15 minutes for the first day, hourly after that until OrderWait. Counted from "결제했어요" once pressed.
    private static TimeSpan PollEvery(PendingOrder o)
    {
        var since = DateTime.UtcNow - (o.PaidAt is DateTime p && p > o.Created ? p : o.Created);
        if (o.Method is "card" or "auto" && since < TimeSpan.FromMinutes(10)) return TimeSpan.FromSeconds(10);
        if (since < TimeSpan.FromHours(1)) return TimeSpan.FromMinutes(1);
        if (since < TimeSpan.FromDays(1)) return TimeSpan.FromMinutes(15);
        return TimeSpan.FromHours(1);
    }

    /// Polls every waiting order while the app runs, each on its own schedule (PollEvery), until its key arrives or
    /// it is rejected, refunded, deleted or 90 days old. Survives restarts (pending-orders.json).
    public static void WatchPending()
    {
        if (System.Threading.Interlocked.Exchange(ref _watching, 1) == 1) return;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var last = new System.Collections.Generic.Dictionary<string, DateTime>();       // when each order was asked
            var issued = new System.Collections.Generic.Dictionary<string, DateTime>();     // first seen approved without its key
            try
            {
                while (PendingOrders is { Length: > 0 } list)
                {
                    foreach (var o in list)
                    {
                        var every = PollEvery(o);   // from now: "결제했어요" shortens it at once
                        if (last.TryGetValue(o.Code, out var at) && DateTime.UtcNow - at < every) continue;
                        var (st, note) = await PollAsync(o.Code);
                        // offline: again within a minute
                        last[o.Code] = st == Poll.Offline && every > TimeSpan.FromMinutes(1) ? DateTime.UtcNow - every + TimeSpan.FromMinutes(1) : DateTime.UtcNow;
                        if (st == Poll.Issued) issued.TryAdd(o.Code, DateTime.UtcNow);
                        bool end = st switch
                        {
                            Poll.Rejected or Poll.Refunded => true,
                            // a brand-new order may not be visible everywhere yet (KV takes up to a minute)
                            Poll.Gone => DateTime.UtcNow - o.Created > TimeSpan.FromMinutes(30),
                            // the key and the approval are written a moment apart: give the key ten minutes to show
                            Poll.Issued => DateTime.UtcNow - issued[o.Code] > TimeSpan.FromMinutes(10),
                            _ => false,
                        };
                        if (!end) continue;
                        ForgetPending(o.Code, keep: false);
                        Notice = note;
                        RaiseChanged();
                    }
                    await System.Threading.Tasks.Task.Delay(5000);   // a new order (or "결제했어요") is picked up within seconds
                }
            }
            finally { _watching = 0; }
            if (PendingOrders.Length > 0) WatchPending();   // added while this loop was ending
        });
    }

    /// The plan page asks about orders it stopped waiting for (cancelled here, or past 90 days), at most hourly each:
    /// the seller may approve late, and an approved order's key can be claimed for 30 days. A key that is there pops
    /// up like a watched order's; an order that is gone, rejected or refunded leaves the list without a word.
    public static async System.Threading.Tasks.Task CheckStoppedAsync()
    {
        if (!ServerConfigured) return;
        System.Collections.Generic.List<StoppedOrder> due;
        lock (OrdersLock)
            due = ReadList<StoppedOrder>(StoppedFile).Where(x => DateTime.UtcNow - x.Stopped <= StoppedKeep && DateTime.UtcNow - x.Checked > TimeSpan.FromHours(1)).ToList();
        foreach (var s in due)
        {
            var (st, _) = await PollAsync(s.Code);
            if (st == Poll.Offline) return;
            lock (OrdersLock)
            {
                var now = ReadList<StoppedOrder>(StoppedFile);
                if (st is Poll.Rejected or Poll.Refunded or Poll.Gone or Poll.Issued) now.RemoveAll(x => x.Code == s.Code);
                else if (now.FindIndex(x => x.Code == s.Code) is var i and >= 0) now[i] = now[i] with { Checked = DateTime.UtcNow };
                WriteList(StoppedFile, now);
            }
        }
    }

    /// Message to show once after a key was turned off by the seller (refund, sharing), an order was rejected and so on.
    public static string? Notice { get; private set; }

    // ---------------------------------------------------------- revocation check and subscription renewal
    private static DateTime _lastRevocationCheck = DateTime.MinValue;
    private static DateTime _lastInactive = DateTime.MinValue;
    private static int _checking, _checksStarted;
    private static DateTime _lastNudge = DateTime.MinValue;

    /// While the app runs: the revocation check and the subscription renewal at start, then every 6 hours, after the
    /// PC wakes from sleep and when the network comes back. A card subscription's next key exists only once its payment
    /// went through (about 3 days before the key in hand runs out), so an app that stays open or asleep through that
    /// must ask again; asking only at start left the paid features off until a restart.
    public static void StartChecks()
    {
        if (System.Threading.Interlocked.Exchange(ref _checksStarted, 1) == 1) return;
        _ = RenewIfNeededAsync();
        try { System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) => { if (e.IsAvailable) Nudge(); }; }
        catch (Exception) { }
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            DateTime tick = DateTime.UtcNow, ran = DateTime.UtcNow;
            while (true)
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(5));
                var now = DateTime.UtcNow;
                bool woke = now - tick > TimeSpan.FromMinutes(15);   // five minutes took much longer: the PC slept
                tick = now;
                if (!woke && now - ran < TimeSpan.FromHours(6)) continue;
                if (woke) await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(30));   // the network comes up after waking
                ran = DateTime.UtcNow;
                try { await RenewIfNeededAsync(); } catch (Exception) { }
            }
        });
    }
    private static void Nudge()
    {
        if (DateTime.UtcNow - _lastNudge < TimeSpan.FromMinutes(2)) return;
        _lastNudge = DateTime.UtcNow;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10));
            try { await RenewIfNeededAsync(); } catch (Exception) { }
        });
    }

    /// Asks whether this key was revoked (offline = nothing changes; at most every 20 hours), and for a subscription
    /// key fetches the next key from a week before its end until 45 days after it (also when it has run out: the app
    /// may have been closed through the renewal). force: ask for the next key now (after a plan change).
    public static async System.Threading.Tasks.Task RenewIfNeededAsync(bool force = false)
    {
        if (!ServerConfigured) return;
        if (System.Threading.Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            string? current = KeyText() is { } raw ? Canonical(raw) : null;
            if (current != null && DateTime.UtcNow - _lastRevocationCheck > TimeSpan.FromHours(20))
            {
                try
                {
                    foreach (var id in KeyIds(current))   // the key as issued, then the same key with its twin signature
                    {
                        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{ServerUrl}/check?id={id}"));
                        if (doc.RootElement.TryGetProperty("revoked", out var rv) && rv.ValueKind == JsonValueKind.True)
                        {
                            Notice = Info?.Sub != null
                                ? "이 기기의 라이선스 키가 판매자에 의해 해제됐어요(환불 또는 키 공유). 카드 구독이 해지됐는지는 결제 메일의 구독 관리 링크에서 확인할 수 있어요. 문의는 고객센터 '문의하기'로 보내 주세요."
                                : "이 기기의 라이선스 키가 판매자에 의해 해제됐어요(환불 또는 키 공유). 문의는 고객센터 '문의하기'로 보내 주세요.";
                            Deactivate();
                            return;
                        }
                    }
                    _lastRevocationCheck = DateTime.UtcNow;
                }
                catch (Exception) { }
            }

            var info = Info;
            if (info?.Sub == null || current == null || info.Expires is not DateTime e) return;
            var left = e - DateTime.UtcNow;
            if (!force && (left > TimeSpan.FromDays(7) || left < -TimeSpan.FromDays(45))) return;
            // ran out and the server said "no next key" lately (cancelled, or a payment still being retried): once a day
            if (!force && left < TimeSpan.Zero && DateTime.UtcNow - _lastInactive < TimeSpan.FromHours(24)) return;
            try
            {
                // the server hands a subscription's next key only to whoever shows a key of that subscription
                string j;
                using (var r = await PostJsonAsync("/renew", new { sub = info.Sub, key = current }))
                    j = r.StatusCode == System.Net.HttpStatusCode.NotFound   // a worker from before 2026-11-12 renews by the number alone
                        ? await Http.GetStringAsync($"{ServerUrl}/renew?sub={Uri.EscapeDataString(info.Sub)}")
                        : await r.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(j);
                if (doc.RootElement.GetProperty("status").GetString() != "ready")
                {   // cancelled, ended or revoked: a new checkout is fine from here (a retried payment brings a key later)
                    _lastInactive = DateTime.UtcNow;
                    lock (EndedSubs) EndedSubs.Add(info.Sub);
                    return;
                }
                var key = doc.RootElement.GetProperty("key").GetString();
                var next = key == null ? null : Verify(key, out _);
                // the subscription's newest key: a renewal runs longer, a plan change was issued later
                if (next != null && next.Sub == info.Sub && Canonical(key!) != current && (next.Expires > e || (next.Issued ?? DateTime.MinValue) > (info.Issued ?? DateTime.MinValue)))
                {
                    lock (EndedSubs) EndedSubs.Remove(info.Sub);
                    Activate(key!, How);
                }
            }
            catch (Exception) { }
        }
        finally { _checking = 0; }
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

    internal static string IdOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8).ToLowerInvariant();

    /// The key's id, then the id of the same key with the other valid signature (r, n - s).
    internal static string[] KeyIds(string key)
    {
        key = Canonical(key);
        var id = IdOf(key);
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
        return new[] { id, IdOf(parts[0] + "." + B64UrlEncode(twin)) };
    }
}
