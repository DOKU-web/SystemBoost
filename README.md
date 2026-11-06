# SystemBoost 0.7 (Windows + macOS)

게임 최적화 + 화면 캡처 방식 프레임 생성·업스케일 프로그램. 게임에 주입하지 않습니다.

## 구성

```
app/SystemBoost.App/        공통 화면 (Avalonia, .NET 8) - Windows·macOS 같은 코드
  Pages/                    프레임 생성(동작), 대시보드, 부스트, 벤치마크, 백업, 설정
  Services/FrameGenEngine   OS에 맞는 엔진 실행 + JSON 통신
  Services/BoostCatalog     부스트 기능 목록 + IBoostBackend
  Services/WindowsBoostBackend  Windows 부스트 구현 (백업·적용·되돌리기·게임 감시)
  Services/BoostHelper      관리자 도우미 실행 + 파이프 통신
native/framegen/            Windows 엔진 (C++ / D3D11)  - README_FRAMEGEN.md 참고
native/framegen-mac/        macOS 엔진 (Swift / Metal / ScreenCaptureKit)
native/helper/              관리자 도우미 sb_helper.exe (C++, 명령 허용 목록)
tools/gen_metal.sh          HLSL -> Metal 셰이더 생성 (두 OS가 같은 알고리즘 사용)
build/windows/              build.ps1 (Velopack 설치판 + 포터블 + 업데이트 패키지)
build/macos/                build.sh, Info.plist, entitlements (Velopack .pkg + 업데이트 패키지)
.github/workflows/build.yml 테스트 + Windows·macOS 빌드 + 태그 시 릴리스·업데이트 배포
RELEASE_NOTES.md            다음 버전의 바뀐 점 (앱 업데이트 화면과 릴리스에 그대로 표시)
site/download.html          다운로드 페이지 (OS 자동 감지 + 직접 선택)
```

## 배포 파일 (다운로드할 때 선택)

| 파일 | 설명 |
|---|---|
| `SystemBoost-Windows-Setup.exe` | 설치판. 관리자 권한 없이 설치되고 바로 실행, 바로가기·제거 포함 |
| `SystemBoost-Windows-Portable.zip` | 포터블. 압축 풀고 `SystemBoost.exe` 실행. 설정·기록은 그 폴더(`data\`)에 저장 |
| `SystemBoost-macOS-AppleSilicon.pkg` | M1 이후 Mac 설치 파일 |
| `SystemBoost-macOS-Intel.pkg` | Intel Mac 설치 파일 |

파일 이름에 버전이 없어서 다운로드 페이지는 항상 최신 릴리스를 가리킵니다(`releases/latest/download/...`).
네 가지 모두 **자동 업데이트**됩니다(아래).

모두 .NET 런타임이 포함된 자체 실행형이라 따로 설치할 것이 없습니다.
스팀에 올릴 때는 Windows 깊이(depot)에 설치판 대신 `dist\windows\SystemBoost` 폴더를, macOS 깊이에 `SystemBoost.app`을 올리면 스팀이 OS에 맞는 것을 자동으로 내려줍니다.

## 업데이트 내보내기 (자동 업데이트)

1. `app/SystemBoost.App/SystemBoost.App.csproj`의 `<Version>`을 올리고 `RELEASE_NOTES.md`에 바뀐 점을 적습니다.
2. 커밋하고 태그를 올립니다: `git tag v0.7.0 && git push origin main v0.7.0`
3. 끝. GitHub Actions가 빌드 → Velopack으로 포장(이전 버전과 바뀐 부분만 담은 델타 포함) → 릴리스 게시까지 합니다.

사용자 쪽에서는 (`Services/Updater.cs`)
- 앱이 켜지고 15초 뒤, 그다음 6시간마다 새 버전을 확인하고 백그라운드에서 받아 둡니다.
- 받아지면 화면 위에 '새 버전 준비됨' 줄이 뜨고, **지금 다시 시작** 또는 **끌 때 적용**을 고릅니다.
  아무것도 안 해도 앱을 끌 때 조용히 적용돼요. 게임 중에 창을 띄우거나 강제로 재시작하지 않습니다.
- 설정 → 업데이트: 현재 버전, 지금 확인, 바뀐 점, 자동 확인 끄기.
- 설정·라이선스 키·기록은 앱 폴더 밖(포터블은 `data\`)에 있어서 업데이트해도 그대로입니다.
- 스팀판은 스팀이 업데이트하므로 이 기능이 꺼져 있습니다(Velopack 설치본이 아니면 아무것도 안 함).

확인 필요: Velopack은 첫 CI 빌드에서 처음 돌아갑니다. `vpk` 결과 파일 이름(`*-Setup.exe`, `*-Portable.zip`, `*-Setup.pkg`)과
업로드 결과를 첫 태그 릴리스에서 확인하세요. 저장소가 비공개면 업데이트 확인에 토큰이 필요하니 공개 저장소(또는 공개 릴리스 전용 저장소)를 쓰세요.

## 빌드

- **자동 (권장)**: GitHub에 올리면 `.github/workflows/build.yml`이 테스트 → Windows 빌드 → macOS 빌드를 돌리고 결과 파일을 Actions 아티팩트로 남깁니다. `v0.6.0` 태그를 붙이면 릴리스와 자동 업데이트까지 배포됩니다(위).
- **Windows 수동**: `dotnet tool install -g vpk --version 0.0.1298` 후 `powershell -ExecutionPolicy Bypass -File build\windows\build.ps1`
- **macOS 수동**: `build/macos/build.sh`

서명: Windows는 `SIGN_CERT_THUMBPRINT`, macOS는 `SIGN_IDENTITY` + `INSTALL_IDENTITY` + `NOTARY_PROFILE` 환경 변수(CI에서는 저장소 Secrets `MAC_SIGN_IDENTITY` 등)를 넣으면 서명·공증까지 합니다. 서명하지 않으면 Windows SmartScreen, macOS Gatekeeper 경고가 뜹니다.

## 지금 상태 (솔직하게)

| 부분 | 상태 |
|---|---|
| 프레임 생성 알고리즘 + 셰이더 | 참조 구현으로 측정, 실제 셰이더 실행 결과와 일치 확인 |
| Windows 엔진 (C++) | mingw로 경고 없이 빌드됨. MSVC·실제 PC 실행은 아직 |
| macOS 엔진 (Swift) | 동작. GitHub macOS 14 가상 장비에서 설치·실행·프레임 생성을 확인 (`mac-smoke.yml`). **실제 Mac·실제 게임은 아직** |
| 공통 화면 (Avalonia) | C# 문법 검사만 통과. **처음 빌드는 CI** (이 작업 환경은 NuGet이 막혀 있었음) |
| Windows 게임 부스트 | 동작. 값이 바뀌고 되돌아가는 것은 재서 확인. **게임 FPS 효과는 아직 재지 않음**, 실제 게임·안티치트와 함께 쓴 시험도 없음 |
| 관리자 도우미 | 새로 작성 (`native/helper`). `build.ps1`이 빌드해 앱 폴더에 넣음 |
| GPU 오버클럭 / 전력 제한 | 동작(NVIDIA, 공식 NVML). 값 변경·유지·시간 초과·온도 초과·앱 종료·강제 종료 복구를 재서 확인. **실제 드라이버 오류로 복구되는 경로는 시험하지 못함** (오류 기록 조회만 확인) |
| 게임별 자동 최적화 | 동작. 프레임 측정과 자동 적용은 시험 프로그램으로 확인. **실제 게임에서는 아직 재지 않음** |
| 단축키 | 아직 없음 (전역 단축키는 OS별 구현 필요) |
| macOS 윈도우 게임 실행 | 작성만 함. Mac에서 설치·실행 확인 필요 |
| macOS 게임 부스트 | 작성만 함. Mac에서 관리자 창·되돌리기 확인 필요 |

## Windows: 게임 부스트
`Services/WindowsBoostBackend.cs`. v0.5 소스가 없어서 새로 작성했습니다. 게임에 주입하지 않고 게임 프로세스를 열지도 않습니다. 게임은 프로세스 이름으로만 알아봅니다('내 게임' 목록, 또는 프레임 생성 중인 게임).

바꾸기 전 값을 `boost-state.json`에 먼저 저장합니다. 앱이 강제로 꺼져도 다음 실행 때 게임 시간 항목을 되돌립니다.

| 종류 | 항목 | 되돌리는 때 |
|---|---|---|
| 끌 때까지 유지 | `gpu.highperf` `gpu.hags` `gpu.driver` `cpu.priority` `net.adapterpower` `sys.gamemode` `sys.dvr` | 스위치, 전체 되돌리기 |
| 게임 중에만 | `ram.standby` `ram.workingset` `cpu.ecores` `cpu.power` `net.monitor` `sys.focus` | 게임 종료, 앱 종료, 다음 실행 |

- `cpu.ecores`: 게임 중 다른 프로그램의 코어 마스크를 E코어로 제한. 앞에 있는 창, 방송·음성·런처, 시스템 프로그램은 옮기지 않음.
- `cpu.priority`: `Image File Execution Options\...\PerfOptions\CpuPriorityClass`. 다음 실행부터 Windows가 적용.
- `gpu.driver`: NVAPI 드라이버 프로필. AMD·Intel은 미지원.
- `sys.focus`: 알림 끄기 + Windows 업데이트 일시정지(최대 1일).
- `net.monitor`: 큰 전송 알림. 프로그램 이름은 관리자 도우미가 있을 때 TCP 연결에서만 찾음(QUIC·UDP는 이름 없음).

### GPU 오버클럭 / 전력 제한 (`gpu.oc`, 프리미엄)
`Services/WindowsBoostBackend.Tuning.cs`, `native/helper/src/gpuoc.cpp`. NVIDIA만, 공식 NVML만 씁니다. 전압은 공개 API로 바꿀 수 없어서 언더볼트 대신 전력 제한을 줍니다. 기본 꺼짐, 앱이 켜져 있는 동안만 적용됩니다.
- 범위: 코어 -200~+100 MHz, 메모리 -500~+300 MHz, 전력은 드라이버가 알려 주는 범위 안 (`validate.h`).
- 도우미가 스스로 되돌리는 경우: 30초 안에 `유지`를 안 누름, 온도가 기준(기본 85 °C) 이상으로 3초, NVML 읽기 실패 또는 시스템 로그의 드라이버 오류(Display 4101, nvlddmkm 13/14/153), 앱 종료·강제 종료.
- 도우미마저 강제로 죽으면 저장된 백업으로 다음 실행 때 되돌립니다.

### 게임별 자동 최적화 (`boost.profiles`, 프리미엄)
`native/helper/src/fpsmeter.cpp`. 게임의 프레임을 Windows 이벤트 추적(ETW)으로 잽니다. 게임을 열거나 건드리지 않습니다.
- 끔, 켬, 켬, 끔 순서로 항목마다 15초씩 재서, 평균이 3% 이상 오르고 측정 오차의 2배를 넘고 1% 저점이 3% 넘게 떨어지지 않은 항목만 그 게임에 저장합니다.
- 끄고 켤 수 있는 `cpu.power` `cpu.ecores`만 잽니다. 나머지는 다음 실행부터 적용되거나 재시작이 필요해서 비교할 수 없습니다.

### 관리자 도우미 (`native/helper`)
앱은 일반 권한으로 돌고, 관리자 작업이 필요할 때만 UAC로 `sb_helper.exe`를 띄웁니다. 앱이 꺼지면 도우미도 꺼집니다.
- 임의 레지스트리 쓰기나 명령 실행은 없고, `commands.cpp`의 허용 목록만 처리합니다(`validate.h`가 값 형식을 검사).
- 무작위 이름 파이프, 원격 클라이언트 거부, 앱 사용자만 접근 가능, 호출자 PID와 실행 파일(`SystemBoost.exe`, 도우미와 같은 폴더) 확인.
- 설치 폴더가 사용자 쓰기 가능하고 서명 전이라, 같은 사용자 권한의 악성 프로그램이 UAC 전에 파일을 바꿀 수는 있습니다. 코드 서명을 하면 UAC에 발행자가 보입니다.
- NVAPI는 CMake가 고정된 커밋과 SHA-256으로 내려받습니다.

## macOS: 윈도우 게임 실행 (Wine)
- 메뉴 "🍷 윈도우 게임"에서 실행 환경을 설치합니다. Wine·DXVK는 처음 쓸 때 GitHub 릴리스에서 내려받고, `Assets/wine-manifest.json`의 SHA-256과 다르면 설치하지 않습니다. CI(macOS 빌드)가 `tools/update_wine_manifest.sh`로 매니페스트를 최신 릴리스 기준으로 갱신합니다.
- 그래픽 방식: DXVK(DirectX 10·11, 기본) / WineD3D(DirectX 9 이하) / D3DMetal(DirectX 11·12 — 사용자가 Apple Game Porting Toolkit을 직접 받아 폴더를 지정. SystemBoost는 배포하지 않음).
- Apple Silicon은 Rosetta 2가 필요하고, 앱에서 Apple 설치 명령을 열어 줍니다.
- 설치 프로그램(.exe)이나 Windows용 Steam을 보틀 안에 설치하고, "설치된 게임 찾기"로 목록에 추가합니다. "실행 + 프레임 생성"으로 게임을 켠 뒤 그 창에 프레임 생성을 겁니다.
- **안 되는 것**: 커널 안티치트(EasyAntiCheat, BattlEye, Vanguard 등) 게임은 대부분 실행되지 않습니다. 스타시티즌도 EAC를 써서 실행되지 않을 가능성이 높습니다. 게임마다 호환성이 다르고 윈도우 PC보다 느릴 수 있습니다.
- 확인 필요: Wine 빌드에 MoltenVK가 들어 있는지(없으면 DXVK가 동작하지 않고 WineD3D로 실행), 컷신 재생용 GStreamer 필요 여부. 실제 Mac에서 확인해야 합니다.
- 서드파티 라이선스: `THIRD_PARTY_NOTICES.md`

## 가상 메모리 (Windows, RAM 확보 메뉴)
`Services/VirtualMemory.cs`, `Pages/VirtualMemoryCard.cs`. 설치된 RAM·지금 설정·드라이브 남은 공간을 보여 주고, 자동(Windows) / 권장(게임용: RAM만큼 시작, 최대 2배, 드라이브에 15GB 또는 10%는 남김) / 직접 입력 중 하나를 골라 관리자 확인 한 번으로 저장합니다. 처음 설정을 백업해 두고 '원래대로'로 되돌립니다. 다시 시작해야 적용되고, '지금 다시 시작'은 두 번 눌러야 실행됩니다.
- 효과: 메모리 부족으로 게임이 튕기는 것을 막음. RAM이 늘거나 FPS가 오르지는 않음(앱 문구에도 그렇게 적음).
- macOS는 스왑을 OS가 관리하고 앱이 바꿀 수 없어서 없음.
- 확인 필요: 실제 PC에서 저장 후 재부팅 시 적용되는지(레지스트리 PagingFiles + AutomaticManagedPagefile).

## 프리미엄
가격: 월간 ₩2,900 · 연간 ₩19,900 · 평생 ₩49,900 · 스팀 DLC ₩14,000, 울트라 월간 ₩4,900 · 연간 ₩34,900 · 평생 ₩89,900 (PremiumPage.cs 상수, 대시보드 문구, 결제 페이지 가격과 맞출 것).
결제·키 발급·관리자 대시보드: `server/license-worker/README.md`.
- **결제 준비 전에는 키가 나가지 않음**: 앱의 '결제하기'는 대시보드에 결제 링크가 있는 플랜만 켜지고(없으면 '결제 준비 중'),
  키는 판매자가 대시보드에서 결제를 확인하고 발급할 때만 나옴. 자동 결제(Lemon Squeezy) 판매 모드 기본값도 '꺼짐'.
`Services/License.cs`, `Pages/PremiumPage.cs`. 무료: 프레임 생성 2배, 업스케일, 게임 부스트·모니터링·벤치마크, 자원 분배(그래프·우선/낮춤). 프리미엄: 자동·3·4배, 품질 모드, 듀얼 GPU, CPU 최대 사용량 제한, 게임별 자동 최적화, 오버클럭. 잠긴 기능을 누르면 안내가 뜨고 선택이 되돌아갑니다.
- 울트라: 프리미엄 전체 + 프레임 생성 6·8배(고정 배수, 자동 최대 배수). 키의 `plan`이 `ultra`일 때만 열림(`License.IsUltra`). 대시보드 '키 직접 발급'에서 등급을 고름. 0.8.4 전 앱은 울트라 키를 프리미엄으로 받음
- 스팀판: 'SystemBoost Premium' DLC → `License.Steam`(ISteamOwnership)에 Steamworks.NET 연결
- 직접 배포판: 웹 결제 후 라이선스 키(ECDSA P-256 서명) 입력. 앱에는 공개키만 들어감
- **출시 전에 채울 것**: `License.cs`의 `ServerUrl`(라이선스 서버 배포 주소), `PremiumPage.cs`의 가격 문구, 대시보드의 결제 링크
- 서명 키: `tools/license_tool.py`로 만든 공개 키가 `License.cs`에 들어 있습니다. 짝이 되는 **비밀 키(systemboost-license-private.pem)는 저장소에 없고** 개발자가 따로 보관합니다(.gitignore로 막음).
- 키 발급: `python3 tools/license_tool.py sign <private.pem> <이메일> [--days 30]` (기간 없으면 평생). 결제 서버도 같은 방식으로 서명하면 됩니다.

## 자원 분배 (CPU·GPU·메모리)
`Services/ResourceManager.cs`, `Pages/ResourcePage.cs` — 앱별 사용량을 쌓인 그래프로 보여 주고, 앱마다 우선/보통/낮춤/제한을 고릅니다. 게임 프로세스는 열지도 바꾸지도 않습니다. 다른 앱을 낮춰서 게임 몫을 늘립니다. 앱을 끄면 모두 원래대로 돌아갑니다.

| | Windows | macOS |
|---|---|---|
| 사용량 그래프 | CPU·GPU(작업 관리자와 같은 성능 카운터)·메모리 | CPU·메모리 (앱별 GPU는 macOS가 공개하지 않음) |
| 우선 | 우선순위를 보통보다 한 단계 위로(AboveNormal, '높음'은 쓰지 않음) | 불가 (macOS가 허용 안 함) |
| 낮춤 | 우선순위 낮춤 + 효율 모드 + 메모리 우선순위 낮춤 + 다음 실행부터 내장 GPU | 백그라운드 모드 (`taskpolicy`) |
| 제한 | Job Object로 CPU 최대 %를 강제 (5~90%) | 불가 |

- GPU를 앱별로 몇 %씩 나눠 주는 기능은 Windows에도 없습니다. '낮춤'은 그 앱을 다음 실행부터 내장 GPU로 보내 외장 GPU를 비웁니다.
- 메모리를 강제로 막는 제한은 앱이 튕길 수 있어 넣지 않았습니다.
- 확인 필요: macOS `taskpolicy -b/-B -p` 동작(OS 버전별), Windows에서 관리자 권한으로 실행된 앱은 권한 부족으로 바꾸지 못함(메시지 표시).

## macOS: 게임 부스트
`Services/MacBoostBackend.cs` — macOS가 허용하고 효과가 있는 것만 넣었습니다. 바꾸기 전 상태를 `mac-backup.json`에 저장하고, 항목별 또는 "백업 · 되돌리기"에서 한 번에 되돌립니다(관리자 암호는 macOS 표준 창으로 받음).
- 저전력 모드 끄기 (`pmset`)
- Time Machine 자동 백업 멈추기 (`tmutil disable` / `enable`)
- Spotlight 색인 멈추기 (`mdutil -a -i off` / `on`)
- 무거운 앱 정리: CPU·메모리 상위 앱 목록, 고른 앱만 정상 종료 요청
- 상태: 발열(CPU 속도 제한), 메모리 여유, 저전력 모드
- 넣지 않은 것(macOS가 막아 둠): RAM 정리, GPU 클럭, 프로세스 우선순위 올리기, 코어 배치

## macOS에서 다른 점
- 처음 실행할 때 **화면 기록 권한**이 필요합니다 (시스템 설정 → 개인정보 보호 및 보안 → 화면 기록).
- 게임 창 위의 출력 창은 다른 캡처·녹화에 잡히지 않습니다(sharingType = none).
- RAM 정리·GPU 오버클럭·CPU 우선순위는 macOS가 앱에 열어 두지 않아 넣지 않았습니다(위 "게임 부스트" 참고).
- 업스케일 중에는 실제 커서를 게임 창 안에 가두지만, 커서를 확대 위치에 다시 그리지는 못합니다. 마우스 커서를 쓰는 게임에서는 커서 위치가 화면과 어긋나 보일 수 있습니다.
- 듀얼 GPU는 내장 + 외장 그래픽이 있는 Intel Mac에서만 의미가 있습니다. Apple Silicon은 GPU가 하나입니다.
- 창 모드 게임은 제목 표시줄까지 캡처됩니다. 전체 화면(별도 Space) 또는 테두리 없는 창 모드를 권장합니다.
