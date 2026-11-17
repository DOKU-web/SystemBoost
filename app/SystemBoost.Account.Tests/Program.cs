// Tests for the account logic of the app (accounts v2): input rules, recovery code normalising, the server's error
// words, reply parsing and the QR encoder. Run: dotnet run --project app/SystemBoost.Account.Tests -c Release
// Prints each failure, then "N passed, M failed"; the exit code is 1 when anything failed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemBoost.App.Services;

namespace SystemBoost.Account.Tests;

internal static class Program
{
    private static int _passed, _failed;

    private static void Check(bool ok, string what)
    {
        if (ok) _passed++;
        else { _failed++; Console.WriteLine("FAIL " + what); }
    }

    private static void Same<T>(T expected, T actual, string what) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected [{expected}], got [{actual}]");

    private static void Throws<TException>(Action act, string what) where TException : Exception
    {
        try { act(); Check(false, what + ": no exception"); }
        catch (TException) { Check(true, what); }
        catch (Exception e) { Check(false, $"{what}: {e.GetType().Name}"); }
    }

    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static int Main()
    {
        var groups = new (string Name, Action Run)[]
        {
            ("backup codes", BackupCodes), ("authenticator codes", TotpCodes), ("email", Emails), ("new passwords", Passwords),
            ("device", Device), ("small helpers", Helpers), ("error words", ErrorWords), ("replies", Replies),
            ("QR building blocks", QrBlocks), ("QR against python-qrcode", QrSweep), ("QR otpauth address", QrUri),
        };
        foreach (var (name, run) in groups)
        {
            try { run(); }
            catch (Exception e) { Check(false, $"{name} threw {e.GetType().Name}: {e.Message}"); }
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ AccountRules
    private static void BackupCodes()
    {
        Same("ABCD-EFGH-JKMN", AccountRules.BackupCode("ABCD-EFGH-JKMN"), "as given");
        Same("ABCD-EFGH-JKMN", AccountRules.BackupCode("abcd-efgh-jkmn"), "lower case");
        Same("ABCD-EFGH-JKMN", AccountRules.BackupCode(" ab cd ef gh jk mn "), "spaces");
        Same("ABCD-EFGH-JKMN", AccountRules.BackupCode("ABCDEFGHJKMN"), "no dashes");
        Same("ABCD-EFGH-JKMN", AccountRules.BackupCode("ABCD–EFGH—JKMN"), "typographic dashes");
        Same("ABCD-EFGH-JKMN", AccountRules.BackupCode("ABCD-\tEFGH\r\nJKMN"), "tab and line break");
        Same("2345-6789-WXYZ", AccountRules.BackupCode("2345 6789 wxyz"), "digits");
        Same(null, AccountRules.BackupCode("ABCD-EFGH-JKM"), "11 letters");
        Same(null, AccountRules.BackupCode("ABCD-EFGH-JKMNP"), "13 letters");
        Same(null, AccountRules.BackupCode("ABCD-EFGH-JKM0"), "zero is never used");
        Same(null, AccountRules.BackupCode("ABCD-EFGH-JKMO"), "O is never used");
        Same(null, AccountRules.BackupCode("ABCD-EFGH-JKM1"), "one is never used");
        Same(null, AccountRules.BackupCode("ABCD-EFGH-JKMI"), "I is never used");
        Same(null, AccountRules.BackupCode("ABCD-EFGH-JKML"), "L is never used");
        Same(null, AccountRules.BackupCode(""), "empty");
        Same(null, AccountRules.BackupCode(null), "null");
        Same(null, AccountRules.BackupCode("XXXX-XXXX-XXXX-XXXX"), "a 아이디 recovery code is not one");
        Same(31, AccountRules.BackupAlphabet.Length, "alphabet size");
        Check(!AccountRules.BackupAlphabet.Any(c => "01OIL".Contains(c)), "alphabet leaves out 0 O 1 I L");
    }

    private static void TotpCodes()
    {
        Same("123456", AccountRules.TotpCode("123456"), "plain");
        Same("123456", AccountRules.TotpCode("123 456"), "space in the middle");
        Same("012345", AccountRules.TotpCode(" 012345 "), "leading zero and spaces");
        Same("123456", AccountRules.TotpCode("123-456"), "dash");
        Same(null, AccountRules.TotpCode("12345"), "five digits");
        Same(null, AccountRules.TotpCode("1234567"), "seven digits");
        Same(null, AccountRules.TotpCode("12345a"), "a letter");
        Same(null, AccountRules.TotpCode("１２３４５６"), "full-width digits");
        Same(null, AccountRules.TotpCode(null), "null");
    }

    private static void Emails()
    {
        foreach (var good in new[] { "a@b.co", "User.Name+tag@example.com", " me@mail.example.kr ", "x@naver.com", "a-b@sub-domain.example.org" })
            Check(AccountRules.ValidEmail(good), "valid: " + good);
        foreach (var bad in new[] { "", " ", "plain", "@example.com", "a@", "a@b", "a@.com", "a@com.", "a@b..com", "a b@example.com",
                                    "a@b@example.com", "a@exa mple.com", "a@example_x.com", new string('a', 65) + "@example.com",
                                    "a@" + new string('b', 250) + ".com" })
            Check(!AccountRules.ValidEmail(bad), "invalid: " + bad);
        Check(!AccountRules.ValidEmail(null), "invalid: null");
        Same("me@example.com", AccountRules.CleanEmail("  Me@Example.COM "), "clean");
        Check(AccountRules.IsEmail("me@example.com") && !AccountRules.IsEmail("my_id"), "email or 아이디");
    }

    private static void Passwords()
    {
        Same(null, AccountRules.NewPasswordProblem("correct horse", "correct horse", "my_id"), "good");
        Same(null, AccountRules.NewPasswordProblem("0123456789", null), "exactly 10, no repeat box");
        Check(AccountRules.NewPasswordProblem("012345678", null) != null, "9 is short");
        Check(AccountRules.NewPasswordProblem(new string('x', 73), null) != null, "73 is long");
        Same(null, AccountRules.NewPasswordProblem(new string('x', 72), null), "72 is fine");
        Check(AccountRules.NewPasswordProblem(null, null) != null, "null");
        Same("비밀번호가 서로 달라요", AccountRules.NewPasswordProblem("correct horse", "correct horsf"), "repeat differs");
        Check(AccountRules.NewPasswordProblem("long_member_id", null, "long_member_id") != null, "same as the id");
        Check(AccountRules.NewPasswordProblem("Long_Member_ID", null, "long_member_id") != null, "same as the id, other case");
        Check(AccountRules.NewPasswordProblem("someone@example.com", null, null, "someone@example.com") != null, "same as the email");
        Check(AccountRules.NewPasswordProblem("someone1234", null, "Someone1234@Example.com") != null, "same as the email's local part");
        Same(null, AccountRules.NewPasswordProblem("someone1234!", null, "someone1234@example.com"), "differs from the local part");
        Same(10, AccountRules.MinPassword, "new minimum");
        Same(72, AccountRules.MaxPassword, "maximum");
    }

    private static void Device()
    {
        Same("Windows 11 PC", AccountRules.DeviceName(true, false, new Version(10, 0, 26200)), "Windows 11 (25H2)");
        Same("Windows 11 PC", AccountRules.DeviceName(true, false, new Version(10, 0, 22000)), "Windows 11 (first)");
        Same("Windows 10 PC", AccountRules.DeviceName(true, false, new Version(10, 0, 19045)), "Windows 10");
        Same("Windows PC", AccountRules.DeviceName(true, false, new Version(6, 1, 7601)), "older Windows");
        Same("Mac", AccountRules.DeviceName(false, true, new Version(15, 1)), "macOS");
        Same("Linux PC", AccountRules.DeviceName(false, false, new Version(6, 8)), "other");
        var name = AccountRules.DeviceName(true, false, new Version(10, 0, 26200));
        bool Holds(string s) => s.Length > 3 && name.Contains(s, StringComparison.OrdinalIgnoreCase);   // "PC" as a machine name proves nothing
        Check(!Holds(Environment.MachineName) && !Holds(Environment.UserName), "the device name holds neither the PC's nor the user's name");
        Same("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", AccountRules.DeviceKey(new byte[32]), "key of 32 zero bytes");
        var ascending = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Same("630dcd2966c4336691125448bbb25b4ff412a49c732db2c8abc1b8581bd710dd", AccountRules.DeviceKey(ascending), "key of 0..31");
        var id = RandomNumberGenerator.GetBytes(32);
        var text = Convert.ToHexString(id).ToLowerInvariant();
        Check(AccountRules.ParseInstallId(text) is { } back && back.SequenceEqual(id), "install id round trip");
        Check(AccountRules.ParseInstallId(text.ToUpperInvariant() + "\r\n") is { Length: 32 }, "upper case and a line break");
        Same(null, AccountRules.ParseInstallId(text[..62]), "short id");
        Same(null, AccountRules.ParseInstallId(text[..63] + "g"), "not hex");
        Same(null, AccountRules.ParseInstallId(null), "no file text");
        Same(64, AccountRules.DeviceKey(id).Length, "key length");
        Check(AccountRules.DeviceKey(id) != text, "the key is not the id");
    }

    private static void Helpers()
    {
        Same("JBSW Y3DP EHPK 3PXP", AccountRules.GroupSecret("JBSWY3DPEHPK3PXP"), "secret in fours");
        Same("JBSW Y3DP EH", AccountRules.GroupSecret(" jbsw y3dp eh "), "secret cleaned");
        Same(((string?)"1234", (string?)null), AccountRules.CardOrDepositor("1234"), "card digits");
        Same(((string?)"1234", (string?)null), AccountRules.CardOrDepositor("**** 1234"), "masked card");
        Same(((string?)null, (string?)"홍길동"), AccountRules.CardOrDepositor(" 홍길동 "), "payer name");
        Same(((string?)null, (string?)"12345"), AccountRules.CardOrDepositor("12345"), "five digits are a name");
        Same(((string?)null, (string?)null), AccountRules.CardOrDepositor("  "), "nothing");
        Same("2026-11-18", AccountRules.ConsentVersion, "consent version sent with a sign-up");
    }

    private static void ErrorWords()
    {
        var general = AccountRules.Message(null);
        Same(general, AccountRules.Message("something new"), "an unknown word gets the general message");
        foreach (var word in new[] { "weak", "email", "expired", "mail_off", "no_email", "key", "locked", "busy", "unavailable",
                                     "id", "password", "consent", "taken", "wrong", "blocked", "session" })
        {
            var text = AccountRules.Message(word);
            Check(text != general && text.Length > 0, "a message of its own: " + word);
        }
        Check(AccountRules.Message("password").Contains("10~72"), "the password message says 10~72");
        Check(AccountRules.Message("weak").Contains("10자"), "the weak message says 10");
        Check(AccountRules.Message("no_email").Contains("고객센터"), "no_email points to 고객센터");
        Same("코드가 틀렸어요", AccountRules.Message("wrong", "코드가 틀렸어요"), "wrong means what the call says");
        Same(AccountRules.Message("busy"), AccountRules.Message("busy", "코드가 틀렸어요"), "other words keep their text");
    }

    // ------------------------------------------------------------ AccountReplies
    private static void Replies()
    {
        var session = AccountReplies.Login(J("""{"id":"my_id","token":"abc"}"""));
        Check(session is { Id: "my_id", Token: "abc", NeedsCode: false, Pending: null }, "login: a session");
        var pending = AccountReplies.Login(J("""{"twofa":true,"pending":"p123"}"""));
        Check(pending is { NeedsCode: true, Pending: "p123", Token: null }, "login: the code step");
        Same(null, AccountReplies.Login(J("""{"twofa":true}""")), "login: code step without a pending token");
        Same(null, AccountReplies.Login(J("""{"id":"x"}""")), "login: no token");
        Same(null, AccountReplies.Login(J("""[]""")), "login: not an object");
        var backup = AccountReplies.Login(J("""{"id":"m1234567890a","token":"t","backupLeft":3,"redo2fa":true}"""));
        Check(backup is { BackupLeft: 3, Redo2fa: true, NeedsCode: false }, "login2 with a recovery code");
        var plain = AccountReplies.Login(J("""{"id":"a","token":"t"}"""));
        Check(plain is { BackupLeft: null, Redo2fa: false }, "login2 with the authenticator");

        Same(new AccountFeatures(true, true), AccountReplies.Features(J("""{"email":true,"totp":true}""")), "features: all");
        Same(new AccountFeatures(false, true), AccountReplies.Features(J("""{"email":false,"totp":true}""")), "features: mail off");
        Same(AccountFeatures.None, AccountReplies.Features(J("""{"error":"not found"}""")), "features: older server");

        Same(new AccountExtras("a@b.co", true, true, 7),
             AccountReplies.Extras(J("""{"id":"x","email":"a@b.co","emailVerified":true,"twofa":true,"backupLeft":7}""")), "me: v2 fields");
        Same(new AccountExtras(null, false, false, null), AccountReplies.Extras(J("""{"id":"x","created":1}""")), "me: older server");
        Same(new AccountExtras(null, false, false, null), AccountReplies.Extras(J("""{"email":null,"emailVerified":null}""")), "me: nulls");

        var devices = AccountReplies.Devices(J("""
            {"devices":[
              {"id":"aaaaaaaaaaaa","name":"Windows 10 PC","country":"kr","created":1700000000000,"lastSeen":1700000500000,"current":false},
              {"id":"bbbbbbbbbbbb","name":"Windows 11 PC","country":"KR","created":"2026-11-01T10:00:00Z","lastSeen":"2026-11-17T09:00:00Z","current":true},
              {"id":"cccccccccccc","name":"","country":"","created":1700000000,"lastSeen":null,"current":false},
              {"name":"no id"}, 5
            ]}
            """));
        Same(3, devices.Count, "devices: those with an id");
        Same("bbbbbbbbbbbb", devices[0].Id, "devices: this one first");
        Check(devices[0].Current && devices.Count(d => d.Current) == 1, "devices: one current");
        Same("KR", devices[1].Country, "devices: country upper case");
        Same("SystemBoost app", devices[2].Name, "devices: unnamed (an older app)");
        Same(DateTimeOffset.FromUnixTimeMilliseconds(1700000500000), devices[1].LastSeen, "devices: milliseconds");
        Same(DateTimeOffset.FromUnixTimeSeconds(1700000000), devices[2].Created, "devices: seconds");
        Same(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero), devices[0].LastSeen, "devices: ISO time");
        Same(0, AccountReplies.Devices(J("""{"error":"x"}""")).Count, "devices: none");

        var keys = AccountReplies.Keys(J("""
            {"keys":[
              {"id":"k1","plan":"premium","key":"SB1-AAA","issued":"2026-10-01T00:00:00Z","expires":"2026-11-01T00:00:00Z","revoked":""},
              {"id":"k2","plan":"ultra","key":"SB1-BBB","issued":"2026-11-10T00:00:00Z","expires":"","revoked":"환불"},
              {"id":"k3","plan":"biz","key":"SB1-CCC","issued":1762000000000,"expires":null,"revoked":true},
              {"id":"k4","plan":"premium","key":"SB1-DDD","issued":"2026-09-01T00:00:00Z","revoked":false}
            ]}
            """));
        Same(4, keys.Count, "keys: all");
        Same("k2", keys[0].Id, "keys: newest first");
        Check(keys[0].Revoked && keys.Single(k => k.Id == "k3").Revoked, "keys: revoked as a reason or true");
        Check(!keys.Single(k => k.Id == "k1").Revoked && !keys.Single(k => k.Id == "k4").Revoked, "keys: not revoked as \"\" or false");
        Same(null, keys[0].Expires, "keys: no end date");
        Same(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), keys.Single(k => k.Id == "k1").Expires, "keys: end date");
        Same("SB1-AAA", keys.Single(k => k.Id == "k1").Key, "keys: the key text");

        var codes = AccountReplies.Backup(J("""{"backup":["AAAA-BBBB-CCCC","DDDD-EEEE-FFFF",3,""]}"""));
        Check(codes != null && codes.SequenceEqual(new[] { "AAAA-BBBB-CCCC", "DDDD-EEEE-FFFF" }), "backup: the strings");
        Same(null, AccountReplies.Backup(J("""{"backup":[]}""")), "backup: none");
        Same(null, AccountReplies.Backup(J("""{"done":true}""")), "backup: missing");

        var totp = AccountReplies.Totp(J("""{"secret":"JBSWY3DPEHPK3PXP","uri":"otpauth://totp/SystemBoost:a?secret=JBSWY3DPEHPK3PXP&issuer=SystemBoost"}"""));
        Check(totp is { Secret: "JBSWY3DPEHPK3PXP" } && totp.Uri.StartsWith("otpauth://totp/"), "totp_start");
        Same(null, AccountReplies.Totp(J("""{"secret":"X","uri":"https://example.com"}""")), "totp_start: not an otpauth address");
        Same(null, AccountReplies.Totp(J("""{"uri":"otpauth://totp/x"}""")), "totp_start: no secret");

        var text = AccountReplies.BackupText(new[] { "AAAA-BBBB-CCCC", "DDDD-EEEE-FFFF" }, "title", "note");
        var lines = text.Split(Environment.NewLine);
        Check(lines[0] == "title" && lines[1] == "note" && lines[2] == "" && lines[3] == "AAAA-BBBB-CCCC" && lines[4] == "DDDD-EEEE-FFFF", "backup file text");
        Same(null, AccountReplies.Time(J("""{"t":""}"""), "t"), "time: empty text");
        Same(null, AccountReplies.Time(J("""{"t":"soon"}"""), "t"), "time: not a time");
        Same(null, AccountReplies.Time(J("""{"t":0}"""), "t"), "time: zero");
    }

    // ------------------------------------------------------------ QrCode
    private static string Hash(QrCode qr)
    {
        var sb = new StringBuilder(qr.Size * qr.Size);
        for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++) sb.Append(qr.Dark(x, y) ? '1' : '0');
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    private static void QrBlocks()
    {
        // ISO/IEC 18004 worked values (also in Thonky's QR tutorial)
        Same(0b101010000010010, QrCode.FormatBits(QrCode.Ecc.Medium, 0), "format bits M, mask 0");
        Same(0b110011000101111, QrCode.FormatBits(QrCode.Ecc.Low, 4), "format bits L, mask 4");
        Same(0b000111110010010100, QrCode.VersionBits(7), "version bits 7");
        Same(0b101000110001101001, QrCode.VersionBits(40), "version bits 40");
        // "HELLO WORLD" 1-M: its 16 data codewords and the 10 error correction codewords
        var data = new byte[] { 32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17 };
        var ecc = QrCode.ReedSolomonRemainder(data, QrCode.ReedSolomonDivisor(10));
        Check(ecc.SequenceEqual(new byte[] { 196, 35, 39, 119, 235, 215, 231, 226, 93, 23 }), "Reed-Solomon of HELLO WORLD 1-M: " + string.Join(",", ecc));
        Same(17, QrCode.ByteCapacity(1, QrCode.Ecc.Low), "capacity 1-L");
        Same(14, QrCode.ByteCapacity(1, QrCode.Ecc.Medium), "capacity 1-M");
        Same(2953, QrCode.ByteCapacity(40, QrCode.Ecc.Low), "capacity 40-L");
        Same(1273, QrCode.ByteCapacity(40, QrCode.Ecc.High), "capacity 40-H");
        var small = QrCode.FromBytes(Encoding.ASCII.GetBytes("SystemBoost"), QrCode.Ecc.Medium, mask: 2);
        Same(1, small.Version, "small: version 1");
        Same(21, small.Size, "small: size 21");
        var drawn = Enumerable.Range(0, small.Size).Select(y => new string(Enumerable.Range(0, small.Size).Select(x => small.Dark(x, y) ? '#' : '.').ToArray())).ToArray();
        for (int y = 0; y < small.Size; y++) Same(QrReference.Small[y], drawn[y], $"small: row {y}");
        Throws<ArgumentException>(() => QrCode.FromBytes(new byte[2954], QrCode.Ecc.Low), "too long for version 40");
        Throws<ArgumentException>(() => QrCode.FromBytes(new byte[15], QrCode.Ecc.Medium, maxVersion: 1), "too long for version 1");
        Throws<ArgumentOutOfRangeException>(() => QrCode.FromBytes(new byte[1], mask: 8), "mask 8");
        Same(2, QrCode.FromBytes(new byte[15], QrCode.Ecc.Medium).Version, "15 bytes at M need version 2");
        Same(1, QrCode.FromBytes(Array.Empty<byte>()).Version, "empty data");
    }

    private static void QrSweep()
    {
        Same(160, QrReference.Sweep.Length, "sweep covers 40 versions x 4 levels");
        foreach (var (level, version, bytes, mask, hash) in QrReference.Sweep)
        {
            var ecc = (QrCode.Ecc)level;
            var what = $"{version}-{"LMQH"[level]} mask {mask}";
            Same(bytes, QrCode.ByteCapacity(version, ecc), what + " capacity");
            var data = Enumerable.Range(0, bytes).Select(i => (byte)((i * 37 + version * 11 + level) % 256)).ToArray();
            var qr = QrCode.FromBytes(data, ecc, minVersion: version, maxVersion: version, mask: mask);
            Check(qr.Version == version && qr.Size == version * 4 + 17 && qr.Mask == mask && qr.Level == ecc, what + " shape");
            Same(hash, Hash(qr), what + " matrix");
            // a full version must not be picked when the data fits the one below
            if (version > 1)
                Same(version, QrCode.FromBytes(data, ecc).Version, what + " smallest version");
        }
    }

    private static void QrUri()
    {
        var qr = QrCode.FromText(QrReference.Uri);
        Same(QrReference.UriVersion, qr.Version, "otpauth: version");
        Same(QrCode.Ecc.Medium, qr.Level, "otpauth: level M");
        Check(qr.Mask is >= 0 and <= 7, "otpauth: a mask was chosen");
        Same(QrReference.UriHashes[qr.Mask], Hash(qr), $"otpauth: matrix with the chosen mask {qr.Mask}");
        for (int m = 0; m < 8; m++)
            Same(QrReference.UriHashes[m], Hash(QrCode.FromBytes(Encoding.UTF8.GetBytes(QrReference.Uri), QrCode.Ecc.Medium, mask: m)), $"otpauth: mask {m}");
        var longer = "otpauth://totp/SystemBoost:" + new string('a', 240) + "%40example.com?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=SystemBoost&digits=6&period=30";
        Check(QrCode.FromText(longer).Version <= 15, "otpauth with the longest email still fits a scannable version");
    }
}
