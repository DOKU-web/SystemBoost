// Customer support: questions and refund requests to the seller, answered inside the app (server/license-worker,
// /support/*). Money never moves here: the seller decides a refund and pays it back.
// This PC keeps only the list of the tickets it sent (support.json next to the other settings, at most 20):
// id, token, subject, kind, status, last change, whether a reply is unread, how many messages were shown. The token
// is the only way to read a ticket again, so the list is never lost to a read error: a damaged file is set aside as
// support.json.bad-<time> (never saved over), a file that cannot be read right now is never written over, and every
// save re-reads the file under a named mutex and merges by id, because two copies of the app can run at once.
// The token never goes into a URL: GET /support/ticket carries it in the X-Ticket-Token header, reply and close in
// the JSON body. A refund request carries the registered key itself so the server can check it is the buyer's; the
// server keeps only the key's ID, and the key text is never written here.
// Replies are looked for with one batch call (POST /support/tickets/status: status, last change, message count and
// unread mark of up to 20 tickets; read-only, it leaves the unread mark alone), never one call per ticket, because
// the free Workers plan has 100k requests a day for all users together:
//   in the background at start and every 30 minutes, only for tickets waiting for the seller (status open = the
//   user wrote last) that changed in the last 14 days, and no call at all when there is none;
//   while the support page is shown and the main window is active, every 60 s and on opening when the last look is
//   that old, for the tickets not closed that changed in the last 30 days (the page runs that, see SupportPage);
//   when the user presses 새로 고침, for every ticket on the list (after a look already on its way).
// A whole ticket (GET /support/ticket) is read only when the user opens it, or when the batch says the open one
// changed. A 429 doubles every wait, up to 2 hours, and no look goes out before its Retry-After; the next answer
// that goes through resets it. An answer older than what this PC already has (a reply sent or the ticket closed
// meanwhile) changes nothing.
// Nothing here throws to the screen: every call answers with a message in Korean (Lang translates it on screen).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SystemBoost.App.Services;

/// A ticket kind or a refund reason. Ko is shown (and translated by Lang); a server entry brings its own English.
public sealed record SupportChoice(string Id, string Ko, string En = "")
{
    public string Label => Lang.Translating && En.Length > 0 ? En : Ko;
}

public sealed record SupportFaq(string Q, string A);

public sealed record SupportSeller(string Name = "", string Rep = "", string Address = "", string BizNo = "", string MailOrderNo = "",
                                   string Email = "", string Phone = "", string Hours = "")
{
    public bool Empty => new[] { Name, Rep, Address, BizNo, MailOrderNo, Email, Phone, Hours }.All(string.IsNullOrWhiteSpace);
}

/// Reachable false = the server did not answer; then the rest is the built-in fallback.
public sealed record SupportInfo(bool Reachable, bool Open, SupportSeller Seller, string Notice, int RefundDays, bool BuiltInFaq,
                                 IReadOnlyList<SupportFaq> Faq, IReadOnlyList<SupportChoice> Categories, IReadOnlyList<SupportChoice> RefundReasons);

public sealed record SupportMessage(bool FromSeller, DateTimeOffset At, string Text);

/// What the buyer may see of a refund request.
public sealed record SupportRefund(string Status, string Reason, string? RejectReason, string? Plan, DateTimeOffset? Issued, int? DaysSinceKey);

public sealed record SupportTicket(string Id, string Cat, string Subject, string Status, DateTimeOffset Updated,
                                   IReadOnlyList<SupportMessage> Messages, string? Contact, SupportRefund? Refund);

/// A ticket sent from this PC. Count = messages known so far, so a new reply can be told apart.
/// Shown = messages the user has seen on screen: the server's unread mark counts only above it (over its daily
/// budget the server may leave a read mark set). Gone = the server no longer has it (deleted, or kept past its period).
public sealed record SupportEntry(string Id, string Token, string Subject, string Cat, DateTimeOffset Updated,
                                  string Status = "open", bool Unread = false, int Count = 1, bool Gone = false, int Shown = 0)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool Closed => Gone || Status == "closed";
}

/// The app details a ticket may carry, only when the user ticks the box. Empty Gpu = not sent.
public sealed record SupportDiag(string App, string Os, string Gpu);

/// Key = the registered key itself, for a refund request only: sent so the server can check it, never kept here.
/// KeyId = its ID, sent instead only when the server could not check the key.
public sealed record SupportDraft(string Cat, string Subject, string Text, string? Contact = null, SupportDiag? Diag = null,
                                  string? Order = null, string? Key = null, string? KeyId = null, string? RefundReason = null);

public static class Support
{
    public const int MaxKept = 20, MaxSubject = 80, MaxText = 2000, MaxContact = 120, MaxMessages = 30;
    public const int MaxKey = 4000;   // the server's LIMITS.key
    private const int MaxDiag = 80;
    private static readonly TimeSpan BackgroundEvery = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PageEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(2), MaxRetryAfter = TimeSpan.FromHours(25);
    private static readonly TimeSpan WaitingWindow = TimeSpan.FromDays(14), PageWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan InfoKeep = TimeSpan.FromMinutes(10), InfoRetry = TimeSpan.FromMinutes(1);

    /// The privacy policy on the seller's server (GET /privacy). The Korean page is the policy; ?lang=en is a summary.
    public static string PrivacyUrl => License.ServerUrl + (Lang.Translating ? "/privacy?lang=en" : "/privacy");

    /// Which tickets a look covers: waiting for the seller (background), not closed (page), all (새로 고침).
    public enum Scope { Waiting, Page, All }

    public const string Offline = "서버에 연결하지 못했어요. 인터넷 연결을 확인해 주세요";
    private const string Busy = "요청이 많아요. 잠시 뒤 다시 해 주세요";
    private const string DayFull = "오늘 받을 수 있는 문의가 다 찼어요. 한국 시간 오전 9시에 다시 열려요.";

    private static readonly JsonSerializerOptions FileJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string ListFile => Path.Combine(Platform.DataDir, "support.json");
    private const string FileMutex = @"Local\SystemBoost.support.json";
    private static readonly object Gate = new();
    private static readonly HashSet<string> Forgotten = new();   // taken off the list here: another copy's file does not bring it back
    private static List<SupportEntry> _list = Read();

    /// Newest change first.
    public static IReadOnlyList<SupportEntry> Tickets { get { lock (Gate) return _list.OrderByDescending(t => t.Updated).ToList(); } }
    public static int Unread { get { lock (Gate) return _list.Count(t => t.Unread); } }
    public static SupportEntry? Find(string id) { lock (Gate) return _list.FirstOrDefault(t => t.Id == id); }

    /// The list or an unread mark changed. On the UI thread.
    public static event Action? Changed;
    /// A seller's reply came in; the argument is the ticket's subject. On the UI thread.
    public static event Action<string>? ReplyArrived;

    // ------------------------------------------------------------ built-in texts (offline, or nothing set on the server)
    public static readonly IReadOnlyList<SupportChoice> BuiltInCategories = new SupportChoice[]
    {
        new("pay", "결제"), new("refund", "환불"), new("bug", "오류 신고"), new("howto", "사용 방법"), new("idea", "제안"), new("other", "기타"),
    };

    public static readonly IReadOnlyList<SupportChoice> BuiltInReasons = new SupportChoice[]
    {
        new("change_mind", "단순 변심"), new("not_working", "작동하지 않음 · 표시나 광고와 다름"), new("double", "중복 결제"), new("other", "기타"),
    };

    // Only what the app itself says elsewhere: no promise the app does not make.
    private static readonly IReadOnlyList<SupportFaq> BuiltInFaq = new SupportFaq[]
    {
        new("라이선스 키는 어디에 등록하나요?", "왼쪽 '★ 플랜' 화면의 '라이선스 키' 칸에 붙여 넣고 '등록'을 누르세요."),
        new("결제했는데 키가 오지 않아요", "카드 결제는 결제가 확인되면 키가 자동으로 떠요. 계좌이체 · 직접 결제는 '결제했어요'를 누른 뒤 판매자가 확인하면 키가 떠요. 앱을 껐다 켜도 이어서 기다려요. 오래 걸리면 주문 코드를 적어 문의해 주세요."),
        new("카드 구독은 어떻게 해지하나요?", "결제 메일의 구독 관리 링크에서 해지할 수 있어요. 이 화면에 '구독 관리' 버튼이 보이면 거기서도 열 수 있어요."),
        new("환불은 어떻게 요청하나요?", "'환불 요청'에서 사유를 골라 보내면 판매자가 확인해요. 청약철회는 사용권 계약 제9조를 따르고, 환불이 끝나면 해당 키는 막혀요."),
        new("앱이 바꾼 설정을 원래대로 돌리려면?", "왼쪽 '↺ 백업 · 되돌리기'에서 '전체 되돌리기'를 누르세요. 앱을 지우기 전에도 눌러 주세요."),
    };

    public static readonly SupportInfo Fallback = new(false, true, new SupportSeller(), "", 7, true, BuiltInFaq, BuiltInCategories, BuiltInReasons);

    // ------------------------------------------------------------ local list
    private enum Disk { Missing, Ok, Bad, Locked }

    /// The file as it is now. Bad = not a list (damaged, half written); Locked = could not be read just now.
    private static (Disk state, List<SupportEntry> list) ReadDisk()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(ListFile)) return (Disk.Missing, new());
                var list = JsonSerializer.Deserialize<List<SupportEntry?>>(File.ReadAllText(ListFile), FileJson);
                if (list == null) return (Disk.Bad, new());
                return (Disk.Ok, list.OfType<SupportEntry>().Where(t => !string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Token)).ToList());
            }
            catch (JsonException) { return (Disk.Bad, new()); }
            catch (Exception) when (attempt < 4) { Thread.Sleep(50); }   // another copy writing, a virus scanner: a moment later
            catch (Exception) { return (Disk.Locked, new()); }
        }
    }

    /// Moves a damaged list aside for a look by hand; it is never saved over. False = it could not be moved.
    private static bool SetAside()
    {
        try
        {
            File.Move(ListFile, ListFile + ".bad-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture), false);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// Runs work while holding the mutex all copies of the app share for this file. Without one (a system that has
    /// no named mutex, or one stuck for 3 s) the work runs anyway: the merge still keeps what the file has.
    private static T Locked<T>(Func<T> work)
    {
        Mutex? mutex = null;
        var held = false;
        try
        {
            try
            {
                mutex = new Mutex(false, FileMutex);
                held = mutex.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException) { held = true; }   // a copy that ended while holding it: it is ours now
            catch (Exception) { }
            return work();
        }
        finally
        {
            if (held) try { mutex!.ReleaseMutex(); } catch (Exception) { }
            mutex?.Dispose();
        }
    }

    private static List<SupportEntry> Read()
    {
        try
        {
            return Locked(() =>
            {
                var (state, list) = ReadDisk();
                if (state == Disk.Bad) SetAside();   // starts empty; the next save writes a new file, the old one stays
                Trim(list);
                return list;   // Locked: empty for now; the next save or Reload merges the file in
            });
        }
        catch (Exception) { return new(); }
    }

    /// The entry that knows more: more messages, then the later change. Shown is kept from both.
    private static SupportEntry Pick(SupportEntry mine, SupportEntry other)
    {
        var later = other.Count != mine.Count ? other.Count > mine.Count : other.Updated > mine.Updated;
        var pick = later ? other : mine;
        var shown = Math.Max(mine.Shown, other.Shown);
        return pick.Shown == shown ? pick : pick with { Shown = shown };
    }

    /// This copy's list together with the file's, by id. Call inside Gate.
    private static List<SupportEntry> Merge(List<SupportEntry> mine, List<SupportEntry> disk)
    {
        var all = new Dictionary<string, SupportEntry>();
        foreach (var d in disk) if (!Forgotten.Contains(d.Id)) all[d.Id] = all.TryGetValue(d.Id, out var twin) ? Pick(twin, d) : d;
        foreach (var m in mine) all[m.Id] = all.TryGetValue(m.Id, out var d) ? Pick(m, d) : m;
        var list = all.Values.ToList();
        Trim(list);
        return list;
    }

    private static void Trim(List<SupportEntry> list)
    {
        while (list.Count > MaxKept)   // the oldest closed one goes first
            list.Remove(list.Where(t => t.Closed).OrderBy(t => t.Updated).FirstOrDefault() ?? list.OrderBy(t => t.Updated).First());
    }

    private static int _saveRetries;

    private static void Save()
    {
        var saved = true;
        try
        {
            saved = Locked(() =>
            {
                var (state, disk) = ReadDisk();
                if (state == Disk.Locked) return false;              // what is there is unknown: write nothing over it
                if (state == Disk.Bad && !SetAside()) return false;  // a damaged file is kept as it is
                string json;
                lock (Gate)
                {
                    _list = Merge(_list, disk);
                    json = JsonSerializer.Serialize(_list, FileJson);
                }
                var tmp = $"{ListFile}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, ListFile, true);
                }
                catch (Exception)
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    throw;
                }
                return true;
            });
        }
        catch (Exception) { saved = false; }   // tried again a few times; a read-only folder keeps the list until the app closes
        if (saved) _saveRetries = 0;
        else if (Interlocked.Increment(ref _saveRetries) <= 20)
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Save(), TaskScheduler.Default);   // the file was busy: again soon
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    /// Takes in what another copy of the app saved meanwhile. Writes nothing.
    public static void Reload()
    {
        try
        {
            var (state, disk) = Locked(ReadDisk);
            if (state != Disk.Ok) return;
            bool changed;
            lock (Gate)
            {
                var merged = Merge(_list, disk);
                changed = merged.Count != _list.Count || merged.Any(m => !_list.Contains(m));
                _list = merged;
            }
            if (changed) Dispatcher.UIThread.Post(() => Changed?.Invoke());
        }
        catch (Exception) { }
    }

    private static void Keep(SupportEntry entry)
    {
        lock (Gate)
        {
            _list.RemoveAll(t => t.Id == entry.Id);
            _list.Add(entry);
            Trim(_list);
        }
        Save();
    }

    /// Takes a ticket the server no longer has off this PC's list.
    public static void Forget(string id)
    {
        lock (Gate)
        {
            Forgotten.Add(id);
            _list.RemoveAll(t => t.Id == id);
        }
        Save();
    }

    // ------------------------------------------------------------ server calls
    /// Status 0 = no answer. RetryAfter and Date come from the answer's headers.
    private readonly record struct Answer(int Status, JsonElement Body, TimeSpan? RetryAfter = null, DateTimeOffset? Date = null);

    /// ticketToken: sent in the X-Ticket-Token header, never in the URL.
    private static async Task<Answer> Call(HttpMethod method, string path, object? body = null, bool account = false, string? ticketToken = null)
    {
        try
        {
            using var req = new HttpRequestMessage(method, License.ServerUrl + path);
            if (body != null || method == HttpMethod.Post)
            {   // every POST carries a JSON body with exactly this type: the server answers 415 otherwise
                req.Content = new StringContent(JsonSerializer.Serialize(body ?? new Dictionary<string, string>()), Encoding.UTF8);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            if (account && Account.Token is { } session) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
            if (ticketToken != null) req.Headers.TryAddWithoutValidation("X-Ticket-Token", ticketToken);
            using var r = await License.Http.SendAsync(req);
            var text = await r.Content.ReadAsStringAsync();
            JsonElement root = default;
            try { using var doc = JsonDocument.Parse(text); root = doc.RootElement.Clone(); }
            catch (JsonException) { }
            var retry = r.Headers.RetryAfter is { } ra ? ra.Delta ?? (ra.Date is { } at ? at - DateTimeOffset.UtcNow : null) : null;
            return new Answer((int)r.StatusCode, root, retry, r.Headers.Date);
        }
        catch (Exception) { return new Answer(0, default); }   // offline, timeout, anything: status 0
    }

    private static string Problem(int status, JsonElement body) => status switch
    {
        0 => Offline,
        429 => Str(body, "scope") == "day" ? DayFull : Busy,
        503 => "지금은 앱에서 문의를 받지 않아요. 판매 페이지에 있는 연락처로 문의해 주세요",
        404 when Str(body, "error") == "not found" => "서버에서 이 문의를 찾지 못했어요. 지워졌거나 보관 기간이 끝났어요",
        409 when Str(body, "error") == "full" => $"한 문의에는 글을 {MaxMessages}개까지 쓸 수 있어요. 새 문의를 남겨 주세요",
        409 => "닫은 지 30일이 지난 문의라 다시 열 수 없어요. 새 문의를 남겨 주세요",
        400 => Str(body, "error") switch
        {
            "cat" => "문의 종류를 골라 주세요",
            "subject" => $"제목은 1~{MaxSubject}자로 적어 주세요",
            "text" => $"내용은 1~{MaxText}자로 적어 주세요",
            "contact" => $"연락처는 {MaxContact}자까지 적을 수 있어요",
            "order" => "주문 코드 형식이 맞지 않아요 (SB-XXXX-XXXX)",
            "keyId" or "key" or "bad key" => "키 식별 번호가 맞지 않아요",
            "consent" => "보관에 동의해야 보낼 수 있어요",
            "refund" or "reason" => "환불 사유를 골라 주세요",
            "messages" or "full" => $"한 문의에는 글을 {MaxMessages}개까지 쓸 수 있어요. 새 문의를 남겨 주세요",
            _ => "보낼 수 없는 내용이 있어요. 내용을 확인해 주세요",
        },
        _ => "요청을 처리하지 못했어요. 잠시 뒤 다시 해 주세요",
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string Esc(string s) => Uri.EscapeDataString(s);
    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private static SupportInfo? _info;
    private static DateTime _infoAt;

    /// FAQ, seller details, kinds and reasons, asked at most every 10 minutes (every minute while it fails).
    /// Offline or on a bad answer: the built-in fallback.
    public static async Task<SupportInfo> InfoAsync()
    {
        if (_info is { } kept && DateTime.UtcNow - _infoAt < (kept.Reachable ? InfoKeep : InfoRetry)) return kept;
        var info = await FetchInfo();
        _info = info;
        _infoAt = DateTime.UtcNow;
        return info;
    }

    private static async Task<SupportInfo> FetchInfo()
    {
        var (status, r, _, _) = await Call(HttpMethod.Get, $"/support/info?lang={Lang.Code}");   // the default FAQ comes in that language
        if (status != 200 || r.ValueKind != JsonValueKind.Object) return Fallback;
        try
        {
            static List<SupportChoice> Choices(JsonElement r, string name, IReadOnlyList<SupportChoice> fallback)
            {
                var list = new List<SupportChoice>();
                if (r.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
                    foreach (var c in a.EnumerateArray())
                        if (Str(c, "id") is { Length: > 0 } id)
                            list.Add(new SupportChoice(id, Str(c, "ko") ?? fallback.FirstOrDefault(f => f.Id == id)?.Ko ?? id, Str(c, "en") ?? ""));
                return list.Count > 0 ? list : fallback.ToList();
            }
            var faq = new List<SupportFaq>();
            if (r.TryGetProperty("faq", out var fa) && fa.ValueKind == JsonValueKind.Array)
                foreach (var f in fa.EnumerateArray())
                    if (Str(f, "q") is { Length: > 0 } q && Str(f, "a") is { Length: > 0 } a) faq.Add(new SupportFaq(q, a));
            var s = Obj(r, "seller");
            var seller = new SupportSeller(Str(s, "name") ?? "", Str(s, "rep") ?? "", Str(s, "address") ?? "", Str(s, "bizNo") ?? "",
                                           Str(s, "mailOrderNo") ?? "", Str(s, "email") ?? "", Str(s, "phone") ?? "", Str(s, "hours") ?? "");
            var open = !(r.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.False);
            return new SupportInfo(true, open, seller, Str(r, "notice") ?? "", Int(r, "refundDays") ?? 7, faq.Count == 0,
                                   faq.Count > 0 ? faq : BuiltInFaq, Choices(r, "categories", BuiltInCategories), Choices(r, "refundReasons", BuiltInReasons));
        }
        catch (Exception) { return Fallback; }
    }

    private static SupportDiag? _diag;

    /// App version, operating system and GPU name, exactly as the box "앱 정보 함께 보내기 (선택)" would send them.
    public static async Task<SupportDiag> DiagAsync()
    {
        if (_diag != null) return _diag;
        var app = Updater.CurrentVersion.Split('+')[0];
        var os = $"{Platform.Name} {Environment.OSVersion.Version}";
        var gpu = "";
        try
        {
            var list = FrameGenEngine.ListGpusAsync();
            if (await Task.WhenAny(list, Task.Delay(TimeSpan.FromSeconds(8))) == list)
                gpu = string.Join(", ", (await list).Where(g => g.Value is not ("same" or "igpu")).Select(g => g.Name.Trim()).Where(n => n.Length > 0).Distinct());
        }
        catch (Exception) { }   // no engine, or it did not answer: the name is not sent
        return _diag = new SupportDiag(Cut(app, MaxDiag), Cut(os, MaxDiag), Cut(gpu, MaxDiag));
    }

    /// Sends a new ticket. Returns its id, or a message.
    public static async Task<(string? id, string? error)> CreateAsync(SupportDraft d)
    {
        var subject = d.Subject.Trim();
        var text = d.Text.Trim();
        var contact = (d.Contact ?? "").Trim();
        var order = (d.Order ?? "").Trim().ToUpperInvariant();
        if (d.Cat.Length == 0) return (null, "문의 종류를 골라 주세요");
        if (subject.Length is 0 or > MaxSubject) return (null, $"제목은 1~{MaxSubject}자로 적어 주세요");
        if (text.Length is 0 or > MaxText) return (null, $"내용은 1~{MaxText}자로 적어 주세요");
        if (contact.Length > MaxContact) return (null, $"연락처는 {MaxContact}자까지 적을 수 있어요");
        if (order.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(order, "^SB-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
            return (null, "주문 코드 형식이 맞지 않아요 (SB-XXXX-XXXX)");
        if (d.Cat == "refund" && order.Length == 0 && string.IsNullOrEmpty(d.Key))
            return (null, "이 PC에 키가 없으면 키 전체나 주문 코드를 적어 주세요. 둘 다 모르면 '문의하기'에 결제한 날, 금액, 입금자명을 적어 주세요.");
        if (d.Cat == "refund" && string.IsNullOrEmpty(d.RefundReason)) return (null, "환불 사유를 골라 주세요");

        var body = new Dictionary<string, object>
        {
            ["cat"] = d.Cat, ["subject"] = subject, ["text"] = text, ["consent"] = true, ["lang"] = Lang.Code,
        };
        if (contact.Length > 0) body["contact"] = contact;
        if (order.Length > 0) body["order"] = order;
        if (!string.IsNullOrEmpty(d.Key)) body["key"] = d.Key;   // checked by the server, which keeps only its ID
        if (d.Diag is { } diag)
        {
            var sent = new Dictionary<string, string> { ["app"] = diag.App, ["os"] = diag.Os };
            if (diag.Gpu.Length > 0) sent["gpu"] = diag.Gpu;
            body["diag"] = sent;
        }
        if (d.Cat == "refund") body["refund"] = new Dictionary<string, string> { ["reason"] = d.RefundReason! };

        var a = await Call(HttpMethod.Post, "/support/ticket", body, account: true);
        if (a.Status == 400 && Str(a.Body, "error") == "bad key" && body.Remove("key") && !string.IsNullOrEmpty(d.KeyId))
        {   // the server could not check this PC's key (damaged, or already blocked): the request names it by its ID
            // only, which the seller sees as not checked
            body["keyId"] = d.KeyId;
            a = await Call(HttpMethod.Post, "/support/ticket", body, account: true);
        }
        if (a.Status is 200 or 201 && Str(a.Body, "id") is { Length: > 0 } id && Str(a.Body, "token") is { Length: > 0 } token)
        {   // the server's clock (Date) marks the start, so its later answers are never taken for older ones
            Keep(new SupportEntry(id, token, subject, d.Cat, a.Date ?? DateTimeOffset.UtcNow, Shown: 1));
            return (id, null);
        }
        if (a.Status == 429 && Str(a.Body, "scope") != "day") _backoff++;
        return (null, Problem(a.Status, a.Body));
    }

    /// Reads a ticket. read: the user is looking at it, so its reply is no longer new.
    public static async Task<(SupportTicket? ticket, string? error)> GetAsync(string id, bool read = true)
    {
        if (Find(id) is not { } e) return (null, "이 PC에서 보낸 문의가 아니에요");
        var a = await Call(HttpMethod.Get, $"/support/ticket?id={Esc(id)}", ticketToken: e.Token);
        return Take(id, a, read);
    }

    public static async Task<(SupportTicket? ticket, string? error)> ReplyAsync(string id, string text)
    {
        text = text.Trim();
        if (text.Length is 0 or > MaxText) return (null, $"내용은 1~{MaxText}자로 적어 주세요");
        if (Find(id) is not { } e) return (null, "이 PC에서 보낸 문의가 아니에요");
        var a = await Call(HttpMethod.Post, "/support/ticket/reply", new Dictionary<string, string> { ["id"] = id, ["token"] = e.Token, ["text"] = text });
        return Take(id, a, read: true);
    }

    public static async Task<(SupportTicket? ticket, string? error)> CloseAsync(string id)
    {
        if (Find(id) is not { } e) return (null, "이 PC에서 보낸 문의가 아니에요");
        var a = await Call(HttpMethod.Post, "/support/ticket/close", new Dictionary<string, string> { ["id"] = id, ["token"] = e.Token });
        return Take(id, a, read: true);
    }

    /// Notes what the server said about a ticket on the local list.
    private static (SupportTicket?, string?) Take(string id, Answer a, bool read)
    {
        if (a.Status == 429 && Str(a.Body, "scope") != "day") _backoff++;
        if (a.Status == 404 && Str(a.Body, "error") == "not found")   // the server's own answer, not a 404 from on the way
        {
            if (Find(id) is { } gone) Keep(gone with { Gone = true, Unread = false });
            return (null, Problem(404, a.Body));
        }
        SupportTicket? t = null;
        try { if (a.Status == 200) t = Parse(a.Body); }
        catch (Exception) { t = null; }
        if (t == null) return (null, Problem(a.Status == 200 ? -1 : a.Status, a.Body));
        if (Find(id) is not { } e) return (t, null);
        // an answer that left before a newer one (a reply sent or the ticket closed meanwhile) must not undo it
        if (t.Messages.Count < e.Count || (t.Updated != default && t.Updated < e.Updated)) return (t, null);
        var fresh = t.Messages.Skip(Math.Max(e.Count, e.Shown)).Any(m => m.FromSeller);
        var next = e with
        {
            Subject = t.Subject.Length > 0 ? t.Subject : e.Subject, Status = t.Status, Updated = t.Updated == default ? e.Updated : t.Updated,
            Count = t.Messages.Count, Unread = !read && (e.Unread || fresh), Gone = false, Shown = read ? Math.Max(e.Shown, t.Messages.Count) : e.Shown,
        };
        if (next != e) Keep(next);   // most checks change nothing: no file write then
        if (fresh && !read) Dispatcher.UIThread.Post(() => ReplyArrived?.Invoke(e.Subject));
        return (t, null);
    }

    private static SupportTicket? Parse(JsonElement r)
    {
        var t = Obj(r, "ticket");
        if (t.ValueKind != JsonValueKind.Object) return null;
        var messages = new List<SupportMessage>();
        if (t.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object)
                    messages.Add(new SupportMessage(Str(m, "from") == "admin", Time(m, "at") ?? default, Str(m, "text") ?? ""));
        SupportRefund? refund = null;
        var rf = Obj(t, "refund");
        if (rf.ValueKind == JsonValueKind.Object)
        {
            var facts = Obj(rf, "facts");
            refund = new SupportRefund(Str(rf, "status") ?? "requested", Str(rf, "reason") ?? "", Str(rf, "rejectReason"),
                                       Str(facts, "plan"), Time(facts, "issued"), Int(facts, "daysSinceKey"));
        }
        return new SupportTicket(Str(t, "id") ?? "", Str(t, "cat") ?? "other", Str(t, "subject") ?? "", Str(t, "status") ?? "open",
                                 Time(t, "updated") ?? default, messages, Str(t, "contact"), refund);
    }

    private static Task<string?>? _look;      // the look on its way, if any
    private static DateTime _lastTry;         // when the last look went out
    private static DateTime _retryAt;         // no look before this (a 429's Retry-After)
    private static string? _retryWhy;
    private static int _backoff;              // 429s in a row; each one doubles the waits

    /// The wait before the next look: normal, doubled for each 429 in a row, at most 2 hours.
    public static TimeSpan Every(TimeSpan normal) => TimeSpan.FromTicks(Math.Min(MaxWait.Ticks, normal.Ticks << Math.Min(_backoff, 16)));

    /// A look may go out now: the last one is a page interval old and no Retry-After is running.
    public static bool MayLook => DateTime.UtcNow >= _retryAt && DateTime.UtcNow - _lastTry >= Every(PageEvery);

    private static List<SupportEntry> Due(Scope scope)
    {
        var now = DateTimeOffset.UtcNow;
        lock (Gate)
            return _list.Where(t => !t.Gone && scope switch
            {
                Scope.Waiting => t.Status == "open" && now - t.Updated <= WaitingWindow,
                Scope.Page => !t.Closed && now - t.Updated <= PageWindow,
                _ => true,
            }).Take(MaxKept).ToList();
    }

    /// One batch look at the tickets of a scope. reading: the ticket the user has on screen (no notice for it; the
    /// page reads it whole when it changed). No call when nothing is due. One look at a time: 새로 고침 waits for
    /// the one on its way and then looks itself; the others skip. Returns null, or a message.
    public static async Task<string?> RefreshAsync(Scope scope, string? reading = null)
    {
        while (_look is { IsCompleted: false } running)
        {
            if (scope != Scope.All) return null;
            await running;
        }
        var look = _look = Look(scope, reading);
        return await look;
    }

    private static async Task<string?> Look(Scope scope, string? reading)
    {
        if (DateTime.UtcNow < _retryAt) return scope == Scope.All ? _retryWhy ?? Busy : null;   // the server asked to wait
        var due = Due(scope);
        if (due.Count == 0) return null;
        _lastTry = DateTime.UtcNow;
        try
        {
            var body = new Dictionary<string, object>
            {
                ["tickets"] = due.Select(t => new Dictionary<string, string> { ["id"] = t.Id, ["token"] = t.Token }).ToList(),
            };
            var (status, r, retryAfter, _) = await Call(HttpMethod.Post, "/support/tickets/status", body);
            if (status == 429)
            {
                _backoff++;
                _retryWhy = Problem(429, r);
                if (retryAfter is { } wait && wait > TimeSpan.Zero) _retryAt = DateTime.UtcNow + (wait < MaxRetryAfter ? wait : MaxRetryAfter);
                return _retryWhy;
            }
            var map = Obj(r, "tickets");
            if (status != 200 || map.ValueKind != JsonValueKind.Object) return Problem(status == 200 ? -1 : status, r);
            _backoff = 0;
            foreach (var e in due)
            {
                if (!map.TryGetProperty(e.Id, out var s)) continue;   // left out: nothing known, nothing changes
                if (s.ValueKind == JsonValueKind.Null) { if (Find(e.Id) is { } gone) Keep(gone with { Gone = true, Unread = false }); continue; }
                if (s.ValueKind == JsonValueKind.Object) Note(e.Id, s, e.Id == reading);
            }
            return null;
        }
        catch (Exception) { return Problem(-1, default); }
    }

    /// One row of the batch answer. The server's unread mark says the user has not read the seller's last words;
    /// it counts only for messages this PC has not shown yet.
    private static void Note(string id, JsonElement s, bool reading)
    {
        if (Find(id) is not { } e) return;
        var count = Int(s, "n") ?? e.Count;
        var updated = Time(s, "updated");
        if (count < e.Count || updated < e.Updated) return;   // older than what this PC has (a reply sent or the ticket closed meanwhile)
        var unread = Bool(s, "unread") && count > e.Shown;
        var fresh = unread && count > e.Count && !reading;
        var next = e with
        {
            Status = Str(s, "status") ?? e.Status, Updated = updated ?? e.Updated, Count = count,
            Unread = !reading && unread, Gone = false,
        };
        if (next != e) Keep(next);
        if (fresh) Dispatcher.UIThread.Post(() => ReplyArrived?.Invoke(e.Subject));
    }

    private static DispatcherTimer? _timer;

    /// Once, after the agreement is accepted (on the UI thread). No call at all while no ticket waits for the seller.
    public static void Start()
    {
        if (_timer != null) return;
        var timer = _timer = new DispatcherTimer { Interval = Every(BackgroundEvery) };
        timer.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow - _lastTry > PageEvery) await RefreshAsync(Scope.Waiting);   // the open page may just have looked
            timer.Interval = Every(BackgroundEvery);
        };
        timer.Start();
        _ = RefreshAsync(Scope.Waiting);
    }

    /// A pre-signed link to the card subscription's own management page. Only for a key with a subscription:
    /// typed = a key typed into the refund form, else this PC's key.
    public static async Task<(string? url, string? error)> SubscriptionPortalAsync(TypedKey? typed = null)
    {
        string key;
        if (typed != null)
        {
            if (typed.Info.Sub == null) return (null, "입력한 키는 카드 구독 키가 아니에요");
            key = typed.Key;
        }
        else if (License.Info?.Sub == null || License.KeyText() is not { } own) return (null, "이 PC의 키는 카드 구독 키가 아니에요");
        else key = own;
        var (status, r, _, _) = await Call(HttpMethod.Post, "/support/subscription", new Dictionary<string, string> { ["key"] = key });
        if (status == 200 && Str(r, "portal") is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            return (link, null);
        return (null, status switch
        {
            0 => Offline,
            503 => "지금은 앱에서 링크를 받을 수 없어요. 결제 메일에 있는 구독 관리 링크를 써 주세요",
            404 => "이 키의 구독을 찾지 못했어요. 이미 해지됐거나 끝난 구독일 수 있어요. 결제 메일에 있는 구독 관리 링크를 써 주세요",
            429 => Problem(429, r),
            _ => "구독 관리 링크를 받지 못했어요. 잠시 뒤 다시 해 주세요",
        });
    }
}
