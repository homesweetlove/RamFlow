# CARP 0.1 연구 정책 명세

본 문서는 구현을 설명한다. 비용 계수·예측 모델의 실험적 타당성과 CARP의 성능 우위는 아직 검증하지 않았다. 제품/어셈블리 연구 버전은 `0.4.0-carp.1`, 정책 버전은 `0.1.0-research`이다.

## 연구 문제와 가설

CPU·RAM·혼합 경쟁에서 전경 지연과 배경 진행량을 함께 고려하는 명시적 배경 제어가 기존 V1보다 나은 Pareto 결과를 내는지 검증한다. H1은 동일한 전경 지연 범위에서 배경 처리량을 더 보존하는지, H2는 동일한 배경 손실 범위에서 전경 지연을 줄이는지이다. 일치 허용 범위는 파일럿 후 본 실험 전에 고정한다. H3는 건강 상태에서 불필요한 개입과 관측·제어 비용이 사전 정한 허용 한계 이내인지이다. 모두 검증 전 가설이다.

## 입력 계약

`PolicyInput`은 원시 `Snapshot`, `ProcessSample`, 설정, 전경 PID·이름, 전체화면, 작업 모드, 기존 메모리 EMA 단계, CPU 히스테리시스 상태, idle 시간, 명시적 지정·큐 소유 여부, 현재 조치와 선택적 `CarpEvidence`를 포함한다. 명시적 지정은 기존 전체 실행 파일 경로나 큐가 소유한 PID+생성시각으로 판단한다.

`CarpEvidence`는 PID+생성시각, 관측 UTC 시각, 모델 식별자, 대응 경쟁 유형, 독립 파일럿 블록 수, 후보별 추정치와 신뢰도를 담는다. 선택적 `MeasuredForegroundP95Ms`, `MeasuredBackgroundUnitsPerSecond`는 실제 계측했을 때만 채운다. null을 0이나 실측으로 바꾸지 않는다. 신뢰도는 호출자가 제공하는 모델 품질 지표이며 코드가 통계적 보정을 보장하지 않는다.

기본 입력 소스는 없다. 연구 호스트가 `IPolicyObservationSource`를 명시적으로 주입해야 한다. `JsonPolicyObservationSource`는 지정한 단일 JSON 배열 파일을 최대 1 MiB/1,024개 대상으로 읽고 스냅샷마다 캐시한다. 작성자는 원자적 파일 교체를 사용한다. PID 생성 신원 중복은 거부한다. 이 입력 방식 자체가 모델의 진실성이나 독립 블록 수를 검증하지는 않으며, 실제 앱 계측기·캘리브레이션 감사가 필요하다.

## 후보와 비용식

후보는 무개입, CPU Below Normal, CPU+EcoQoS, 메모리 우선순위, 이들의 허용 조합이다. EcoQoS는 CPU 조치와 함께만 허용한다. CPU 조치는 CPU/혼합, 메모리 조치는 RAM/혼합 경쟁에만 허용한다. CPU Sets, 프로세스 중단·종료, Standby List 삭제, Working Set 강제 축소는 CARP 후보가 아니다.

각 후보의 입력 추정치는 [0,1] 구간이다. \(g_a\)는 전경 이득, \(b_a\)는 배경 손실, \(r_a\)는 재접근 위험, \(o_a\)는 제어 비용이며 모두 사전에 정한 정규화 기준을 따른다.

\[
J(a)=-w_f g_a+w_b b_a+w_r r_a+w_o o_a,\quad J(\varnothing)=0.
\]

정규화 분모, 수집 시간 창, 파일럿 모델의 학습/검증 블록 분리는 앞으로 확정해야 한다. 현재 코드는 정규화된 추정치를 입력받아 비교하며, ms를 자동으로 추정하거나 온라인 학습하지 않는다. AI/RL/LLM은 사용하지 않는다.

유한값·범위·설정·경쟁 유형을 만족하고 신뢰도 0.8 이상인 후보만 가능하다. 잘못되거나 중복된 후보가 하나라도 있으면 전체 관측을 거부한다. `J < -0.02`를 만족하는 도달 가능한 후보 중 비용 최소를 선택하고, 동률이면 더 적은 제어 수를 택한다. 무개입을 기준으로 작은 추정 오차에 반응하지 않도록 margin을 둔다. 기본 가중치는 각각 1이며 **파일럿으로 검증한 값이 아니다**.

개입 확대는 한 번에 제어 하나만 추가한다. 중간 단계도 자체 이득 근거가 있어야 한다. 기본 진입 조건은 서로 다른 관측 시각의 3개 연속 유효 관측이다. 같은 관측을 다른 tick에서 다시 읽어도 새 관측으로 세지 않는다. 실제 조치 변화 이후 새로운 제어 도입은 30초를 기다리며, 제어 수가 같은 종류 교체에도 이 조건을 적용한다. 증거 소실·건강 상태·보호 변경은 즉시 무개입/원복으로 전환한다. 기존 제어를 제거하기만 하는 완화는 확대 쿨다운에 묶지 않는다. 시계 역행·오래된 관측·미래 관측·PID 재사용은 무개입이다.

엔진은 `OnActionChanged`로 실제 적용과 복구를 직접 통지한다. 따라서 두 tick 사이의 빠른 전경 복구도 안정화 횟수를 초기화하고 쿨다운에 반영한다. 관측의 원래 시각과 정책 상태 갱신 시각을 분리해 몇 초 지연된 유효 관측도 소실 이후 정상 복귀할 수 있다. 적용 직전에는 관측 취득·감사·저널·최종 검증에 소비된 단조 경과 시간을 더해 관측 만료를 다시 검사한다.

## 의사코드

```text
if hard_protection or not explicit_background: return NONE
classify contention from shared snapshot + EMA/hysteresis
if healthy or identity/system/evidence invalid: return NONE
validate recent same-identity same-regime model evidence
include NONE with zero cost
score permitted candidates with normalized cost
discard candidates without benefit beyond entry margin
restrict escalation to one supported control at a time
count distinct observation timestamps, apply cooldown
return selected action, all candidate costs, and reason
```

## 엔진 경계

```mermaid
flowchart LR
    A[시스템·프로세스 관측] --> B[동일 PolicyInput]
    B --> C[V1 / 고정 / CARP 판단]
    C --> D[감사 로그·최종 보호 재검사]
    D --> E[원래 설정 저널]
    E --> F[PID 생성 신원 확인·Windows 적용]
    F --> G[전경 전환·증거 소실·중지 시 복구]
    G --> H[실패 시 저널 유지·재시도]
```

시스템·보안·사용자 예외·활동 정보 미확인 대상은 정책과 최종 적용 경계에서 보호한다. 명시적 배경 지정도 이 보호를 해제하지 않는다. 기존 V1과 동일하게 명시적 배경 지정은 일반 활성 작업 보호만 완화하며 전경/시스템/보안 보호는 유지한다. Dry Run에서는 새 설정을 쓰지 않는다. 기존 조치의 안전 복구는 Dry Run 전환 때도 수행한다.

`Engine`은 정책 판단, 마지막 전경/신원 검증, 저널 선기록, 선택된 설정 적용, 복구, 연구 계측을 소유한다. V1의 기존 Working Set 경로는 별도로 보존하며 CARP·고정 정책에서는 실행되지 않는다. 학습 기반의 추가 보호는 엔진의 안전 경계에 남는다. 따라서 재생 도구는 순수 정책 비교이며 최종 엔진 보호를 모두 실행한 성능 실험이 아니다.

큐는 CPU 압박 또는 RAM 여유 부족 시 **새 작업 시작만** 보류한다. 이는 보수적 입장 제어이며 현재 비용 함수에서 `defer` 후보의 전경 이득/완료 지연을 최적화한 구현은 아니다. 실행 중 작업을 중단하지 않는다. 큐 대기 비용을 포함한 CARP 통합 최적화는 미완료이다.

## 감사 로그와 오버헤드

`FileResearchAuditSink`는 새 디렉터리의 manifest/JSONL/CSV를 생성하고 기존 run을 덮어쓰지 않는다. manifest는 호출자가 전달한 commit과 선택적 소스 트리 해시, 실제 Core 해시·어셈블리/런타임 버전을 담는다. commit 문자열이 실행 바이너리와 일치하는지는 연구자가 검증해야 한다. dirty diff와 모델/계측기 해시도 연구 manifest와 함께 보존한다.

이벤트에는 정책명·버전·설정 해시, 스냅샷, 후보·선택 사유, PID+생성시각, 조치 의도·적용/복구 결과와 일부 실패 상태가 기록된다. JSONL이 원본이고 CSV는 인덱스다. 감사 로그 쓰기 실패 시 새 개입을 막으며 복구는 계속한다. 동기 파일 쓰기의 비용이 있으므로 엔진 자체 tick 소요 시간만으로 전체 비용을 판정하지 말고 독립 관찰기로 CPU·Private Bytes·Working Set·로그 I/O와 무관측 대비 비용을 측정해야 한다. 현재는 그 실측을 하지 않았다.

`Snapshot.PagesInput`은 시스템 전체 page-in 페이지/초이다. PID별 누적 page-fault 수, 디스크 hard fault 수, 전경 지연과 같지 않다. PID별 Hard Fault는 별도 ETW 귀속 수집 전까지 unavailable/null이다.

## 소거와 실패 판정

가중치 0으로 배경 비용/재접근 비용/오버헤드 항을 하나씩 제거하는 소거가 가능하다. `StableSamples=1`, `CooldownSeconds=0`은 안정화 기제 소거용 설정이다. 보호·신원·저널·Dry Run은 소거하지 않는다. 본 실험에서 어느 한 기제를 제거할지 사전 고정하며 안전 소거를 우위 실험으로 부르지 않는다.

관측 모델의 독립 검증 실패, 보호 오개입, 원래 값 복구 불일치, 미해결 복구, 계측 오버헤드 한계 초과, CARP의 Pareto 우위 부재도 실패/무효/부정 결과로 보고한다. 결과가 나올 때까지 개선률·유의성·게재 가능성을 확정하지 않는다.
