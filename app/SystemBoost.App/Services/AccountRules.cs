// What the account screens check before anything goes to the server, and how the server's error words read.
// Pure: no Avalonia, no app classes, no files (SystemBoost.Account.Tests links this file). The server checks
// everything again; these checks only save a round trip and say clearly what to fix.
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SystemBoost.App.Services;

public static class AccountRules
{
    /// Every new password (sign-up, change, reset) of any account. Old 8+ passwords still log in.
    public const int MinPassword = 10, MaxPassword = 72;
    public const int MaxEmail = 254;
    /// The consent text version a sign-up agrees to (AccountPage.ConsentLines).
    public const string ConsentVersion = "2026-11-18";
    /// 2단계 인증 복구 코드: 10 of "XXXX-XXXX-XXXX" from these letters (no 0/O, 1/I/L).
    public const string BackupAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public const int BackupLength = 12, BackupFew = 3;

    // ------------------------------------------------------------ input
    public static bool IsEmail(string? login) => login != null && login.Contains('@');

    /// Trimmed and lowercased, as the server keeps it.
    public static string CleanEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    /// A simple check, like the server's: one @, something before it, a dotted domain, no spaces, 254 at most.
    public static bool ValidEmail(string? email)
    {
        var e = CleanEmail(email);
        if (e.Length == 0 || e.Length > MaxEmail || e.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        int at = e.IndexOf('@');
        if (at < 1 || at != e.LastIndexOf('@') || at > 64) return false;
        var domain = e[(at + 1)..];
        if (domain.Length < 3 || !domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains("..")) return false;
        return domain.All(c => char.IsLetterOrDigit(c) || c is '.' or '-');
    }

    /// Null when a new password may be sent, else what to fix. names: the id and/or email it must differ from.
    public static string? NewPasswordProblem(string? password, string? again, params string?[] names)
    {
        var p = password ?? "";
        if (p.Length < MinPassword || p.Length > MaxPassword) return $"비밀번호는 {MinPassword}~{MaxPassword}자로 정해 주세요";
        if (again != null && p != again) return "비밀번호가 서로 달라요";
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var n = name.Trim().ToLowerInvariant();
            int at = n.IndexOf('@');
            if (p.ToLowerInvariant() == n || at > 0 && p.ToLowerInvariant() == n[..at])
                return "비밀번호는 아이디나 이메일과 다르게 정해 주세요";
        }
        return null;
    }

    /// An authenticator code: 6 digits, spaces allowed while typing ("123 456"). Null when it is not one.
    public static string? TotpCode(string? input)
    {
        var digits = new string((input ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return digits.Length == 6 && digits.All(c => c is >= '0' and <= '9') ? digits : null;
    }

    /// A backup code as the server spells it, "XXXX-XXXX-XXXX": case, dashes and spaces do not matter.
    /// Null when it cannot be one (wrong length or a letter the codes never use).
    public static string? BackupCode(string? input)
    {
        var sb = new StringBuilder();
        foreach (var c in input ?? "")
        {
            if (char.IsWhiteSpace(c) || c is '-' or '‐' or '‑' or '–' or '—' or '_' or '.') continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        var s = sb.ToString();
        if (s.Length != BackupLength || s.Any(c => !BackupAlphabet.Contains(c))) return null;
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    /// The base32 secret in groups of four, easier to type into an authenticator app by hand.
    public static string GroupSecret(string? secret)
    {
        var s = new string((secret ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// "카드 끝 4자리 또는 입금자명": four digits are a card's last digits, anything else a payer name.
    public static (string? card4, string? depositor) CardOrDepositor(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return (null, null);
        var digits = new string(s.Where(c => !char.IsWhiteSpace(c) && c != '*' && c != '-').ToArray());
        return digits.Length == 4 && digits.All(char.IsAsciiDigit) ? (digits, null) : (null, s);
    }

    // ------------------------------------------------------------ this device
    /// What the device list shows for this PC: the kind of computer, never its name or the user's.
    public static string DeviceName(bool windows, bool mac, Version os) =>
        windows ? os.Major >= 10 ? os.Build >= 22000 ? "Windows 11 PC" : "Windows 10 PC" : "Windows PC"
        : mac ? "Mac" : "Linux PC";

    /// The device key the server sees: sha256 (hex) of the random install id, never the id itself.
    public static string DeviceKey(byte[] installId) => Convert.ToHexString(SHA256.HashData(installId)).ToLowerInvariant();

    /// The install id as kept in its file: 64 hex characters. Null when the text is not one.
    public static byte[]? ParseInstallId(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length != 64 || !t.All(char.IsAsciiHexDigit)) return null;
        return Convert.FromHexString(t);
    }

    // ------------------------------------------------------------ server error words
    /// The server's error word as the member reads it. Unknown words get the general message. wrong: what
    /// "wrong" means for this call (a code step has no id), else today's id/password/recovery code text.
    public static string Message(string? error, string? wrong = null) => error switch
    {
        "wrong" when wrong != null => wrong,
        "id" => "아이디는 영문 소문자, 숫자, 밑줄 4~20자예요. 쓸 수 없는 아이디도 있어요",
        "password" => $"비밀번호는 {MinPassword}~{MaxPassword}자로, 아이디와 다르게 정해 주세요",
        "weak" => $"쓸 수 없는 비밀번호예요. {MinPassword}자 이상으로, 아이디나 이메일과 다르고 흔하지 않은 비밀번호로 정해 주세요",
        "email" => "이메일 주소를 확인해 주세요",
        "consent" => "개인정보 수집·이용 동의와 만 14세 이상 확인이 필요해요",
        "taken" => "이미 쓰는 아이디예요",
        "wrong" => "아이디나 비밀번호, 복구 코드가 맞지 않아요",
        "expired" => "시간이 지났거나 이미 쓴 요청이에요. 처음부터 다시 해 주세요",
        "locked" => "여러 번 틀려서 잠겼어요. 15분 뒤 다시 해 주세요",
        "blocked" => "이 계정은 막혀 있어요. 판매자에게 문의해 주세요",
        "busy" => "요청이 많아요. 잠시 뒤 다시 해 주세요",
        "mail_off" => "지금은 메일을 보낼 수 없어요. 잠시 뒤 다시 해 주세요. 급하면 고객센터에 문의해 주세요",
        "no_email" => "이 키에는 구매 이메일이 없어요. 계좌이체로 산 키는 고객센터(디스코드 #help)에서 입금자명과 입금 날짜를 확인한 뒤 연결해 드려요",
        "key" => "키가 맞지 않거나 막힌 키예요. 키 전체를 그대로 붙여 넣어 주세요",
        "unknown" => "없는 코드이거나 끝난 코드예요",
        "used up" => "이 코드는 사용 횟수가 다 찼어요",
        "already" => "이 기기는 이미 추천 코드나 초대 코드를 썼어요. 코드를 지우고 가입해 주세요",
        "bad request" => "코드는 영문과 숫자 4~16자예요",
        "nothing" => "받을 보상이 없어요",
        "session" => "로그인이 끝났어요. 다시 로그인해 주세요",
        "unavailable" => "회원 서버가 응답하지 않아요. 잠시 뒤 다시 해 주세요. 키와 앱 기능은 그대로 쓸 수 있어요",
        _ => "요청을 처리하지 못했어요. 잠시 뒤 다시 해 주세요",
    };
}
