// The member's session on this PC: account.json in Platform.DataDir (%APPDATA%\SystemBoost, or the data folder
// next to the app in the portable build, where the folder may be readable by every Windows user of the PC).
//
// Format 2, Windows: {"format":2,"protection":"dpapi-user","data":"<base64>"}. data is the id and session token
// sealed with DPAPI for the Windows user who signed in. Another Windows user of the PC, or the file copied to
// another PC, cannot open it; that counts as signed out. The entropy below is not a secret: it ships inside
// this program and only keeps the blob from opening for a program that unseals DPAPI data without it.
// DPAPI does not stop a program that runs as the same Windows user: it can unseal the file just as this app
// does, or read the token from this app's memory. Nor does it stop the member copying their own token.
//
// Format 1, written by v0.8.11 and older and still by the macOS build: {"Id":"...","Token":"..."} in plain
// text. Windows reads it once and rewrites it as format 2 at once, so the member stays signed in.
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SystemBoost.App.Services;

internal sealed record AccountSession(string Id, string Token);

internal static class AccountFile
{
    public const int Format = 2;
    private const string Protection = "dpapi-user";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SystemBoost account.json v2");   // not a secret, see above

    /// The session kept on this PC, or null: signed out. Never throws. A file that cannot be read, opened or
    /// understood is left as it is, neither renamed nor deleted: DPAPI can fail for a while (the Windows profile
    /// not loaded yet), and the next sign-in replaces the file anyway.
    public static AccountSession? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("format", out var format))
            {
                // Written by a newer app: left for it. Sealed on Windows but read elsewhere: copied from a PC.
                if (format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var v) || v != Format) return null;
                if (!OperatingSystem.IsWindows()) return null;
                if (Text(root, "protection") != Protection || Text(root, "data") is not { } data) return null;
                return Unseal(Convert.FromBase64String(data));
            }
            var old = Parse(root);
            // Format 1. If sealing or writing fails, the plain file stays and this runs again at the next start.
            if (old != null && OperatingSystem.IsWindows()) Save(path, old);
            return old;
        }
        catch (Exception) { return null; }   // damaged, or sealed for another Windows user or PC
    }

    /// Keeps the session; null forgets it. False when the file could not be written or deleted: the session
    /// then lasts until the app closes. Never throws, and never writes the token in plain text on Windows.
    public static bool Save(string path, AccountSession? session)
    {
        try
        {
            if (session == null) File.Delete(path);
            // macOS and Linux keep format 1 as before: there is no DPAPI there (the Keychain would be the match).
            else Replace(path, OperatingSystem.IsWindows() ? Sealed(session) : JsonSerializer.SerializeToUtf8Bytes(session));
            return true;
        }
        catch (Exception) { return false; }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static AccountSession? Parse(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && Text(e, "Id") is { Length: > 0 } id && Text(e, "Token") is { Length: > 0 } token
            ? new AccountSession(id, token) : null;

    // Kept apart so that a missing DPAPI library fails inside Load's and Save's catch, not before them.
    [SupportedOSPlatform("windows"), MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] Sealed(AccountSession session)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.SerializeToUtf8Bytes(new { format = Format, protection = Protection, data = Convert.ToBase64String(blob) });
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    [SupportedOSPlatform("windows"), MethodImpl(MethodImplOptions.NoInlining)]
    private static AccountSession? Unseal(byte[] blob)
    {
        var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var doc = JsonDocument.Parse(plain);
            return Parse(doc.RootElement);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// Writes beside the record under a name of its own, then puts it in the record's place in one step,
    /// so a crash or a full disk leaves the old record or the new one, never half of one.
    private static void Replace(string path, byte[] bytes)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                f.Write(bytes);
                f.Flush(true);
            }
            File.Move(tmp, path, true);
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception) { }   // already gone when the move worked
        }
    }
}
