// Catalog of system boost features shown in the boost pages.
// The actual apply/revert logic lives in SystemBoost v0.5 (WPF app + admin helper).
// Hook it up by implementing IBoostBackend (see V05Backend) - the UI already
// calls Apply/Revert/IsApplied and shows results.
// macOS: these features have no macOS equivalent and are hidden there.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public enum BoostCategory { GameBoost, Ram, Gpu, Cpu, Network, System }

public sealed record BoostFeature(string Id, BoostCategory Category, string Title, string Description,
                                  bool RequiresAdmin, bool WindowsOnly = true, bool Premium = false,
                                  bool NeedsReboot = false, bool MacOnly = false);

public interface IBoostBackend
{
    bool Connected { get; }
    Task<bool> IsAppliedAsync(string featureId);
    /// <summary>Applies the change after backing up the previous value. Returns a short result text.</summary>
    Task<string> ApplyAsync(string featureId);
    Task<string> RevertAsync(string featureId);
    Task<string> RevertAllAsync();
}

/// <summary>Placeholder until the v0.5 logic is moved in. Never changes the system.</summary>
public sealed class V05Backend : IBoostBackend
{
    public bool Connected => false;
    public Task<bool> IsAppliedAsync(string featureId) => Task.FromResult(false);
    public Task<string> ApplyAsync(string featureId) => Task.FromResult("v0.5 기능 연결 전이라 아무것도 바꾸지 않았어요");
    public Task<string> RevertAsync(string featureId) => Task.FromResult("변경된 것이 없어요");
    public Task<string> RevertAllAsync() => Task.FromResult("변경된 것이 없어요");
}

public static class BoostCatalog
{
    public static readonly IReadOnlyList<BoostFeature> All = new List<BoostFeature>
    {
        // game boost
        new("boost.oneclick", BoostCategory.GameBoost, "원클릭 게임 부스트", "선택한 프로필의 항목을 한 번에 적용, 게임 종료 시 자동 복구", true),
        new("boost.profiles", BoostCategory.GameBoost, "게임별 자동 최적화", "항목마다 측정해서 효과가 확인된 것만 켜요", false, Premium: true),
        // RAM
        new("ram.standby", BoostCategory.Ram, "대기 메모리 정리", "여유 메모리가 설정값 아래로 떨어질 때만 실행", true),
        new("ram.workingset", BoostCategory.Ram, "백그라운드 작업 집합 정리", "게임 중, 고른 프로그램만 대상", true),
        // GPU
        new("gpu.highperf", BoostCategory.Gpu, "게임을 고성능 GPU로 지정", "노트북·듀얼 GPU", false),
        new("gpu.hags", BoostCategory.Gpu, "하드웨어 가속 GPU 스케줄링", "상태 확인 후 설정", true, NeedsReboot: true),
        new("gpu.driver", BoostCategory.Gpu, "드라이버 게임 프로필", "NVIDIA·AMD·Intel 공식 API 범위 안에서", true),
        new("gpu.oc", BoostCategory.Gpu, "오버클럭 / 언더볼트", "온도 초과·드라이버 오류 시 자동 복구, 기본 꺼짐", true, Premium: true),
        // CPU
        new("cpu.priority", BoostCategory.Cpu, "게임 우선순위 '높음'", "'실시간'은 쓰지 않아요", false),
        new("cpu.ecores", BoostCategory.Cpu, "백그라운드를 E코어로", "하이브리드 CPU, 방송·음성 프로그램은 제외", false),
        new("cpu.power", BoostCategory.Cpu, "최고 성능 전원 계획", "게임 종료 시 원래 계획으로", true),
        // network
        new("net.monitor", BoostCategory.Network, "대용량 다운로드 감지", "게임 중 다른 프로그램의 큰 전송을 알려요", false),
        new("net.adapterpower", BoostCategory.Network, "네트워크 어댑터 절전 끄기", "", true),
        // system
        new("sys.gamemode", BoostCategory.System, "Windows 게임 모드", "", false),
        new("sys.dvr", BoostCategory.System, "Xbox 백그라운드 녹화 끄기", "", false),
        new("sys.focus", BoostCategory.System, "게임 중 알림·업데이트 일시정지", "", false),
        // macOS (MacBoostBackend)
        new("mac.lowpower", BoostCategory.GameBoost, "저전력 모드 끄기", "켜져 있으면 CPU·GPU 성능이 크게 낮아져요", true, WindowsOnly: false, MacOnly: true),
        new("mac.tm", BoostCategory.GameBoost, "Time Machine 자동 백업 멈추기", "게임 중 디스크·CPU를 쓰는 백업을 멈춰요. 끝나면 꼭 되돌리세요", true, WindowsOnly: false, MacOnly: true),
        new("mac.spotlight", BoostCategory.GameBoost, "Spotlight 색인 멈추기", "게임 설치·업데이트 직후 색인 부하를 줄여요. 되돌리면 다시 색인해요", true, WindowsOnly: false, MacOnly: true),
    };

    public static IEnumerable<BoostFeature> For(BoostCategory c) =>
        All.Where(f => f.Category == c && (!f.WindowsOnly || OperatingSystem.IsWindows()) &&
                       (!f.MacOnly || OperatingSystem.IsMacOS()));

    public static bool CategoryAvailable(BoostCategory c) => For(c).Any();
}
