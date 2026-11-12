# 관련 연구와 주장 범위

작성·공식 출처 확인일: **2026-11-10**. 상태: 연구 준비 초안. 새 실험 결과는 없다. 웹·코드 열람 사실과 연구자의 최종 원문 검증을 구분한다. 본 문서의 비교는 아래 출처와 열람한 경로에 한정되며 체계적 문헌고찰이나 신규성의 증명이 아니다.

## 1. 연구 문제

연구 대상은 Windows에서 사용자가 양보를 허용한 배경 작업의 자원 정책을 조절할 때 생기는 **전경 응답 지연과 배경 진행량의 교환 관계**다. CARP는 이득 추정과 배경 손실·재접근 위험·관리 비용을 함께 판단하는 연구 정책이다. 물리 RAM을 늘리거나 메모리를 정리하면 자동으로 빨라진다는 가설을 세우지 않는다. 정책 추정치는 관측한 앱 지연과 별도로 기록한다.

이미 Windows에는 전경 상태에 따른 QoS와 프로세스·스레드별 우선순위 조절 수단이 있다. 전경 보호·저우선순위 배경 작업·적응형 자원 배분 자체를 새 기여로 주장할 수 없다. 이 연구의 후보 기여는 이러한 수단 위에서 비용 제약, 무개입, 복구 및 관측의 불확실성을 명시하고, 동일 작업의 대응 비교로 절충을 평가하는 것이다. 실제 기여 여부는 [프로토콜](PROTOCOL.ko.md)의 C·D·E·F 비교 후 판단한다. [R3–R6]

## 2. 비교표

| 대상 | 1차 출처에서 확인한 범위 | CARP와 겹치는 부분 | 평가에서 구분할 점 |
| --- | --- | --- | --- |
| MemClean (`Xenonesis/memclean`) | Windows Rust 도구. README의 메모리 정리·전경 보호 설명과 별도로, `cleaner.rs`의 수동/자동 경로, `guard.rs`의 전경 PID·제외 목록, `process.rs`의 EcoQoS 설정 함수를 확인했다. | 전경을 고려한 선택적 조치와 Windows 자원 제어 | 자동 정리는 standby와 선택적 파일 캐시 경로이며 수동 정리와 다르다. 전경 보호·EcoQoS가 없다고 쓰지 않는다. 함수 존재만으로 자동 호출·복구·비용 모델 전체를 입증하지 않는다. [R1] |
| Mem Reduct (`henrypp/memreduct`) | README는 Native API 기반 캐시·working set·standby·modified list 정리를 설명한다. `src/main.c`에 해당 `NtSetSystemInformation` 분기가 있다. | Windows 메모리 상태 관측과 조치 | README의 감소율은 개발자 주장이다. 지연 개선·순 RAM 확보·CARP 대비 우열의 근거로 사용하지 않는다. 설정·실행 경로를 고정하지 않은 제품명 비교는 불충분하다. [R2] |
| Microsoft Windows QoS/API | QoS는 성능·전력 효율 의도를 나타내고 코어 선택·전력 관리에 영향을 준다. 스케줄링 우선순위와 구분된다. `SetProcessInformation`은 메모리 우선순위와 EcoQoS 조절 수단을 제공한다. | 조치 수단 및 전경/배경 구분 | OS 자체 정책이 A에 이미 존재한다. EcoQoS는 전경 응답시간 보장이나 고정 CPU 할당량이 아니다. API 조합만으로 정책 신규성을 주장하지 않는다. [R3, R4] |
| Acclaim | Liang 등, USENIX ATC 2020. Android의 page re-fault와 direct reclaim 문제에 대응하는 foreground-aware eviction(FAE) 및 allocation 예측 기반 reclaim(LWP). | 전경 인지, 재접근 비용, 예측을 통한 적응 | Linux/Android reclaim 설계와 Windows 사용자 공간 우선순위 정책은 계층·수단이 다르다. Windows에서 구현·측정하지 않은 Acclaim 성능을 수치 기준선으로 놓지 않는다. [R5] |
| Heracles | Lo 등, ISCA 2015 및 TOCS 2016 확장판. 지연 민감 서비스의 목표를 지키며 best-effort 작업의 자원 이용을 늘리는 피드백 제어. CPU·캐시/메모리·네트워크 등의 격리를 조정한다. | 지연과 배경 처리량의 명시적 절충, 다중 자원 간섭 | 데이터센터 SLO와 데스크톱 상호작용의 관측·조치 권한이 다르다. 개념적 선행연구이며 동일 PC에서 재현한 직접 비교 대상이 아니다. 확장판과 원 논문의 결과를 섞지 않는다. [R6] |

MemClean README의 5단계 설명을 모든 실행 경로의 실제 동작으로 옮기지 않는다. 확인한 `perform_auto_clean`은 working set 축소·modified flush를 수행하는 수동 `perform_clean`과 구분된다. 수동 경로의 `force_aggressive` 및 보호 설정에 따라서도 동작이 달라진다. README의 무지연·성능 보장 표현은 검증된 사실로 인용하지 않는다. [R1]

## 3. Windows 메모리 지표의 해석

Working set은 현재 물리 메모리에 상주한 pageable 페이지 집합이다. 제거된 페이지를 다시 참조할 때 fault가 생기며, 디스크 읽기 없이 해소되는 경우도 있다. 따라서 working set 감소, 가용 RAM 증가, private commit 감소, 전경 지연 개선은 서로 다른 주장이다. `PrivateUsage`는 프로세스의 private commit이며 private resident RAM이나 디스크 pagefile 점유량이 아니다. 여러 프로세스의 working set 합에는 공유 페이지 중복이 생길 수 있다. [R7, R8]

메모리 우선순위는 페이지 회수 우선도를 바꾸는 수단이고, 할당 해제나 commit 감소와 동일하지 않다. CPU·EcoQoS 조절로 혼합 부하의 지연이 좋아져도 RAM 제어의 독립 효과라고 부르지 않는다. 해당 구분에는 CPU-only/RAM 중심 조건과 조치별 F 절제가 필요하다. [R4; 연구 설계상의 해석]

## 4. 기존 RamFlow 기록과 새 연구의 관계

- [저메모리 예비 기록](../SCARCITY_STUDY.ko.md): working set 축소의 재접근 비용과 순성능 효과의 불확실성을 드러낸 내부 기록이다.
- [배경 제어 기록](../BACKGROUND_CONTROL_STUDY.ko.md): 단일 PC·합성 부하에서 전경 지연과 배경 처리량 손실을 함께 보고한다. CPU와 RAM 조치의 독립 인과효과는 분리하지 않았다.
- [실제 앱 기록](../REAL_APP_STUDY.ko.md): Chrome·VS Code에서 생성한 전경 작업과 합성 배경 부하를 사용했다. 실제 앱 실행과 실제 일상 작업·실제 배경 빌드는 구분한다.
- [기존 계측 정의](../BENCHMARK.md): 관측기 비용, 조건 검증, 결측을 다룬다. 새 프로토콜은 원시 요청·시간 커버리지가 추가로 필요하다.

위 기록은 내부 예비 근거이며 CARP 결과나 새 확증 표본이 아니다. 후속 문서에서 수치를 옮기려면 빌드 해시·원시 자료·측정 정의와 함께 별도 표로 출처를 밝힌다. 유리한 관측만 골라 새 결과와 합치지 않는다.

[기준선 감사](BASELINE_AUDIT.ko.md)는 기존 11월 benchmark 작업별 원본·당시 실행 소스를 현재 및 확인한 이전 사본에서 확보하지 못했다고 기록한다. 요약 자료가 존재하는 것과 독립 원시 자료 감사 완료는 다르다. 공개 v0.3.4 ZIP의 `e9b86e1f…` release digest와 옛 로컬 재포장 ZIP의 `d4d2a1c9…` hash도 다르므로 동일한 공개 배포본이라고 부르지 않는다. 전체 hash와 검증 범위는 해당 감사를 따른다.

새 구현 `0.4.0-carp.1`은 `v1` 기본값과 fixed/carp 선택, 외부 관측 주입 계약을 제공한다. CARP의 네 가중치 1은 미보정 값이며 실제 앱 비용 모델은 없다. 기본 관측 부재 시 자원 후보 무개입, Working Set·CPU Sets 제외, 별도 큐 시작 가드와 순수 `ResearchReplay`를 구분한다. 이 작업의 Windows+IPC 274개 assertion 및 합성 입력의 실제 테스트 자식 setter/원복은 기능 근거다. 성능 파일럿·현실 비용 보정·CARP 우위의 근거가 아니다. 자세한 실제 기본값과 제약은 [프로토콜](PROTOCOL.ko.md)에 기록했다.

## 5. 방어 가능한 기여와 남은 조사

후보 기여는 (1) Windows의 제한된 조치 수단에서 전경 이득과 배경 비용을 함께 다루는 판단 계약, (2) 결측·오래된 관측에서 무개입하고 신원과 복구를 감사하는 구현, (3) 고정 정책/V1/절제 및 정상 상태를 포함한 재현 평가다. 각 항목은 구현 증거와 새 실험으로 확인해야 하며, 기존 도구보다 우월하다는 결론을 미리 쓰지 않는다.

**“최초”, “유일”, “기존 도구는 전경을 고려하지 않는다”, “메모리 정리로 전체 성능 향상”이라는 주장은 사용하지 않는다.** 이번 조사는 다섯 비교군 중심의 범위 제한 조사다. 투고 전 국내외 Windows 자원 스케줄링·QoS·간섭 제어 연구를 확장 검색하고, 검색식·기간·제외 이유를 보존해야 한다. 해당 추가 조사는 미완료다.

## 6. 직접 출처와 확인 범위

모두 **2026-11-10** 열람. GitHub default branch는 변하므로 아래 고정 commit을 기준으로 재검증한다. 조회한 branch SHA와 당시 읽은 코드의 최종 대조는 투고 전 사람이 수행한다.

| ID | 직접 URL | 확인한 내용·제한 |
| --- | --- | --- |
| R1 | [MemClean README](https://github.com/Xenonesis/memclean), [cleaner.rs](https://github.com/Xenonesis/memclean/blob/6d2914f5df664e99fdf5ac46ce34a560b11f2e7b/memclean/src/engine/cleaner.rs), [guard.rs](https://github.com/Xenonesis/memclean/blob/6d2914f5df664e99fdf5ac46ce34a560b11f2e7b/memclean/src/engine/guard.rs), [process.rs](https://github.com/Xenonesis/memclean/blob/6d2914f5df664e99fdf5ac46ce34a560b11f2e7b/memclean/src/core/process.rs) | 작성자 저장소·코드. 확인 SHA `6d2914f5df664e99fdf5ac46ce34a560b11f2e7b`. 자동/수동 분기와 보호·EcoQoS 함수 열람. 실행·성능 검증 안 함. |
| R2 | [Mem Reduct README](https://github.com/henrypp/memreduct), [main.c](https://github.com/henrypp/memreduct/blob/04402de8b6a8ff9d1c5517c675d87c346655323c/src/main.c) | 작성자 저장소·코드. 확인 SHA `04402de8b6a8ff9d1c5517c675d87c346655323c`. 정리 API 분기 열람. 독립 성능 실험 안 함. |
| R3 | [Microsoft: Quality of Service](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service) | 스케줄링 우선순위와 QoS, 전경 상태·전력 정책의 관계. 배터리에서 무입력 자동화가 QoS에 영향을 줄 수 있다는 시험 주의사항도 확인. |
| R4 | [Microsoft: SetProcessInformation](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation) | ProcessMemoryPriority·ProcessPowerThrottling의 용도. 특정 PC 성능의 보증은 아님. |
| R5 | [USENIX 발표·초록·서지](https://www.usenix.org/conference/atc20/presentation/liang-yu), [공식 PDF](https://www.usenix.org/system/files/atc20-liang-yu_0.pdf) | Yu Liang, Jinheng Li, Rachata Ausavarungnirun, Riwei Pan, Liang Shi, Tei-Wei Kuo, Chun Jason Xue, *Acclaim: Adaptive Memory Reclaim to Improve User Experience in Android Systems*, USENIX ATC 2020, pp. 897–910. 발표 페이지 확인. PDF 직접 열람은 403으로 실패하여 상세 실험 수치·알고리즘 원문 검증은 미완료. |
| R6 | [Google Research: ISCA 2015](https://research.google/pubs/heracles-improving-resource-efficiency-at-scale/), [저자 연구실: TOCS 2016](https://mast.stanford.edu/pubs/improving_resource_efficiency_at_scale_with_heracles/), [확장판 PDF](https://mast.stanford.edu/pubs/improving_resource_efficiency_at_scale_with_heracles/paper.pdf) | David Lo, Liqun Cheng, Rama Govindaraju, Parthasarathy Ranganathan, Christos Kozyrakis. *Heracles: Improving Resource Efficiency at Scale*, ISCA 2015. 확장판 *Improving Resource Efficiency at Scale with Heracles*, TOCS 34(2), Article 6, 2016, [DOI](https://doi.org/10.1145/2882783). 확장판 §3–5 및 첫 페이지의 원 논문 구분 확인. |
| R7 | [Microsoft: Working Set](https://learn.microsoft.com/en-us/windows/win32/memory/working-set) | resident 페이지·fault·회수 개념. |
| R8 | [Microsoft: PROCESS_MEMORY_COUNTERS_EX](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex) | working set, PageFaultCount, private commit 정의. |

## 7. 주장 검증 기록 양식

행은 실제 원문 대조 후에만 추가한다. 현재 사람 검증자·승인·실험 결과를 채워 넣지 않았다.

| 주장 ID | 원고 주장 | 출처 ID·절/코드 경로 | 근거가 지지하는 범위 | 반대 근거·한계 | 사람 검증자 | 검증일 |
| --- | --- | --- | --- | --- | --- | --- |
