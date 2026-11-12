// The end-user licence agreement: which version this PC accepted, and when.
// The text ships inside the app (Assets/eula.txt). When its meaning changes, raise
// Version so everyone is asked again. The record stays on this PC; nothing is sent.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace SystemBoost.App.Services;

public static class Eula
{
    // Not yet shipped (0.8.11 asks for "2026-11-09"): Articles 1, 3.2/3.3/3.10/3.11, 8, 9.2/9.5 and 10.3, in force 2027-01-01.
    // server/license-worker/src/purchase_note.js quotes this version on the /thanks page; test_purchase_note.mjs compares them.
    public const string Version = "2026-11-12";

    private sealed record Acceptance(string Version, DateTimeOffset AcceptedAt, string AppVersion);

    private static readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Acceptance? _current = Read();

    static Eula()
    {
        if (Accepted) Done.TrySetResult();
    }

    private static string RecordFile => Path.Combine(Platform.DataDir, "eula.json");

    public static bool Accepted => _current?.Version == Version;
    public static DateTimeOffset? AcceptedAt => Accepted ? _current!.AcceptedAt : null;

    // Whatever must not run before the agreement waits on this.
    public static Task WhenAccepted => Done.Task;

    public static void Accept()
    {
        _current = new Acceptance(Version, DateTimeOffset.Now, Updater.CurrentVersion);
        try
        {
            var tmp = RecordFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_current));
            File.Move(tmp, RecordFile, true);
        }
        catch (Exception) { }   // a read-only folder asks again next time; it must not lock the user out
        Done.TrySetResult();
    }

    public static string[] Lines()
    {
        // The Korean text is the agreement; the English one says so on its first line.
        using var stream = AssetLoader.Open(new Uri($"avares://SystemBoost/Assets/eula{(Lang.Translating ? ".en" : "")}.txt"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }

    private static Acceptance? Read()
    {
        try { if (File.Exists(RecordFile)) return JsonSerializer.Deserialize<Acceptance>(File.ReadAllText(RecordFile)); }
        catch (Exception) { }   // a damaged record counts as not accepted
        return null;
    }
}
