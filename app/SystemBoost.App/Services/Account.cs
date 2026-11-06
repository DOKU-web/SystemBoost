// Member account. Optional: nothing in the app needs one. The app keeps a session token, never the
// password. Messages come back in Korean; Lang translates them where they reach the screen.
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public sealed record AccountProfile(string Id, DateTimeOffset Created, string? Code, int Invites, int RewardDays, int EarnedDays,
                                    int RewardCap, int RewardEach, int InviteDays);

public static class Account
{
    public const int MinPassword = 8, MaxPassword = 72;

    private sealed record Saved(string Id, string Token);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static string RecordFile => Path.Combine(Platform.DataDir, "account.json");
    private static Saved? _saved = Read();

    public static string? Id => _saved?.Id;
    public static bool SignedIn => _saved != null;
    public static event Action? Changed;

    private static Saved? Read()
    {
        try { if (File.Exists(RecordFile)) return JsonSerializer.Deserialize<Saved>(File.ReadAllText(RecordFile)); }
        catch (Exception) { }   // a damaged record counts as signed out
        return null;
    }

    private static void Keep(Saved? saved)
    {
        _saved = saved;
        try
        {
            if (saved == null) File.Delete(RecordFile);
            else File.WriteAllText(RecordFile, JsonSerializer.Serialize(saved));
        }
        catch (Exception) { }   // the session then lasts until the app closes
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    private static string Message(string? error) => error switch
    {
        "id" => "아이디는 영문 소문자, 숫자, 밑줄 4~20자예요. 쓸 수 없는 아이디도 있어요",
        "password" => $"비밀번호는 {MinPassword}~{MaxPassword}자로, 아이디와 다르게 정해 주세요",
        "consent" => "개인정보 수집·이용 동의와 만 14세 이상 확인이 필요해요",
        "taken" => "이미 쓰는 아이디예요",
        "wrong" => "아이디나 비밀번호, 복구 코드가 맞지 않아요",
        "locked" => "여러 번 틀려서 잠겼어요. 15분 뒤 다시 해 주세요",
        "blocked" => "이 계정은 막혀 있어요. 판매자에게 문의해 주세요",
        "busy" => "요청이 많아요. 잠시 뒤 다시 해 주세요",
        "unknown" => "없는 코드이거나 끝난 코드예요",
        "used up" => "이 코드는 사용 횟수가 다 찼어요",
        "already" => "이 기기는 이미 추천 코드나 초대 코드를 썼어요. 코드를 지우고 가입해 주세요",
        "bad request" => "코드는 영문과 숫자 4~16자예요",
        "nothing" => "받을 보상이 없어요",
        "session" => "로그인이 끝났어요. 다시 로그인해 주세요",
        "unavailable" => "회원 서버가 응답하지 않아요. 잠시 뒤 다시 해 주세요. 키와 앱 기능은 그대로 쓸 수 있어요",
        _ => "요청을 처리하지 못했어요. 잠시 뒤 다시 해 주세요",
    };

    private static async Task<(JsonElement? ok, string? error)> Send(string action, object? body = null)
    {
        try
        {
            using var req = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, $"{License.ServerUrl}/account/{action}");
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (_saved != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _saved.Token);
            using var r = await Http.SendAsync(req);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = doc.RootElement.Clone();
            if (r.IsSuccessStatusCode) return (root, null);
            var code = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (code == "session") Keep(null);   // ended here or on the server: either way it no longer works
            return (null, Message(code));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, "서버에 연결하지 못했어요. 인터넷 연결을 확인해 주세요");
        }
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// A key that came with a code. A paid key is never replaced by a short one.
    private static string? Take(JsonElement r)
    {
        var key = Text(r, "key");
        return key == null || License.IsPremium ? null : License.Activate(key);
    }

    /// Returns (error, recovery code to show once).
    public static async Task<(string? error, string? recovery)> SignUpAsync(string id, string password, string invite)
    {
        invite = invite.Trim();
        if (invite.Length > 0 && License.IsPremium) return ("유료 플랜을 쓰는 중이라 초대 코드를 쓸 수 없어요. 코드를 지우고 가입해 주세요", null);
        var (r, error) = await Send("signup", new { id, password, invite, device = License.ReferralDevice(), agree = true, age14 = true });
        if (r is not { } ok) return (error, null);
        Keep(new Saved(Text(ok, "id")!, Text(ok, "token")!));
        return (Take(ok), Text(ok, "recovery"));
    }

    public static async Task<string?> LogInAsync(string id, string password)
    {
        var (r, error) = await Send("login", new { id, password });
        if (r is { } ok) Keep(new Saved(Text(ok, "id")!, Text(ok, "token")!));
        return error;
    }

    public static async Task LogOutAsync()
    {
        await Send("logout", new { });   // offline, the session simply runs out on the server
        Keep(null);
    }

    public static async Task<(string? error, string? recovery)> RecoverAsync(string id, string recovery, string next)
    {
        var (r, error) = await Send("recover", new { id, recovery, next });
        if (r is not { } ok) return (error, null);
        Keep(new Saved(Text(ok, "id")!, Text(ok, "token")!));
        return (null, Text(ok, "recovery"));
    }

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
        return (new AccountProfile(Text(ok, "id")!, DateTimeOffset.FromUnixTimeMilliseconds(ok.GetProperty("created").GetInt64()), Text(ok, "code"),
                                   N("invites"), N("rewardDays"), N("earnedDays"), N("rewardCap"), N("rewardEach"), N("inviteDays")), null);
    }

    public static async Task<string?> NewInviteCodeAsync() => (await Send("invite", new { })).error;

    /// Trades the days earned for a key. They stay on the server while another key is running.
    public static async Task<string?> ClaimRewardAsync()
    {
        if (License.IsPremium) return "지금 쓰는 키가 끝난 뒤에 받을 수 있어요. 보상은 그대로 남아 있어요";
        var (r, error) = await Send("reward", new { });
        return r is { } ok ? Take(ok) : error;
    }
}
