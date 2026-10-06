# RamFlow

현재 사용 중인 작업을 보호하는 Windows 리소스 오케스트레이터입니다. 최신 배포 **0.3.0은 C#/.NET 10 + WPF**로 구현했습니다. Python/PySide6 0.2.0은 호환·회귀 검증용으로 보존했습니다.

## 실행

[최신 공개 릴리스](https://github.com/homesweetlove/RamFlow/releases/latest)의 `RamFlow-0.3.0-windows-x64.zip`을 풀고 `RamFlow-native/RamFlow.exe`를 실행하세요. .NET 런타임을 포함하므로 Python이나 SDK 설치가 필요 없습니다. 폴더의 DLL과 `RamFlow.Service.exe`를 함께 유지하세요.

기본값은 **Dry Run**입니다. 시작할 때마다 실제 최적화를 해제하고 예상 동작부터 보여줍니다. 실제 적용은 설정의 **관리자로 다시 열기**를 선택하고 Dry Run을 해제한 뒤 확인합니다. 트레이에서 종료하면 변경한 자원 설정을 복원합니다. 창 닫기는 트레이로 숨깁니다.

## 기능

| 항목 | 구현 |
|---|---|
| 모니터 | RAM/Commit/캐시/Standby/Modified, CPU, 디스크, Page-in/out, 배터리, 지원되는 GPU/온도 카운터 |
| 압박 | Memory Pressure EMA·히스테리시스·연속 확인·Commit 위기 감지, 센서가 있는 항목만 System Pressure에 반영 |
| Foreground First | 현재 앱·동일 이름 프로세스·작업 그룹 보호, 약 250ms마다 복원 확인 |
| App Nap | 30분 Idle 앱의 Memory Priority/CPU Below Normal/EcoQoS, 압박이 높은 2시간 Deep Idle에 제한적 Working Set 축소 |
| CPU Sets | 공식 CPU topology와 EfficiencyClass 사용, 하이브리드·단일 그룹에 한해 Idle 앱 효율 코어 선택; 기본 OFF |
| 예측 | 선택적 시간대·앱 전환 빈도 학습, 자주 사용할 앱 보호; 예측만으로 Working Set을 비우지 않음 |
| 안전 복원 | 원래 값 사전 기록, 일부 API 실패 재시도, 생성 시각 검증, 강제 종료 후 다음 시작 때 복원 저널 재처리 |
| 모델 저장소 | HOT/WARM/COLD 분석, 스트리밍 보관, SHA-256·크기·원자적 메타데이터, 명시적 공간 회수, 복원 후 모델 실행 |
| 클라우드 | 연결된 Google Drive/OneDrive 등의 동기화 폴더 또는 HTTPS WebDAV. 암호는 메모리에서만 사용 |
| Pagefile | Windows 자동/균형/Low RAM/개발/게임/AI/수동 모드, 관측 Commit 기반 권장, 최초 백업, 확인 후 적용·복원 |
| 벤치마크 | Before/After, 선택 프로그램 준비·완료 시간, JSON/CSV 내보내기, Frame Time/tokens/sec 숫자 열 CSV 분석 |
| UI | 한국어 9개 화면, 그래프, 프로세스·그룹·보호 사유, 트레이, 일시 정지·작업 모드 |
| 분리 엔진 | 사용자 세션의 별도 .NET 프로세스, 사용자 SID별 Named Pipe, 로컬 전용, 크기·연결·관리자 토큰 검사 |
| 설치/업데이트 | 사용자 설치·시작 메뉴·선택적 자동 시작, 검증된 업데이트 설치·재시작, 원복 후 제거 |

## 보호와 제한적 개입

시스템·서비스·보안·드라이버·AI·음악·다운로드·렌더·컴파일·서버 및 정보가 불확실한 앱은 보호합니다. 화이트리스트는 이름으로 등록합니다. 정상 메모리 상태에는 개입하지 않으며 CPU 부하나 배터리 상태도 고려합니다. 전체화면 중 새로운 변경을 중단합니다. Working Set은 한 번에 한 앱, 전역 2분 간격, 같은 PID 15분 이상 간격으로만 축소합니다. 자동 종료·Realtime Priority·커널 후킹·Standby Clear는 사용하지 않습니다.

실제 변경 직전과 복원 때 PID 생성 시각, 사용자 소유권·세션·시스템 경로·Critical 상태를 확인합니다. 처음 관측한 값으로 복원하며, 조회가 일시적으로 실패해도 복원 기록을 보존합니다. 정전/강제 종료 뒤 즉시 복원은 불가능하지만 다음 실행이 복원 저널을 재처리합니다. Working Set 자체는 OS가 재접근 시 복구합니다.

## 모델 보관과 복원

모델 저장소에서 원본 폴더와 보관 폴더/HTTPS WebDAV 컬렉션을 선택합니다. 먼저 분석한 뒤 파일을 선택해 **보관 · 원본 유지**를 수행하세요. Dry Run이 켜져 있으면 전송도 시뮬레이션합니다.

동기화 폴더의 해시 일치는 로컬 복사본 검증이며, 클라우드 서버 업로드 완료를 보장하지 않습니다. 원본 제거는 별도 확인 후에만 수행합니다. 공급자의 업로드 완료와 보관 파일을 직접 확인하세요. WebDAV는 PUT 완료 응답 뒤 전체 GET 해시를 검사합니다. 클라우드는 RAM/Pagefile로 사용하지 않습니다.

COLD 파일은 GUID 메타데이터로 관리합니다. 목록에서 복원하거나 **모델 자동 복원 후 실행**으로 런타임의 실행 파일과 인수 JSON 배열을 지정할 수 있습니다. 모델 경로는 마지막 인수로 추가됩니다. 사용 중인 파일, 수정된 원본, 손상된 보관본, 충돌, 경로 이탈, 정션·심볼릭 링크·스파스/온라인 전용 파일은 작업을 거부합니다. 파일 시각 기반 HOT/WARM/COLD는 실제 사용 빈도와 다를 수 있습니다.

## 설치·서비스·제거

포터블 사용은 설치 없이 가능합니다. 사용자 설치는 UI 또는 배포 폴더의 `Install.ps1`로 합니다.

```powershell
./Install.ps1                    # 사용자 Programs + 시작 메뉴
./Install.ps1 -Startup           # 위 내용 + 현재 사용자 자동 시작
./Install.ps1 -MonitorService    # 관리자: 별도 시스템 모니터 서비스도 설치
./Uninstall.ps1                 # 먼저 트레이에서 종료
```

SCM 서비스는 Session 0에서 사용자 Foreground를 볼 수 없으므로 모니터링만 합니다. 실제 자원 조정은 사용자 세션의 분리 엔진이 담당합니다. 관리자 서비스 실행파일은 쓰기 제한된 Program Files, 상태는 쓰기 제한된 ProgramData에 둡니다. 서비스는 기본 설치하지 않습니다.

업데이트 화면에서 공개 저장소 확인 → SHA-256 검증 → 새 버전 설치·재시작을 선택합니다. 설치에는 사용자 확인이 필요하며 일반 권한으로 연 창에서 진행합니다. 선택적 SCM 모니터 서비스는 관리자 권한으로 제거·재설치해 업데이트합니다.

제거는 설치 폴더, 해당 설치의 시작 메뉴/자동 시작 및 선택적 서비스를 제거합니다. Pagefile을 변경했다면 관리자 권한으로 최초 설정을 먼저 복원합니다. `%LOCALAPPDATA%\RamFlowNative`의 모델 복구 메타데이터와 사용자 모델·클라우드 파일은 보존합니다. 이 메타데이터를 삭제하면 보관 모델의 자동 복원 정보를 잃을 수 있습니다.

## 빌드·검증

Windows와 .NET 10 SDK, Python 3가 필요합니다. Python은 검증 스크립트에만 사용합니다.

```powershell
dotnet run --project native/RamFlow.Tests -c Release
dotnet run --project native/RamFlow.Tests -c Release -- --windows --ipc
dotnet run --project native/RamFlow.Storage.Tests -c Release
./scripts/build-native.ps1
```

Mock 회귀, 자체 생성 자식 프로세스만 변경하는 Windows API 왕복, Mock Named Pipe 권한·정상 종료, 테스트 파일만 쓰는 모델 보관·복원, 실제 WPF 창 생성 및 테스트 전용 폴더의 설치·제거를 검증합니다. GitHub Actions도 Windows에서 같은 빌드를 수행합니다. 실제 사용자 Pagefile·SCM 등록·외부 클라우드 업로드는 자동 검증에서 수행하지 않습니다.

## Windows 구조에 맞춘 대안과 측정 범위

문서화된 Background I/O Mode는 자기 프로세스에만 사용할 수 있으므로 RamFlow 엔진 자체에 적용합니다. 다른 앱의 I/O 우선순위를 비공식 Nt API로 변경하지 않습니다. 대신 활성 I/O 작업을 보호하고 Idle 앱 CPU/EcoQoS를 조정합니다. CPU affinity를 고정하는 대신 복원 가능한 CPU Sets를 사용합니다. 현재 앱의 P-core 고정은 수행하지 않습니다.

GPU/ACPI 온도 카운터가 없으면 미지원으로 표시합니다. GPU 값은 관측 전용 dedicated usage이며 전체 VRAM 용량이나 모델별 KV cache 보장을 의미하지 않습니다. Page-in은 페이지 수이며 hard-fault 사건 수가 아닙니다. 앱 준비 시간과 스케줄링 지터는 실제 입력 지연이 아닙니다. 게임/LLM/IDE 결과는 사용자가 선택한 런타임/CSV로 측정합니다. 환경별 성능 개선 수치는 보장하지 않습니다.

WinUI 3의 별도 배포 의존성 대신 WPF를 선택했습니다. 커널 드라이버나 후킹을 사용하지 않습니다. 공식 API와 검증 범위는 [아키텍처](docs/NATIVE_ARCHITECTURE.md), [검증](docs/VALIDATION.md)를 참고하세요. Python 0.2.0 실행 안내는 [기존 버전 문서](docs/LEGACY_PYTHON.md)에 있습니다.
