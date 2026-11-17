// The member server's replies (accounts v2), read leniently: a missing or odd field gives a default rather than
// an exception, because the server and the app ship apart and an older server simply lacks the new fields.
// Pure: no Avalonia, no app classes (SystemBoost.Account.Tests links this file).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SystemBoost.App.Services;

/// GET /account/features. Totp is true on every accounts v2 server; an older server answers 404 (None).
public sealed record AccountFeatures(bool Email, bool Totp)
{
    public static readonly AccountFeatures None = new(false, false);
}

/// login and login2: a session, or (login with 2단계 인증) the pending token for the code step.
public sealed record LoginReply(string? Id, string? Token, string? Pending, int? BackupLeft, bool Redo2fa)
{
    public bool NeedsCode => Token == null && Pending != null;
}

/// The fields `me` gained in v2. Absent on an older server: no email, 2단계 인증 off, count unknown.
public sealed record AccountExtras(string? Email, bool EmailVerified, bool TwoFa, int? BackupLeft);

public sealed record AccountDevice(string Id, string Name, string Country, DateTimeOffset? Created, DateTimeOffset? LastSeen, bool Current);

public sealed record AccountKey(string Id, string Plan, string Key, DateTimeOffset? Issued, DateTimeOffset? Expires, bool Revoked);

public sealed record TotpSetup(string Secret, string Uri);

public static class AccountReplies
{
    public static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Flag(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => v.GetString() is { Length: > 0 } s && s != "false" && s != "0",   // a revoke reason counts
            JsonValueKind.Number => v.TryGetDouble(out var d) && d != 0,
            _ => false,
        };
    }

    public static int? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// A time as milliseconds (or seconds) since 1970, or an ISO text. "" and null: none.
    public static DateTimeOffset? Time(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0)
            return n < 100_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(n) : DateTimeOffset.FromUnixTimeMilliseconds(n);
        if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) return t;
        return null;
    }

    public static AccountFeatures Features(JsonElement e) => new(Flag(e, "email"), Flag(e, "totp"));

    /// Null when the reply holds neither a session nor a pending code step.
    public static LoginReply? Login(JsonElement e)
    {
        var backupLeft = Number(e, "backupLeft");
        if (Text(e, "id") is { Length: > 0 } id && Text(e, "token") is { Length: > 0 } token)
            return new LoginReply(id, token, null, backupLeft, Flag(e, "redo2fa"));
        if (Flag(e, "twofa") && Text(e, "pending") is { Length: > 0 } pending)
            return new LoginReply(null, null, pending, null, false);
        return null;
    }

    public static AccountExtras Extras(JsonElement e) =>
        new(Text(e, "email") is { Length: > 0 } mail ? mail : null, Flag(e, "emailVerified"), Flag(e, "twofa"), Number(e, "backupLeft"));

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToArray()
            : Array.Empty<JsonElement>();

    /// This device first, then the most recently seen.
    public static List<AccountDevice> Devices(JsonElement e) =>
        Items(e, "devices")
            .Where(d => Text(d, "id") is { Length: > 0 })
            .Select(d => new AccountDevice(Text(d, "id")!, Text(d, "name") is { Length: > 0 } n ? n : "SystemBoost app",
                                           (Text(d, "country") ?? "").Trim().ToUpperInvariant(), Time(d, "created"), Time(d, "lastSeen"), Flag(d, "current")))
            .OrderByDescending(d => d.Current).ThenByDescending(d => d.LastSeen ?? d.Created ?? DateTimeOffset.MinValue)
            .ToList();

    /// Newest first.
    public static List<AccountKey> Keys(JsonElement e) =>
        Items(e, "keys")
            .Where(k => Text(k, "id") is { Length: > 0 })
            .Select(k => new AccountKey(Text(k, "id")!, Text(k, "plan") ?? "", Text(k, "key") ?? "", Time(k, "issued"), Time(k, "expires"), Flag(k, "revoked")))
            .OrderByDescending(k => k.Issued ?? DateTimeOffset.MinValue)
            .ToList();

    /// The backup codes of totp_confirm and backup_new; null unless the reply holds some.
    public static string[]? Backup(JsonElement e)
    {
        var codes = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("backup", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();
        return codes.Length > 0 ? codes : null;
    }

    public static TotpSetup? Totp(JsonElement e) =>
        Text(e, "secret") is { Length: > 0 } secret && Text(e, "uri") is { } uri && uri.StartsWith("otpauth://totp/", StringComparison.Ordinal)
            ? new TotpSetup(secret, uri) : null;

    /// The text a "복사", "텍스트 파일로 저장" or "인쇄" puts out: a title line, the codes one a line.
    public static string BackupText(IEnumerable<string> codes, string title, string note) =>
        string.Join(Environment.NewLine, new[] { title, note, "" }.Concat(codes)) + Environment.NewLine;
}
