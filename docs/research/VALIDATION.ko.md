# 연구 구현 검증 기록 — 2026-11-10

환경: Windows x64, 명시적으로 지정한 로컬 **.NET SDK 10.0.401**, Python **3.14.7**. 기본 PATH의 .NET 8 SDK 대신 기존에 설치된 .NET 10 SDK를 사용했다. 연구 버전은 `0.4.0-carp.1`이다. 아래는 코드/도구 검증이며 성능 실험이 아니다.

| 검사 | 실제 결과 | 범위 |
|---|---|---|
| 변경 전 네이티브 기본 검사 | 197 assertion 통과 | `research/kci-baseline`과 같은 최신 main에서 먼저 실행 |
| 최종 네이티브 기본 검사 | 250 assertion 통과 | 기존 보호·원복 + CARP 입력·비용·진입·전환·감사·관측·큐 |
| Windows API + 격리 IPC 포함 | **274 assertion 통과** | 위 250 포함. 생성한 disposable 자식만 실제 설정 변경·복구 |
| 기존 독립 관찰기 | **38 tests 통과** | `python scripts/test_benchmark_observer.py` |
| 비교 스케줄/실행 기록 골격 | **28 tests 통과** | `python -B -m unittest discover -s scripts -p test_research_matrix.py` |
| 저장 기능 기존 회귀 | **26 tests 통과** | `dotnet run --project native/RamFlow.Storage.Tests -c Release` |
| WPF UI / Service / Tray / ResearchReplay | 모두 빌드 성공, 경고 0·오류 0 | `dotnet build <project> -c Release`; 앱·트레이는 실행하지 않음 |
| ResearchReplay 실제 I/O 검사 | 통과 | `python scripts/test_research_replay.py`: 동일 입력·결측·무조치·기존 run 거부·중도 실패 보존 |
| 연구 스케줄 CLI | plan-only 144 trial 생성 | 4경쟁 유형 × 6블록 × 6조건. 실제 실험 수행 수가 아님 |
| 기존 연구 문서·근거 보존 | 19파일 canonical SHA-256 일치, diff 없음 | Git blob 기준 해시, checkout 줄바꿈 변환과 구분 |
| `git diff --check` | 통과 | 기존 기록을 수정하지 않음 |

네이티브 최종 실행 명령은 `dotnet run --project native/RamFlow.Tests -c Release -- --windows --ipc`이다. 기본 검사 250과 Windows/IPC 포함 274를 합산하지 않는다. CI의 서비스 세션(0)에서는 보호를 낮추지 않고 CARP 엔진의 실제 자식 검사만 명시적으로 SKIP할 수 있다. 이번 로컬 사용자 세션에서는 해당 검사도 실제 통과했다.

## 추가한 주요 실패 경계

관측 없음/만료/미래/잘못된 PID 생성 신원, NaN·낮은 신뢰·중복/Null 후보, 보호/사용자 예외/Dry Run, 건강 상태, RAM 재접근 위험, 단계 확대, 제어 종류 교체, 같은 관측 재사용, 일정 지연 관측의 정상 복귀를 확인했다. 실제 적용·복구 통지로 tick 사이 전경 복구의 쿨다운도 유지한다.

관측 취득 지연으로 적용 직전에 유효기간을 넘으면 setter를 호출하지 않는다. 감사 sink의 IOException/예상 밖 예외 및 대체 로그 권한 실패에도 새 개입·큐 시작을 막고 복구를 계속한다. 복구 실패는 pending 상태를 보존하고 재시도한다. JSONL/CSV와 실행 manifest의 존재, 기존 run 덮어쓰기 거부도 검사했다.

독립 코드 검토에서 발견한 7개 경계 문제를 회귀 사례로 반영했다. 구현 전 새 계약의 미정의 실패, Null 후보·반복 관측 문제의 재현 실패 등 red 단계가 있었고 수정 후 위 전체 검사를 통과했다. 초기 파일 접근 제한 실행·테스트 fixture/타입 표기 문제는 해결했다. 이를 누락 실험이나 성능 실패 데이터로 취급하지 않는다.

## 수행하지 않은 검증

- 실앱 CARP 비용 모델 보정·성능 파일럿·새 Chrome/VS Code 지연·실제 빌드/AI 처리량 실험.
- 8/16/24+ GiB 실물 PC 다중 장치·다중 날짜 반복과 사전 고정한 통계 분석.
- 연구 JSONL/CSV 파일 쓰기와 관측기의 독립 오버헤드 실측, PID별 ETW Hard Fault 귀속.
- 이번 로컬 실행에서 자체 런타임 패키징·WPF 화면 캡처·격리 설치/제거 전체 경로. 기존 Windows CI는 이 경로를 실행하도록 유지하며 연구 테스트·재생 도구 검사를 추가했다. 실제 원격 결과는 해당 commit/PR의 Actions 기록으로 별도 확인한다.
- 당시 11월 실험의 대용량 작업별 원본·실행 소스·원래 Core 바이너리의 독립 재검증.

새로운 성능 수치·CI·효과 크기·유의성·CARP 우위를 작성하지 않았다. 테스트가 통과했다는 사실은 KCI 정규논문 수준의 실증이 완료됐다는 의미가 아니다.
