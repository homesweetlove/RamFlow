# .NET 10 아키텍처

```text
RamFlow.UI (WPF / 트레이)
  → 사용자 SID별 Named Pipe
  → RamFlow.Service --serve (사용자 세션)
      → Core Engine / WindowsResourceProvider
      → Pressure / Policy / Prediction
      → 우선순위 / EcoQoS / CPU Sets / 복원 저널

UI 기능 페이지
  → Storage: Folder / HTTPS WebDAV → 해시·메타데이터 → 명시적 제거·복원·실행
  → Pagefile: 조회 / 권장 / 관리자 적용·백업·복원
  → Benchmark: 관측 / 선택 프로그램 / CSV
  → Updates: 공식 GitHub → 해시 검증 → 경로 검증 → 사용자 설치·재시작

선택적 RamFlowMonitor SCM 서비스
  → Session 0 관측만 수행 / Program Files·ProgramData 관리자 ACL
```

Core는 외부 NuGet 라이브러리 없이 .NET과 Windows API를 사용합니다. UI와 엔진을 같은 권한 수준의 사용자 세션에서 분리하고, JSON 메시지를 1MiB/8개 연결/5초 요청으로 제한합니다. 원격 Named Pipe 접속을 거부하고 최초 인스턴스 플래그를 사용합니다. 관리자 서버는 일반 사용자에게 파이프 인스턴스 생성 권한을 주지 않습니다. 실제 최적화 활성화는 impersonation으로 확인한 관리자 토큰이 필요합니다.

자원 변경 전 복원할 원래 값을 원자적인 JSON 저널에 보존합니다. 부분 성공은 복원 대기로 전환합니다. PID/생성 시각, 실제 사용자 소유권/세션, 시스템 경로와 Critical 플래그를 재확인합니다. 복원 재시도는 비활성화된 정책과 독립적입니다. 유효하지 않은 CPU 우선순위/메모리 우선순위/mask는 저널에서 거부합니다.

폴더 저장소는 로컬 검증만 보장합니다. WebDAV는 자동 리디렉션과 기본 Windows 자격 증명을 사용하지 않고, HTTPS와 사용자가 입력한 메모리 내 자격 증명만 사용합니다. 전송/제거/복원/실행은 각각 Dry Run과 확인 값을 요구합니다. 제거 전에 원본 및 백엔드 해시를 다시 확인하고, 손상/충돌/수정/링크/진행 중 기록은 거부합니다.

## 공식 API 근거

- [CPU Sets](https://learn.microsoft.com/windows/win32/procthread/cpu-sets), [SYSTEM_CPU_SET_INFORMATION](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-system_cpu_set_information): 높은 EfficiencyClass는 더 빠르고 효율이 낮은 코어입니다. 단일 CPU 그룹·상이한 효율 클래스만 선택합니다.
- [GetProcessInformation](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessinformation), [SetProcessInformation](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation): Memory Priority / Power Throttling 원래 mask 보존.
- [SetPriorityClass](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass): Below Normal과 자기 프로세스의 Background I/O Mode만 사용합니다.
- [PDH formatted array](https://learn.microsoft.com/windows/win32/api/pdh/nf-pdh-pdhgetformattedcounterarrayw): 지원되는 GPU/ACPI 센서만 읽습니다.
- [Named Pipe 보안](https://learn.microsoft.com/windows/win32/ipc/named-pipe-security-and-access-rights): 로컬 사용자·관리자/SYSTEM ACL, 생성 권한 분리.
- [.NET Windows 설치](https://learn.microsoft.com/dotnet/core/install/windows): .NET 10 SDK와 자체 런타임 포함 배포.

제품 압박 점수와 모델 실행 가능성은 휴리스틱입니다. 원시 카운터가 없는 값을 임의로 0으로 성공 처리하지 않으며 UI에서 미지원으로 표시합니다. 실제 성능 주장은 동일 작업의 반복 실측 이후에만 가능합니다.
