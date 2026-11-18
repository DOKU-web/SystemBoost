// The app's side of AmdTerms (AmdTerms.cs): the records in consents.json through OcConsent.Store (one store, one lock
// for the file), the agreement version (Eula.Version), and Article 12-2's text from the agreement shipped in the app.
// Only builds with the Radeon driver features (AmdBuild.DriverFeatures) ever ask: without ADLX nothing here is used,
// and those builds look as before.
using System;
using System.Collections.Generic;

namespace SystemBoost.App.Services;

public static partial class AmdTerms
{
    private static readonly object Gate = new();
    private static bool? _agreed;

    /// A consent to Article 12-2 of the current agreement is on record. Read from consents.json once, then kept
    /// until this app records an agreement or a withdrawal.
    public static bool AgreedNow
    {
        get
        {
            lock (Gate) return _agreed ??= Agreed(OcConsent.Store.Load(), Eula.Version);
        }
    }

    /// What AmdHost.AmdTermsOk asks before every AMD command: false while the separate consent is required.
    /// Builds without ADLX: always true (their helper answers "off" as before).
    public static bool Ok(DateTime nowUtc) => !AmdBuild.DriverFeatures || InForce(nowUtc) || AgreedNow;

    /// The separate consent is required now (an ADLX build, before 2027-01-01 KST, no valid consent).
    public static bool RequiredNow => !Ok(DateTime.UtcNow);

    /// '동의 철회' is offered (an ADLX build, before 2027-01-01 KST, a consent in force).
    public static bool WithdrawableNow => AmdBuild.DriverFeatures && !InForce(DateTime.UtcNow) && AgreedNow;

    /// Records the consent now. Throws when the file could not be written (nothing changed then).
    public static void RecordAgreement()
    {
        lock (Gate) _agreed = null;
        Agree(OcConsent.Store, Eula.Version, OcConsent.AppVersion, DateTime.UtcNow);
        lock (Gate) _agreed = null;   // read back from the file, which now holds the record
    }

    /// Records the withdrawal now. Throws when the file could not be written (the consent then stays as it was).
    public static void RecordWithdrawal()
    {
        lock (Gate) _agreed = null;
        Withdraw(OcConsent.Store, Eula.Version, OcConsent.AppVersion, DateTime.UtcNow);
        lock (Gate) _agreed = null;
    }

    /// Article 12-2 in the screen's language (the Korean text is the agreement; the English one is its reference
    /// translation, as the licence agreement window shows it). Empty when it could not be read.
    public static IReadOnlyList<string> Text()
    {
        try { return Section(Eula.Lines()); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// One line on the consent on record, for the read-only window.
    public static string Standing()
    {
        if (Latest(OcConsent.Store.Load()) is not { } last) return "아직 이 조건에 동의하지 않았어요.";
        var when = last.AtUtc is DateTime t ? t.ToLocalTime().ToString(Lang.Translating ? "yyyy-MM-dd HH:mm" : "yyyy년 M월 d일 HH:mm") : last.At;
        if (last.Withdrawn) return $"{when}에 동의를 철회했어요.";
        return $"{when}에 이 조건에 동의했어요.";
    }
}
