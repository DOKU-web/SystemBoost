// Consent to the overclocking & power limit notice (OverclockNotice), kept in consents.json in Platform.DataDir
// (%APPDATA%\SystemBoost; the data folder of a portable copy). The file is a list of records, only ever appended to;
// the latest record of a kind decides:
//   [{"kind":"oc","version":"2026-11-18","at":"2026-11-18T03:00:00Z","gpu":"NVIDIA GeForce RTX 4070","app":"0.8.16","withdrawn":false}]
// Nothing personal beyond the graphics card's model. A missing file is no consent; so is an unreadable one, which is
// moved aside to consents.json.bad. Written whole to consents.json.tmp, then moved over the file (one atomic step).
// Asked (again) by Needs: no record, withdrawn, another version of the notice, another graphics card, and always for
// an imported preset with overclock values. The window and EnsureAsync are in OcConsent.App.cs.
// No Avalonia here: the GPU tests link this file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public sealed class ConsentRecord
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    /// OverclockNotice.Version that was shown.
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    /// UTC, ISO 8601 ("2026-11-18T03:00:00Z").
    [JsonPropertyName("at")] public string At { get; set; } = "";
    /// The graphics card's model as the driver names it.
    [JsonPropertyName("gpu")] public string Gpu { get; set; } = "";
    /// The app version that recorded it.
    [JsonPropertyName("app")] public string App { get; set; } = "";
    [JsonPropertyName("withdrawn")] public bool Withdrawn { get; set; }

    public DateTime? AtUtc =>
        DateTime.TryParse(At, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

/// Why the notice is asked (again). None: a consent to this version for this card is on record.
public enum ConsentNeed { None, NoRecord, Withdrawn, OtherVersion, OtherGpu, Preset }

/// consents.json, or any file the tests give it.
public sealed class ConsentStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly object _lock = new();

    public string FilePath { get; }
    public ConsentStore(string path) { FilePath = path; }
    public string BadPath => FilePath + ".bad";

    /// Every record, oldest first. Missing or unreadable: none (an unreadable file is moved to .bad, so it is kept once).
    public List<ConsentRecord> Load()
    {
        lock (_lock) return LoadCore();
    }

    private List<ConsentRecord> LoadCore()
    {
        string text;
        try
        {
            if (!File.Exists(FilePath)) return new();
            text = File.ReadAllText(FilePath);
        }
        catch (Exception) { return new(); }   // cannot be read now (locked, no access): no consent, the file stays as it is
        try
        {
            var list = JsonSerializer.Deserialize<List<ConsentRecord?>>(text) ?? throw new JsonException("null");
            return list.Where(r => r != null).Select(r => Clean(r!)).ToList();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            try { File.Move(FilePath, BadPath, overwrite: true); } catch (Exception) { }
            return new();
        }
    }

    private static ConsentRecord Clean(ConsentRecord r)
    {
        r.Kind ??= "";
        r.Version ??= "";
        r.At ??= "";
        r.Gpu ??= "";
        r.App ??= "";
        return r;
    }

    /// Adds one record at the end. Throws when it could not be written (the file is then as before).
    public void Append(ConsentRecord record)
    {
        lock (_lock)
        {
            var list = LoadCore();
            list.Add(record);
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}

public static partial class OcConsent
{
    public const string Kind = "oc";
    public const string FileName = "consents.json";

    /// Set by the account code: records the same consent or withdrawal on a signed-in member's server account.
    /// (kind, version, gpu, app, withdraw). Called after agree and after withdraw, never awaited, errors ignored.
    /// Null (the default): nothing leaves this PC.
    public static Func<string, string, string, string, bool, Task>? ServerRecord { get; set; }

    /// A consent was given or withdrawn. May fire on any thread.
    public static event Action? Changed;

    /// The latest overclocking record, null when there is none.
    public static ConsentRecord? Latest(IEnumerable<ConsentRecord> records) => records.LastOrDefault(r => r.Kind == Kind);

    /// A consent is on record and not withdrawn (for any version or card): '동의 철회' is offered.
    public static bool InForce(IEnumerable<ConsentRecord> records) => Latest(records) is { Withdrawn: false };

    /// Whether the notice must be shown before an overclocking or power limit write to the card named gpu.
    public static ConsentNeed Needs(IEnumerable<ConsentRecord> records, string gpu, bool fromPreset, string version = OverclockNotice.Version)
    {
        if (fromPreset) return ConsentNeed.Preset;
        var last = Latest(records);
        if (last == null) return ConsentNeed.NoRecord;
        if (last.Withdrawn) return ConsentNeed.Withdrawn;
        if (last.Version != version) return ConsentNeed.OtherVersion;
        if (!SameGpu(last.Gpu, gpu)) return ConsentNeed.OtherGpu;
        return ConsentNeed.None;
    }

    /// Model names as drivers report them: case and spacing do not make another card.
    public static bool SameGpu(string a, string b) =>
        string.Equals(string.Join(' ', (a ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                      string.Join(' ', (b ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);

    /// The line under the headline when the notice is asked again; null the first time.
    public static string? WhyAgain(ConsentNeed need) => need switch
    {
        ConsentNeed.Withdrawn => "동의를 철회했어요. 다시 쓰려면 아래 안내를 읽고 다시 동의해 주세요.",
        ConsentNeed.OtherVersion => "안내 문구가 바뀌어서 다시 동의를 받아요.",
        ConsentNeed.OtherGpu => "그래픽카드가 바뀌어서 다시 동의를 받아요.",
        ConsentNeed.Preset => "가져온 프리셋에 오버클럭 값이 있어서 다시 동의를 받아요.",
        _ => null,
    };

    public static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// Records a consent to the current notice for this card. Throws when the file could not be written.
    public static ConsentRecord Agree(ConsentStore store, string gpu, string app, DateTime utcNow)
    {
        var r = new ConsentRecord { Kind = Kind, Version = OverclockNotice.Version, At = Stamp(utcNow), Gpu = gpu ?? "", App = app ?? "" };
        store.Append(r);
        Tell(r);
        return r;
    }

    /// Records the withdrawal. gpu: the card on screen, otherwise the one of the consent it withdraws.
    /// Throws when the file could not be written.
    public static ConsentRecord Withdraw(ConsentStore store, string gpu, string app, DateTime utcNow)
    {
        var last = Latest(store.Load());
        var r = new ConsentRecord
        {
            Kind = Kind, Version = last?.Version is { Length: > 0 } v ? v : OverclockNotice.Version, At = Stamp(utcNow),
            Gpu = string.IsNullOrWhiteSpace(gpu) ? last?.Gpu ?? "" : gpu, App = app ?? "", Withdrawn = true,
        };
        store.Append(r);
        Tell(r);
        return r;
    }

    // The server hook and the screens; neither may hold up or break the local record.
    private static void Tell(ConsentRecord r)
    {
        try { Changed?.Invoke(); } catch (Exception) { }
        if (ServerRecord is not { } hook) return;
        _ = Task.Run(async () =>
        {
            try { await hook(r.Kind, r.Version, r.Gpu, r.App, r.Withdrawn); }
            catch (Exception) { }
        });
    }
}
