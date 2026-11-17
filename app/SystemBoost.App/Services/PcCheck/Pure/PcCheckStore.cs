// PC 맞춤 추천: the files it keeps, all on this PC in the data folder (the paths are passed in, so the tests use a
// folder of their own):
//   pc-check.json           the last report (PcCheckReport)
//   pc-check-applied.json   what "선택한 추천 한 번에 적용" changed and what was there before (PcApplyRecord, PcApplyPlan.cs)
//   fg-choice.json          the frame generation choice the FrameGen page starts from (FgChoice)
// Each is written whole to <file>.tmp and moved over the file (one step), so a crash leaves the old file or the new one.
// A missing or unreadable file reads as null; nothing here throws on reading.
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemBoost.App.Services.PcCheck;

public static class PcCheckStore
{
    public const string ReportFile = "pc-check.json", AppliedFile = "pc-check-applied.json", FgChoiceFile = "fg-choice.json";

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static PcCheckReport? LoadReport(string path) => Read<PcCheckReport>(path) is { Specs: not null, Bench: not null } r
        ? r with { Recommendations = r.Recommendations ?? Array.Empty<Recommendation>() }
        : null;

    public static void SaveReport(string path, PcCheckReport report) => Write(path, report);

    public static PcApplyRecord? LoadApplied(string path) => Read<PcApplyRecord>(path) is { } r
        ? r with { Boosts = r.Boosts ?? Array.Empty<string>() }
        : null;

    /// An empty record removes the file: nothing is left to undo.
    public static void SaveApplied(string path, PcApplyRecord record)
    {
        if (record.IsEmpty) Delete(path);
        else Write(path, record);
    }

    public static FgChoice? LoadFgChoice(string path) => Read<FgChoice>(path) is { Mode: not null, Quality: not null } c ? c : null;

    public static void SaveFgChoice(string path, FgChoice choice) => Write(path, choice);

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// Throws when the file could not be written (the old one is then as it was).
    private static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
