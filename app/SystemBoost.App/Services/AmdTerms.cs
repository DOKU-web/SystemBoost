// The separate consent to Article 12-2 of the licence agreement (제12조의2, AMD 구성 요소: the terms AMD's ADLX licence
// asks for) before the Radeon driver features may run. Article 12-2 is in the 2026-11-20 agreement, but for someone who
// agreed to an earlier version it binds only from 2027-01-01 00:00 KST (Article 13: 30 days' notice), so until then a
// Radeon user agrees to it on its own first. From that instant on no separate consent is needed: everyone on this
// version has accepted the agreement that holds it.
// Kept in consents.json (ConsentStore, the same file and store as OcConsent) as records of kind "amd-terms":
//   {"kind":"amd-terms","version":"2026-11-20","at":"2026-11-18T03:00:00Z","gpu":"","app":"0.8.18","withdrawn":false}
// version is the agreement version (Eula.Version) whose Article 12-2 was shown; the latest record of the kind decides,
// and only a consent to the current agreement version counts. Nothing leaves this PC (no server hook).
// The app's side (the store, Eula.Version, the text from eula.txt) is in AmdTerms.App.cs; the window in Pages/AmdTermsDialog.cs.
// No Avalonia here: the GPU tests link this file.
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemBoost.App.Services;

public static partial class AmdTerms
{
    public const string Kind = "amd-terms";

    /// 2027-01-01 00:00 KST, when Article 12-2 binds everyone (the revision date in eula.txt). Not to be moved earlier.
    public static readonly DateTime InForceUtc = new(2026, 12, 31, 15, 0, 0, DateTimeKind.Utc);

    /// The headings the section starts with: eula.txt, and the reference translation eula.en.txt.
    public static readonly string[] Headings = { "## 제12조의2", "## Article 12-2" };

    /// A consent was given or withdrawn. May fire on any thread.
    public static event Action? Changed;

    /// The latest record of this kind, null when there is none.
    public static ConsentRecord? Latest(IEnumerable<ConsentRecord> records) => records.LastOrDefault(r => r.Kind == Kind);

    /// A consent to Article 12-2 of this agreement version is on record and not withdrawn.
    public static bool Agreed(IEnumerable<ConsentRecord> records, string version) =>
        Latest(records) is { Withdrawn: false } last && last.Version == version;

    /// Article 12-2 binds everyone at this instant (2027-01-01 KST or later). An unspecified kind counts as UTC.
    public static bool InForce(DateTime nowUtc) => Utc(nowUtc) >= InForceUtc;

    /// The separate consent must be given before any ADLX feature runs: before 2027-01-01 KST, with no valid consent.
    public static bool Required(DateTime nowUtc, IEnumerable<ConsentRecord> records, string version) =>
        !InForce(nowUtc) && !Agreed(records, version);

    /// '동의 철회' is offered: a consent is in force and the date has not come yet (after it, withdrawing changes nothing).
    public static bool Withdrawable(DateTime nowUtc, IEnumerable<ConsentRecord> records, string version) =>
        !InForce(nowUtc) && Agreed(records, version);

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);

    /// Records the consent to Article 12-2 of this agreement version. Throws when the file could not be written.
    public static ConsentRecord Agree(ConsentStore store, string version, string app, DateTime utcNow)
    {
        var r = new ConsentRecord { Kind = Kind, Version = version ?? "", At = OcConsent.Stamp(utcNow), App = app ?? "" };
        store.Append(r);
        Tell();
        return r;
    }

    /// Records the withdrawal. Throws when the file could not be written.
    public static ConsentRecord Withdraw(ConsentStore store, string version, string app, DateTime utcNow)
    {
        var last = Latest(store.Load());
        var r = new ConsentRecord
        {
            Kind = Kind, Version = last?.Version is { Length: > 0 } v ? v : version ?? "", At = OcConsent.Stamp(utcNow),
            App = app ?? "", Withdrawn = true,
        };
        store.Append(r);
        Tell();
        return r;
    }

    private static void Tell()
    {
        try { Changed?.Invoke(); } catch (Exception) { }
    }

    /// The lines of Article 12-2: from its heading ("## 제12조의2" or "## Article 12-2") up to the next "## " heading,
    /// without the empty lines at the end. Empty when the heading is not there.
    public static IReadOnlyList<string> Section(IReadOnlyList<string> lines)
    {
        int start = -1;
        for (int i = 0; i < lines.Count && start < 0; i++)
            if (Headings.Any(h => lines[i].StartsWith(h, StringComparison.Ordinal))) start = i;
        if (start < 0) return Array.Empty<string>();
        int end = start + 1;
        while (end < lines.Count && !lines[end].StartsWith("## ", StringComparison.Ordinal)) end++;
        while (end > start + 1 && lines[end - 1].Trim().Length == 0) end--;
        return lines.Skip(start).Take(end - start).ToList();
    }
}
