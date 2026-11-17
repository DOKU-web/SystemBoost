// The app's side of OcConsent (OcConsent.cs): consents.json in Platform.DataDir, the check before every tuning write,
// and the window that asks.
// Asked before: the GPU page's '오버클럭 / 전력 제한' switch (NVIDIA and AMD; the AMD tuning values are applied by it) and
// the AMD auto tuning buttons (Pages/GpuCards.AskConsentAsync). A preset with overclock values calls
// EnsureAsync(owner, gpu, fromPreset: true) before it puts them in, which always asks.
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using SystemBoost.App.Pages;

namespace SystemBoost.App.Services;

public static partial class OcConsent
{
    private static ConsentStore? _store;

    /// consents.json in Platform.DataDir.
    public static ConsentStore Store => _store ??= new ConsentStore(Path.Combine(Platform.DataDir, FileName));

    public static string AppVersion => Updater.CurrentVersion.Split('+')[0];

    /// Why the last EnsureAsync gave false although the box was ticked (the record could not be written); null otherwise.
    public static string? Problem { get; private set; }

    /// A consent to the current notice for this card is on record: what every tuning write checks first.
    public static bool Valid(string gpu) => Needs(Store.Load(), gpu, fromPreset: false) == ConsentNeed.None;

    /// '동의 철회' is offered.
    public static bool InForce() => InForce(Store.Load());

    /// Shows the notice when it is needed (no consent, withdrawn, another version, another card) and always when
    /// fromPreset (a preset with overclock values is about to be put in). True when a valid consent is on record
    /// afterwards; false when the user cancelled (or the record could not be written: Problem says why).
    /// Call on the UI thread.
    public static async Task<bool> EnsureAsync(Window owner, string gpuName, bool fromPreset)
    {
        Problem = null;
        var need = Needs(Store.Load(), gpuName, fromPreset);
        if (need == ConsentNeed.None) return true;
        if (!await new OverclockConsentDialog(ask: true, WhyAgain(need)).ShowDialog<bool>(owner)) return false;
        try
        {
            Agree(Store, gpuName, AppVersion, DateTime.UtcNow);
            return true;
        }
        catch (Exception e)
        {
            Problem = "동의 기록을 저장하지 못해서 켜지 않았어요: " + e.Message;
            return false;
        }
    }

    /// '동의 철회': the withdrawal on record (and the server hook told). Throws when the file could not be written.
    /// The GPU page then turns gpu.oc off and puts the card back (WindowsBoostBackend.WithdrawOcAsync).
    public static void RecordWithdrawal(string gpu) => Withdraw(Store, gpu, AppVersion, DateTime.UtcNow);

    /// '자세히 보기': the notice without the agree button, with how the consent stands.
    public static Task ShowAsync(Window owner) => new OverclockConsentDialog(ask: false, Standing()).ShowDialog(owner);

    /// One line on the consent on record, for the read-only notice.
    public static string Standing()
    {
        if (Latest(Store.Load()) is not { } last) return "아직 이 안내에 동의하지 않았어요.";
        var when = last.AtUtc is DateTime t ? t.ToLocalTime().ToString(Lang.Translating ? "yyyy-MM-dd HH:mm" : "yyyy년 M월 d일 HH:mm") : last.At;
        if (last.Withdrawn) return $"{when}에 동의를 철회했어요.";
        var line = $"{when}에 이 안내에 동의했어요 ({last.Gpu})";
        return last.Version == OverclockNotice.Version ? line : line + " · 안내 문구가 바뀌어서 다음에 켤 때 다시 동의를 받아요.";
    }
}
