// Member account. Optional: nothing in the app needs one. The app keeps a session token, never the
// password; AccountFile keeps it on disk, sealed for the Windows user. Messages come back in Korean;
// Lang translates them where they reach the screen. Passwords, codes and tokens are never logged or kept
// anywhere else: a 2단계 인증 pending token lives only in the account page while its code step is open.
// The server's actions and replies: accounts v2 contract (server/license-worker/src/accounts.js).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Saved = SystemBoost.App.Services.AccountSession;

namespace SystemBoost.App.Services;

public sealed record AccountProfile(string Id, DateTimeOffset Created, string? Code, int Invites, int RewardDays, int EarnedDays,
                                    int RewardCap, int RewardEach, int InviteDays, int InviteTicket, int? TicketPercent, DateTimeOffset? TicketExpires,
                                    AccountExtras? Extras = null);

/// "계정 복구 요청": what the member gives to prove the account is theirs. Empty fields are left out.
public sealed record RecoveryRequest(string Login, string Contact, string Order, string PaidOn, string Amount, string CardOrName, string Message);

public static class Account
{
    public const int MinPassword = AccountRules.MinPassword, MaxPassword = AccountRules.MaxPassword;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static string RecordFile => Path.Combine(Platform.DataDir, "account.json");
    private static Saved? _saved = Read();

    public static string? Id => _saved?.Id;
    public static string? Token => _saved?.Token;
    public static bool SignedIn => _saved != null;
    public static event Action? Changed;

    private static Saved? Read()
    {
        try { return AccountFile.Load(RecordFile); }   // damaged, or sealed for another Windows user or PC: signed out
        catch (Exception) { }   // no data folder: the same
        return null;
    }

    private static void Keep(Saved? saved)
    {
        _saved = saved;
        try { AccountFile.Save(RecordFile, saved); }   // false: the session then lasts until the app closes
        catch (Exception) { }   // no data folder: the same
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    private static void KeepSession(LoginReply r)
    {
        if (r.Id != null && r.Token != null) Keep(new Saved(r.Id, r.Token));
    }

    /// One server call. ok: the reply on success; word: the server's error word; error: its text for the screen.
    /// wrong: what "wrong" means for this call (AccountRules.Message).
    private static async Task<(JsonElement? ok, string? word, string? error)> Call(string path, object? body, string? wrong = null)
    {
        try
        {
            using var req = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, $"{License.ServerUrl}/{path}");
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            var sent = _saved;
            if (sent != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sent.Token);
            using var r = await Http.SendAsync(req);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = doc.RootElement.Clone();
            if (r.IsSuccessStatusCode) return (root, null, null);
            var code = AccountReplies.Text(root, "error");
            // Ended here or on the server: either way it no longer works. Only the session this went out with:
            // the member may have signed in again while it was on its way.
            if (code == "session" && sent != null && _saved == sent) Keep(null);
            return (null, code, AccountRules.Message(code, wrong));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, null, "서버에 연결하지 못했어요. 인터넷 연결을 확인해 주세요");
        }
    }

    private static async Task<(JsonElement? ok, string? error)> Send(string action, object? body = null, string? wrong = null)
    {
        var (ok, _, error) = await Call("account/" + action, body, wrong);
        return (ok, error);
    }

    private static string? Text(JsonElement e, string name) => AccountReplies.Text(e, name);

    private const string WrongPassword = "비밀번호가 맞지 않아요";
    private const string WrongCode = "코드가 맞지 않아요. 다시 확인해 주세요";

    // ------------------------------------------------------------ this device (sent at login for the device list)
    private static string DeviceFile => Path.Combine(Platform.DataDir, "device-id.txt");
    private static byte[]? _installId;

    /// A random 32-byte install id, kept beside the other app files so it outlasts logging out. Not personal
    /// data: it is made here, says nothing about the PC, and only its sha256 leaves the app.
    private static byte[] InstallId()
    {
        if (_installId != null) return _installId;
        try
        {
            if (File.Exists(DeviceFile) && AccountRules.ParseInstallId(File.ReadAllText(DeviceFile)) is { } kept) return _installId = kept;
        }
        catch (Exception) { }
        var fresh = RandomNumberGenerator.GetBytes(32);
        try { File.WriteAllText(DeviceFile, Convert.ToHexString(fresh).ToLowerInvariant()); }
        catch (Exception) { }   // not written: a new one next start, so this PC shows as a new device then
        return _installId = fresh;
    }

    private static object Device() => new
    {
        name = AccountRules.DeviceName(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), Environment.OSVersion.Version),
        key = AccountRules.DeviceKey(InstallId()),
    };

    // ------------------------------------------------------------ signed out
    /// What the server offers. An older server (404) or no connection: none of the v2 parts.
    public static async Task<AccountFeatures> FeaturesAsync()
    {
        var (r, _) = await Send("features");
        return r is { } ok ? AccountReplies.Features(ok) : AccountFeatures.None;
    }

    /// A key that came with a code. A paid key is never replaced by a short one.
    private static string? Take(JsonElement r, string how)
    {
        var key = Text(r, "key");
        return key == null || License.IsPremium ? null : License.Activate(key, how);
    }

    private const string PaidInvite = "유료 플랜을 쓰는 중이라 초대 코드를 쓸 수 없어요. 코드를 지우고 가입해 주세요";

    /// 아이디 sign-up (servers without mail). Returns (error, recovery code to show once).
    public static async Task<(string? error, string? recovery)> SignUpAsync(string id, string password, string invite)
    {
        invite = invite.Trim();
        if (invite.Length > 0 && License.IsPremium) return (PaidInvite, null);
        var (r, error) = await Send("signup", new { id, password, invite, device = License.ReferralDevice(), agree = true, age14 = true, consent = AccountRules.ConsentVersion });
        if (r is not { } ok) return (error, null);
        Keep(new Saved(Text(ok, "id")!, Text(ok, "token")!));
        return (Take(ok, "invite"), Text(ok, "recovery"));
    }

    /// Email sign-up: the account exists once the link in the mail is pressed; the member then logs in here.
    /// The reply is the same whether or not the address is already a member's.
    public static async Task<string?> SignUpEmailAsync(string email, string password, string invite)
    {
        invite = invite.Trim();
        if (invite.Length > 0 && License.IsPremium) return PaidInvite;
        var (_, error) = await Send("signup_email", new
        {
            email = AccountRules.CleanEmail(email), password, agree = true, age14 = true, consent = AccountRules.ConsentVersion,
            lang = Lang.Code, invite = invite.Length > 0 ? invite : null,
        });
        return error;
    }

    /// login: 아이디 or email. A reply that NeedsCode goes on with LogIn2Async; otherwise the member is in.
    public static async Task<(string? error, LoginReply? reply)> LogInAsync(string login, string password)
    {
        login = login.Trim();
        object body = AccountRules.IsEmail(login)
            ? new { email = AccountRules.CleanEmail(login), password, device = Device() }
            : new { id = login, password, device = Device() };
        var (r, error) = await Send("login", body, "아이디(또는 이메일)나 비밀번호가 맞지 않아요");
        if (r is not { } ok) return (error, null);
        var reply = AccountReplies.Login(ok);
        if (reply == null) return (AccountRules.Message(null), null);
        if (!reply.NeedsCode) KeepSession(reply);
        return (null, reply);
    }

    /// The code step: an authenticator code, or (backup) a recovery code. again: the pending login is over
    /// (time or tries): back to the password.
    public static async Task<(string? error, LoginReply? reply, bool again)> LogIn2Async(string pending, string code, bool backup)
    {
        object body = backup ? new { pending, backup = code } : new { pending, code };
        var (r, word, error) = await Call("account/login2", body, WrongCode);
        if (r is not { } ok)
            return (word == "expired" ? "시간이 지났어요. 처음부터 다시 로그인해 주세요" : error, null, word is "expired" or "locked");
        var reply = AccountReplies.Login(ok);
        if (reply?.Token == null) return (AccountRules.Message(null), null, true);
        KeepSession(reply);
        return (null, reply, false);
    }

    /// Always the same answer when it went out: nobody learns whether the address is a member's.
    public static async Task<string?> ForgotAsync(string email) =>
        (await Send("forgot", new { email = AccountRules.CleanEmail(email), lang = Lang.Code })).error;

    /// "키 다시 받기": the key(s) bought with this email go to it. The same answer whatever the address.
    public static async Task<string?> ResendKeysAsync(string email) =>
        (await Call("keys/resend", new { email = AccountRules.CleanEmail(email), lang = Lang.Code })).error;

    public static async Task<string?> RecoveryRequestAsync(RecoveryRequest q)
    {
        static string? Opt(string s) => s.Trim() is { Length: > 0 } t ? t : null;
        var (card4, depositor) = AccountRules.CardOrDepositor(q.CardOrName);
        var login = q.Login.Trim();
        return (await Send("recovery_request", new
        {
            login = AccountRules.IsEmail(login) ? AccountRules.CleanEmail(login) : login.ToLowerInvariant(),
            contact = Opt(AccountRules.CleanEmail(q.Contact)), order = Opt(q.Order), paidOn = Opt(q.PaidOn), amount = Opt(q.Amount),
            card4, depositor, message = Opt(q.Message), lang = Lang.Code,
        })).error;
    }

    public static async Task LogOutAsync()
    {
        var token = _saved?.Token;
        Keep(null);   // off this PC first: closing the app or a dead connection must not leave the session on disk
        if (token != null) await RevokeAsync(token);
    }

    /// Ends the session on the server too, so a copy of the token stops working. Best effort: offline or on
    /// server trouble the session simply runs out on the server (90 days at most).
    private static async Task RevokeAsync(string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{License.ServerUrl}/account/logout")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var r = await Http.SendAsync(req);
        }
        catch (Exception) { }
    }

    public static async Task<(string? error, string? recovery)> RecoverAsync(string id, string recovery, string next)
    {
        var (r, error) = await Send("recover", new { id, recovery, next });
        if (r is not { } ok) return (error, null);
        Keep(new Saved(Text(ok, "id")!, Text(ok, "token")!));
        return (null, Text(ok, "recovery"));
    }

    // ------------------------------------------------------------ signed in
    public static async Task<(string? error, string? recovery)> ChangePasswordAsync(string password, string next)
    {
        var (r, error) = await Send("password", new { password, next });
        if (r is not { } ok) return (error, null);
        Keep(new Saved(Text(ok, "id")!, Text(ok, "token")!));
        return (null, Text(ok, "recovery"));
    }

    public static async Task<string?> DeleteAsync(string password)
    {
        var (r, error) = await Send("delete", new { password });
        if (r != null) Keep(null);
        return error;
    }

    public static async Task<(AccountProfile? profile, string? error)> ProfileAsync()
    {
        var (r, error) = await Send("me");
        if (r is not { } ok) return (null, error);
        int N(string name) => ok.GetProperty(name).GetInt32();
        var ticket = ok.TryGetProperty("ticket", out var t) && t.ValueKind == JsonValueKind.Object ? t : (JsonElement?)null;
        return (new AccountProfile(Text(ok, "id")!, DateTimeOffset.FromUnixTimeMilliseconds(ok.GetProperty("created").GetInt64()), Text(ok, "code"),
                                   N("invites"), N("rewardDays"), N("earnedDays"), N("rewardCap"), N("rewardEach"), N("inviteDays"), N("inviteTicket"),
                                   ticket?.GetProperty("percent").GetInt32(),
                                   ticket is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v.GetProperty("expires").GetInt64()) : null,
                                   AccountReplies.Extras(ok)), null);
    }

    public static async Task<string?> NewInviteCodeAsync() => (await Send("invite", new { })).error;

    /// Trades the days earned for a key. They stay on the server while another key is running.
    public static async Task<string?> ClaimRewardAsync()
    {
        if (License.IsPremium) return "지금 쓰는 키가 끝난 뒤에 받을 수 있어요. 보상은 그대로 남아 있어요";
        var (r, error) = await Send("reward", new { });
        return r is { } ok ? Take(ok, "reward") : error;
    }

    /// 아이디 accounts add an email; the link in the mail attaches it.
    public static async Task<string?> AddEmailAsync(string email, string password) =>
        (await Send("add_email", new { email = AccountRules.CleanEmail(email), password, lang = Lang.Code }, WrongPassword)).error;

    public static async Task<(List<AccountDevice>? devices, string? error)> DevicesAsync()
    {
        var (r, error) = await Send("devices");
        return r is { } ok ? (AccountReplies.Devices(ok), null) : (null, error);
    }

    public static async Task<string?> LogOutDeviceAsync(string device) => (await Send("logout_device", new { device })).error;

    /// Every session of the account ends, this one too.
    public static async Task<string?> LogOutAllAsync()
    {
        var (r, error) = await Send("logout_all", new { keepThis = false });
        if (r != null) Keep(null);
        return error;
    }

    public static async Task<(TotpSetup? setup, string? error)> TotpStartAsync(string password)
    {
        var (r, error) = await Send("totp_start", new { password }, WrongPassword);
        if (r is not { } ok) return (null, error);
        return AccountReplies.Totp(ok) is { } setup ? (setup, null) : (null, AccountRules.Message(null));
    }

    /// Turns 2단계 인증 on. Returns the 10 recovery codes, shown once.
    public static async Task<(string[]? backup, string? error)> TotpConfirmAsync(string code)
    {
        var (r, error) = await Send("totp_confirm", new { code }, WrongCode);
        if (r is not { } ok) return (null, error);
        return AccountReplies.Backup(ok) is { } codes ? (codes, null) : (null, AccountRules.Message(null));
    }

    /// code: an authenticator code or a recovery code.
    public static async Task<string?> TotpOffAsync(string password, string code) =>
        (await Send("totp_off", new { password, code }, "비밀번호나 코드가 맞지 않아요")).error;

    /// New recovery codes; every old one stops working.
    public static async Task<(string[]? backup, string? error)> BackupNewAsync(string password)
    {
        var (r, error) = await Send("backup_new", new { password }, WrongPassword);
        if (r is not { } ok) return (null, error);
        return AccountReplies.Backup(ok) is { } codes ? (codes, null) : (null, AccountRules.Message(null));
    }

    public static async Task<(List<AccountKey>? keys, string? error)> KeysAsync()
    {
        var (r, error) = await Send("keys");
        return r is { } ok ? (AccountReplies.Keys(ok), null) : (null, error);
    }

    /// A confirmation mail goes to the address the key was bought with; its link links the key.
    public static async Task<string?> LinkKeyAsync(string key) =>
        (await Send("link_key", new { key = key.Trim(), lang = Lang.Code })).error;

    /// The server's copy of a consent (e.g. kind "oc": the overclocking notice) for a signed-in member.
    /// Null on success or when nobody is signed in (nothing is sent then), else the error text.
    public static Task<string?> RecordConsentAsync(string kind, string version, string gpu, string appVersion, bool withdraw)
    {
        if (!SignedIn) return Task.FromResult<string?>(null);
        return Post();
        async Task<string?> Post() => (await Send("consent", new { kind, version, gpu, app = appVersion, withdraw })).error;
    }
}
