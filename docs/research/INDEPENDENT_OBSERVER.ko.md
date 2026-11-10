# 독립 Windows 관측기

상태: 독립 helper를 ResearchLab의 Trial/Program에 연결했다. 기존 프로세스 내 관측기와 달리, 관측기 자신의 CPU·메모리 비용을 별도 `observer` 역할로 기록한다. 트레이·서비스가 명시 대상에 없으면 해당 역할의 행을 만들지 않으며 0으로 대체하지 않는다.

## 통합 API

`ResearchLab` namespace의 공개 API는 다음과 같다. 전달하는 실행 파일은 같은 애플리케이션의 Windows apphost이며 이름 변경을 허용한다. `dotnet.exe`나 shell을 대신 전달하지 않는다.

```csharp
public static class ObserverWorker
{
    public static Task<int> Run(string[] args);
}
public sealed class IndependentObserver : IAsyncDisposable
{
    public static Task<IndependentObserver> StartAsync(string executable, string directory,
        IReadOnlyDictionary<string, (int Pid, long Identity)> targets);
    public void SetPhase(string phase);
    public Task StopAsync();
    public JsonElement[] ReadSamples();
    public ValueTask DisposeAsync();
}
```

부모의 command dispatch에는 `observer`를 `ObserverWorker.Run(args)`으로 연결해야 한다. `StartAsync`의 사전 검증과 준비 완료 대기는 포함되어 있다. 부모는 실제 생성 신원을 알고 있는 PID만 전달하고, 단계 경계마다 `SetPhase`를 호출한 뒤 반드시 `await StopAsync()`가 끝난 후 `ReadSamples()`를 호출한다. 종료/준비 실패는 예외로 전달되며 정상 성능 표본으로 바꾸지 않는다. `DisposeAsync`도 같은 종료 작업을 기다린다. 프로그램 이름에 의존하거나 실행 중인 다른 앱을 검색하지 않는다.

## 파일·명령 계약

`StartAsync`는 지정 디렉터리에 GUID 기반 `observer-<id>.targets.json`, `.phase.json`, `.raw.jsonl` 경로를 만든다. 대상 파일과 raw는 `FileMode.CreateNew`이며 기존 자료를 덮어쓰지 않는다. phase 제어 파일만 같은 디렉터리의 새 임시 파일을 flush/close한 뒤 rename/replace하여 원자적으로 바꾼다. 종료 후 원시 자료·제어 파일은 보존한다.

```text
<passed-apphost.exe> observer --targets <targets.json> --phase-file <phase.json>
    --output <new-raw.jsonl> --parent-pid <pid> --parent-identity <creation-filetime>
```

대상 파일은 `[{"role":"controller","pid":123,"creation_filetime":134000000000000000}]` 형식이다. **0–32개 명시 대상**, 중복 없는 role/PID, 양의 PID/생성 FILETIME만 허용한다. role은 공백만으로 구성될 수 없고 64자 이하·제어 문자 없음이며 `observer`는 예약되어 있다. JSON 속성은 정확히 `role`, `pid`, `creation_filetime`이어야 한다. 대상 파일은 최대 64 KiB, phase는 최대 4 KiB, JSON 깊이는 최대 8이다. 누락·중복 속성, 잘못된 자료형, 중복/미지정 CLI 옵션을 거부한다. helper 자신은 명시 대상과 별도로 한 행씩 추가되므로 한 tick의 최대 역할 수는 33개다.

phase 파일은 `{"phase":"idle","revision":0}`이며, 공개 단계는 `idle/observe/evaluate/intervene/restore`다. `SetPhase`는 같은 단계 이름으로 호출해도 revision을 증가시킨다. 내부 종료 신호는 `stop`이다. 생성/갱신은 부모 API가 담당하며 수동 덮어쓰기나 revision 재사용은 지원하지 않는다. helper는 raw 파일을 연 뒤 유효한 제어/부모 신원을 확인하면 stdout에 정확히 `ready` 한 줄을 보내고 flush한다. 다른 측정 출력은 stdout으로 보내지 않는다.

## 표본 의미와 실패

명시된 PID와 helper 자신의 PID만 read-only 권한으로 연다. 같은 process handle의 생성 FILETIME을 확인한 뒤 CPU 시간·메모리를 읽고 종료 여부를 다시 검사한다. 대상 재사용·종료·접근 실패 시 해당 역할 행을 `status:"unavailable"`과 오류 내용으로 보존한다. 원래 요청 PID/FILETIME은 남고 CPU·메모리 값은 null이다. 생성 신원이 다른 프로세스의 메모리를 읽어 대체하지 않는다. 부모 신원은 자원 관측 목록과 별도로 생존 확인에만 사용한다.

약 100 ms의 `PeriodicTimer` 주기로 다음 필드를 JSONL에 기록한다. 지연된 tick을 정확한 100 ms 실측으로 가장하지 않으며 CPU 분모에는 실제 단조 경과 시간을 쓴다.

| 필드 | 의미 |
| --- | --- |
| `role`, `phase` | 명시 역할 또는 helper의 `observer`, 관측 단계. 제어 파일 읽기 실패 시 phase는 null이며 실패 행만 남기고 종료한다. |
| `cpu_percent` | `Δ(kernel+user FILETIME)/10^7 / Δmonotonic_seconds / 전체 활성 논리 CPU 수 × 100`. 첫 관측·실패 후 재시작·단계 경계의 구간은 null이다. |
| `private_bytes` | Windows `PROCESS_MEMORY_COUNTERS_EX.PrivateUsage`: private commit bytes. private resident RAM이 아니다. |
| `working_set_bytes` | 같은 구조체의 WorkingSetSize. 여러 프로세스의 합은 공유 페이지를 중복 계산할 수 있다. |
| `decision_ms`, `log_write_ms` | 항상 null. 외부 자원 관측기로 내부 판단/로그 시간을 측정했다고 주장하지 않는다. |
| `time_utc`, `pid`, `creation_filetime` | UTC 표본 시각과 명시/자체 프로세스 신원. 실패 행 시각은 실패 기록 시각이다. |
| `status`, `error`, `cpu_status` | 가용/실패 상태, 오류 원인, CPU의 `first/mixed_phase/ok/unavailable` 구분. |

CPU는 인접 CPU 누적값과 `Stopwatch` 시각으로 계산한다. 전체 논리 CPU 수는 `GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)`로 얻으며 프로세스 affinity/quota로 제한된 `Environment.ProcessorCount`에 의존하지 않는다. batch 앞뒤 phase snapshot을 대조하고 이전 표본의 revision도 비교한다. 따라서 샘플 사이의 `observe→evaluate→observe`도 CPU 구간을 혼합하지 않는다. 경계 batch의 memory는 순간값이고 CPU만 null이다. batch 도중 제어를 읽을 수 없으면 값들을 성공 표본으로 내보내지 않는다.

`stop`, 부모 종료·신원 불일치/접근 불가 시 helper가 종료하고 writer를 닫는다. 잘못된 제어는 실패 행과 종료 코드 3, 초기 스키마/파일 생성 등 오류는 stderr와 종료 코드 2를 남긴다. 부모 API는 준비를 최대 10초, 정상 종료를 5초 기다린다. 종료가 실패하면 **직접 생성한 helper의 보유 handle·PID·생성 신원**을 다시 확인하여 그 프로세스 하나만 종료하고 추가로 최대 5초 기다린다. tree kill이나 PID 이름 검색을 사용하지 않는다. 강제 종료·비정상 exit code는 예외로 알린다. 강제 종료 뒤 파일이 잘렸으면 `ReadSamples`가 파싱 실패를 알리며 행을 조용히 버리지 않는다. 실패 후 읽기는 종료/파이프 정리가 끝났는지 확인한 뒤 수행한다.

이 관측기는 자원 설정, 우선순위, Engine 상태, 다른 프로세스의 수명에 개입하지 않는다. 단, 자신이 생성한 helper에 대한 종료 복구 권한만 사용한다. 별도 프로세스도 CPU·메모리·파일 I/O 간섭을 만들므로 `observer` 비용과 전체 실험 순효과를 함께 보고해야 한다.

## 검증 기록

**2026-11-10: 별도 Windows harness에서 39개 검사 통과.** 부모 runner 파일을 변경하지 않고 이 소스만 함께 컴파일했다. 설치된 Roslyn C# 12 컴파일러와 .NET **10.0.9** assembly를 사용했으며 .NET 10 runtime에서 실행했다. 복사한 apphost를 공백이 있는 다른 이름으로 실행했다. 공유 runner 전체의 MSBuild 빌드·부모 통합 시험은 이 결과에 포함하지 않는다.

- 양수가 아닌 PID/생성 신원, 예약 role, 33개 대상과 잘못된 phase를 거부했다. `StopAsync` 이전 읽기도 거부했다.
- controller 및 helper 자신에 대해 CPU/메모리 표본, 첫 CPU null, 단계별 첫 CPU null과 빠른 `observe→evaluate→observe`의 revision 경계를 확인했다. 없는 PID는 오류·null을 보존하고 tray/service 행은 생성하지 않았다.
- raw `CreateNew` 충돌 시 기존 `PRESERVE` 내용이 유지됐고, JSON 중복 속성은 raw 생성 전에 거부됐다. 이 두 음성 시험의 stderr 오류는 의도한 결과다.
- 직접 만든 자식만 사용하여 대상 생성 신원 불일치, 부모 신원 불일치 및 부모 종료 시 helper 종료를 확인했다.
- `StopAsync`의 동일 Task 재사용, 종료 뒤 raw 파일의 배타적 열기(writer 종료), 고의로 stop을 무시하는 자체 helper 하나의 제한된 강제 종료와 실패 예외를 확인했다. 강제 종료의 실패 상태는 `DisposeAsync`에서도 보존됐다.

관리자/보호 프로세스, PID의 실제 재사용, 32개 대상의 장시간 수집 비용, 64개 초과 CPU/CPU hotplug, 부모 runner의 실험 성능은 검증하지 않았다. 생성 신원을 일부러 다르게 한 시험은 실제 PID 재사용 시험을 대체하지 않는다. 환경의 기본 .NET SDK는 8이므로 정식 net10 SDK 빌드 성공으로 표현하지 않는다. 검증 산출물은 저장소 밖의 별도 작업 디렉터리에 보존했다.

공식 필드/API 근거: [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes), [GetProcessMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo), [GetActiveProcessorCount](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getactiveprocessorcount).
