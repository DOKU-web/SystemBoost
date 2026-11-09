// What the account page and the plan window say about the plan in use.
using System;

namespace SystemBoost.App.Services;

public static class PlanStatus
{
    private static LicenseInfo? Key => License.Info is { } i && (i.Expires == null || i.Expires > DateTime.UtcNow) ? i : null;

    /// Pro adds only Windows features, so a Mac is offered up to Ultra.
    public static int TopRank => Platform.IsWindows ? 3 : 2;
    public static int Rank => License.IsPro ? 3 : License.IsUltra ? 2 : License.IsPremium ? 1 : 0;
    public static string Name => Rank switch { 3 => "프로", 2 => "울트라", 1 => "프리미엄", _ => "무료" };
    public static string Multipliers => Rank switch { >= 2 => "자동 · 2 · 3 · 4 · 6 · 8배", 1 => "자동 · 2 · 3 · 4배", _ => "2배" };

    /// A key with an end date that does not renew by itself.
    public static bool CanExtend => Key is { Expires: not null, Sub: null };

    public static string How =>
        Rank == 0 ? "키 없이 쓰는 중이에요"
        : Key == null ? "스팀 DLC"
        : Key.Sub != null ? "카드 구독 · 기간마다 자동으로 결제돼요"
        : License.How switch
        {
            "paid" => "결제로 받은 키",
            "referral" => "추천 코드로 받은 키",
            "invite" => "초대 코드로 받은 키",
            "reward" => "초대 보상으로 받은 키",
            _ => "등록한 라이선스 키",
        };

    public static string Period
    {
        get
        {
            if (Key?.Expires is not DateTime end) return "기간 제한 없음";
            var days = (int)Math.Ceiling((end - DateTime.UtcNow).TotalDays);
            return $"{end.ToLocalTime():yyyy-MM-dd}까지 · {days}일 남음";
        }
    }
}
