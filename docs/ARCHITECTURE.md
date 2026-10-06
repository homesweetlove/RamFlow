# RamFlow 아키텍처

## 기술 선택

첨부 WinMemoryFlow의 Python/PySide6 구조를 유지했습니다. 기존 시나리오·GUI·Win32 래퍼를 재사용해 실행 가능한 확장 MVP를 제공합니다. C#/.NET 10/WinUI 3 이식은 별도 작업입니다. 커널 드라이버나 후킹은 사용하지 않습니다.

```text
RamFlow UI / Tray (일반 사용자)
  ├─ EmbeddedBackend → Engine
  └─ IpcBackend → 사용자 SID별 Named Pipe → 별도 엔진 프로세스

Engine
  → WindowsProvider / MockProvider
  → MemoryMonitor + PressureEngine
  → AI 감지 + Workload Detection + ProcessAnalyzer
  → Memory Policy + SchedulerAssistant
  → WorkingSetManager (직전 보호 검증 + 실제/모의 실행 분리)
  → EventLog + 선택적 UsageLearner + Benchmark
```

## 모듈

| 모듈 | 책임 |
|---|---|
| `core/win_api.py` | 문서화된 Windows API 구조체/64비트 시그니처 |
| `core/providers.py` | 메모리·PDH·CPU·배터리·프로세스 활동·세션/생성 시각 수집 |
| `core/pressure_engine.py` | 0~100 점수, EMA, 확인 틱, 히스테리시스 |
| `core/system_pressure.py` | 지원되는 자원만 통합. 온도는 미지원 |
| `core/process_analyzer.py` | Foreground/Background/Idle/Deep Idle 및 보호 사유 |
| `core/workload.py` | 모드 감지·수동 선택·개발/게임/AI 등 그룹 |
| `core/policy.py` | 메모리 우선순위, 제한적 축소, 권장·위기 알림 |
| `core/scheduler.py` | CPU Below Normal/EcoQoS 계획·복원 |
| `core/working_set_manager.py` | 원래 값 저장, 실측 전 검증, 설정/종료 시 복원 |
| `core/pagefile_manager.py` | Commit/디스크 기반 권장·명시적 적용·최초 백업/복원 |
| `core/storage.py` | 선택한 모델 폴더의 제한적 읽기 전용 분석 |
| `core/learning.py` | 시간대별 EMA 및 Commit p95/peak |
| `core/benchmark.py` | Before/After 측정과 JSON/CSV |
| `service/commands.py` | 입력 검증, 엔진 명령 직렬화 |
| `ipc/pipe.py` | JSON 파이프, SID DACL, 원격 차단, 관리자 호출 권한 |

## 실제 변경과 복원

1. 분석 시 프로세스 이름·활동·현재 작업 그룹을 보호합니다.
2. 조치마다 PID/생성 시각을 기록합니다.
3. 실제 실행 직전에 포그라운드, 시스템 이름/경로, 동일 사용자 세션, PID 재사용을 확인합니다.
4. 원래 우선순위 또는 EcoQoS mask를 읽을 수 없으면 변경하지 않습니다.
5. 성공한 변경만 별도 `originals`에 저장합니다. Dry Run의 모의 정책 상태와 분리합니다.
6. 앱 재사용/압박 해소/설정 전환/정상 종료 시 저장한 값으로 복원합니다. PID가 다른 프로세스에 재사용되었다면 복원하지 않습니다.

정상 종료 복원을 구현했지만 강제 종료나 정전의 복원을 보장하지 않습니다. Working Set은 강제로 원래 크기를 채우지 않습니다. OS가 재접근 시 페이지를 다시 가져옵니다. 실시간 우선순위는 설정하지 않습니다.

## IPC 권한 경계

파이프는 SID별 이름이며 현재 사용자와 관리자/SYSTEM만 접근합니다. 관리자 서비스에서는 일반 사용자 ACE에 파이프 인스턴스 생성 권한을 포함하지 않습니다. 일반 권한 엔진은 자기 사용자 권한으로 파이프를 생성합니다. 첫 서버 인스턴스는 `FILE_FLAG_FIRST_PIPE_INSTANCE`를 사용합니다. JSON 크기 1MiB, 연결 처리 16개 제한, 원격 클라이언트 차단을 적용합니다.

클라이언트 impersonation으로 관리자 토큰을 확인한 뒤 즉시 revert합니다. 프로세스 종료/Pagefile 적용·복원/실제 변경 활성화는 관리자 클라이언트만 서비스에 요청할 수 있습니다. Embedded 모드에서는 UI의 현재 사용자 권한을 사용합니다. IPC는 다른 사용자에게 임의 관리자 코드 실행을 제공하지 않습니다.

## 공식 Windows API

- [GlobalMemoryStatusEx](https://learn.microsoft.com/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex): 물리 메모리
- [GetPerformanceInfo](https://learn.microsoft.com/windows/win32/api/psapi/nf-psapi-getperformanceinfo): Commit 및 캐시
- [GetProcessInformation](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessinformation) / [SetProcessInformation](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation): Memory Priority / Power Throttling
- [MEMORY_PRIORITY_INFORMATION](https://learn.microsoft.com/windows/win32/api/processthreadsapi/ns-processthreadsapi-memory_priority_information): 우선순위 1~5
- [SetPriorityClass](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass): Below Normal 조정 및 원복
- [EcoQoS](https://devblogs.microsoft.com/performance-diagnostics/introducing-ecoqos/): Execution Speed mask 사용. 기존 mask 보존 및 복원
- [EmptyWorkingSet](https://learn.microsoft.com/windows/win32/api/psapi/nf-psapi-emptyworkingset): Deep Idle에 제한적으로만 사용
- [PdhAddEnglishCounterW](https://learn.microsoft.com/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw): 로케일 독립 카운터
- [Named Pipe Security](https://learn.microsoft.com/windows/win32/ipc/named-pipe-security-and-access-rights): 사용자별 DACL 및 생성 권한 분리

## 정확도와 남은 범위

Pages Input/sec는 page-in된 페이지 수이고 fault 사건 수가 아닙니다. Memory Compression 프로세스 Working Set과 CommitLimit−RAM은 근사입니다. 성능 카운터 권한/센서가 없으면 일부 값이 측정되지 않습니다. 통합 압박은 제품 휴리스틱이며 Windows 공식 지표가 아닙니다.

I/O Priority, affinity/topology, 온도, 클라우드 모델 이동, 실제 SCM 서비스 설치, 자동 업데이트는 구현하지 않았습니다. 자동 Pagefile 변경 및 NtSetSystemInformation Standby 정리 경로는 제거했습니다. 모델 파일의 COLD 판정은 파일 시각 기반 추정입니다. 학습은 가벼운 통계이며 입력 이벤트나 키 내용을 수집하지 않습니다.
