# RAM Flow QoS 진단과 v4 실앱 연결 검증

2026-11-12 KST · i5-13400 (10코어/16논리 CPU), Windows 11 빌드 26200, OS 보고 물리 RAM 25,430,679,552 bytes · 앱/조건당 1세션. **6/6 스모크 연결 검증 통과**. 총 1874개 전경 요청은 독립 실험 반복이 아니다. 성능 우위·동등성·KCI 게재 가능성은 이 결과로 확정하지 않는다.

## 이번에 실제 구현한 부분

읽기 전용 스레드/코어/PDH 진단, 생성한 자식 PID+정확한 생성 FILETIME에 한정한 QoS 대조 도구, 지속 CPU 부하 입장 검사와 바이너리 해시 결속, 새 v4 입력층을 추가했다. 정책 기준선 복원과 공통 워크로드 초기값 복원을 별도 저널·readback으로 검증한다. 제품 V1/Engine의 임계값과 판단 로직은 변경하지 않았다.

## 사전 진단과 입력 조건

기본 QoS의 14개 계산 작업자는 진행 중이었지만 마지막 실행 위치 샘플이 효율 클래스 0인 논리 코어 12~15에 모였다. 동일 PID의 기본 상태→명시적 정상 QoS→기본값 복원 대조에서 CPU와 샘플 위치가 가역적으로 바뀌었다. 마지막 위치는 연속 실행 추적이 아니며 시스템 전체 실행 큐를 이 작업에 모두 귀속하지 않는다. 명시 API의 0/0은 효과적인 OS QoS 분류를 알려주지 않고, immediate Job 정보는 상위 Job 제한을 배제하지 않는다.

| 동일 소유 PID 진단 구간 | 전체 CPU 범위 | QoS Control/State |
|---|---:|---:|
| default-qos | 28.8–35.9% | [(0, 0)] |
| normal-qos | 94.9–99.8% | [(1, 0)] |
| default-restored | 30.1–37.9% | [(0, 0)] |

이 PC/부하의 대조 관측이다. Windows는 QoS에 따라 코어 선택과 전력 관리를 달리할 수 있다. [Microsoft QoS](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service), [SetProcessInformation](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation).

v4는 **모든 A/B/C에 동일한 명시적 정상 QoS 워크로드**를 넣은 별도 조건이다. A는 엔진/트레이 미실행이며 워크로드 QoS까지 Windows 기본인 조건은 아니다. 공통 초기화는 CPU/메모리 우선순위나 affinity를 바꾸지 않는다. Normal→Contention→Recovery 활성 작업자 수는 1→14→1, 작업 단위는 xorshift64-1000000-v1:normal-qos이며 체크섬 bd2eeae7775b742c를 확인했다. 기본 QoS v2/v3의 실패/무개입 데이터와 합치거나 대체하지 않았다.

새 사전 검사 CPU 표본은 [99.0, 100.0, 100.0, 100.0, 100.0, 100.0, 100.0, 100.0, 97.7, 94.7, 95.8, 96.1]%. 지속 기준과 초기 설정 복원·정리 모두 통과했다.

진단 원본은 5bd790b 기반 dirty 개발 스냅샷과 해시를 보존한다. QoS probe의 원본 inference 문자열에 read-only라는 이전 문구가 남아 있지만 실제 profile/이벤트/결과는 쓰기 2회를 명시한다. 이 문구를 사후 수정하지 않았다. v4 본 실행 소스는 clean `bff7583801c2a09beaf352673de10256e652e08a`이며 6세션의 controller/core 해시가 같다.

## 관측한 성능: Contention 구간

아래는 단일 세션 내 p95이며 블록별 중앙값이나 효과 검정이 아니다. Chrome은 클릭 핸들러→두 번째 rAF, VS Code는 completion/save API 지연이다. 물리적 키 입력→화면 표시 지연으로 해석하지 않는다.

| 앱·기능 | A 미실행 p95 ms | B 고정 p95 ms | C V1 p95 ms |
|---|---:|---:|---:|
| chrome search | 32.46 | 31.70 | 32.86 |
| chrome sort | 30.50 | 30.21 | 30.70 |
| code completion | 111.64 | 78.40 | 97.35 |
| code save | 22.67 | 21.20 | 21.32 |

| 배경 처리율 (완료 단위/초) | A | B | C |
|---|---:|---:|---:|
| chrome | 8276.0 | 1814.9 | 5157.5 |
| code | 7712.7 | 1781.3 | 5093.5 |

Chrome의 세 조건 지연은 매우 비슷했다. VS Code에서 B는 C보다 낮은 completion p95를 보였고 C는 더 많은 배경 처리량을 보존했다. 따라서 **같은 응답성에서 처리량 우위라는 주장은 아직 입증되지 않았다**. Recovery의 C 배경 처리율은 Chrome 712.4 / Code 684.3 단위/초였다. B는 Recovery에도 고정 제한을 유지한다. 실행 시간이 정해진 스트리밍 부하이므로 작업 전체 완료시간은 측정하지 않았다.

## 적용·복원과 안정성

B/C의 활성 OS readback은 두 앱에서 모두 CPU 0x4000(BelowNormal), 메모리 우선순위 3, QoS Control/State=1/1, CPU Sets=[]로 일치했다. 공통 입력 기준선은 CPU 32(Normal), 메모리 5, QoS 1/0이고, 실행 전 워크로드 원본 0/0까지 별도로 복원했다. A의 정책 쓰기는 0회이며 공통 워크로드 초기화/복원 쓰기 2회는 별도 표기했다.

| 세션 | 요청 | 정책 적용 | Recovery 마지막 복원 지연 초 |
|---|---:|---:|---:|
| chrome-A | 354 | 0 | 해당 없음 |
| chrome-C | 344 | 9 | 0.032 |
| chrome-B | 348 | 1 | 해당 없음 |
| code-C | 272 | 8 | 0.703 |
| code-A | 274 | 0 | 해당 없음 |
| code-B | 282 | 1 | 해당 없음 |

Chrome C는 Contention에서 적용/복원을 9/8회 반복하고 Recovery에서 마지막 복원했다. Code C는 Contention에서 7/7회, Recovery 초입에서 1/1회 적용/복원했다. 압박 해제에 따른 반복 전환과 Recovery 지연 개입은 V1의 안정성 한계다. 완료 판정은 회복 구간 안 복원과 원본 readback을 충족했다는 뜻이며, 무진동 정책이라는 뜻이 아니다. 원시 타임라인을 그대로 보존한다.

모든 전경 결과 정확성 확인, 보호 전경 오개입 0, readback 불일치 0, 원복 실패 0, 소유 Job 정리 완료 및 남은 PID 0이었다. fd17548 계열 무결성 검사에서 원시 응답/백분위수/처리율/완료 마커/파일 해시와 v4 두 단계 복원을 재검증했다. 실패 데이터는 이전 증거 묶음 그대로 보존한다.

## 실제 오버헤드 예시: Code C

CPU는 16개 논리 CPU 전체 대비 평균 %, 메모리는 해당 구간 최대 MiB. 엔진은 실행기와 같은 프로세스이므로 엔진 단독 비용이 아니다. 원시 표본 수·모든 세션/단계 값은 overheads.csv에 있다.

| 단계 | 프로세스 역할 | CPU % | Private MiB | Working Set MiB |
|---|---|---:|---:|---:|
| Normal | engine-and-controller | 1.067 | 35.38 | 73.58 |
| Normal | tray | 0.185 | 12.75 | 51.62 |
| Normal | app-driver | 0.180 | 22.38 | 35.84 |
| Normal | observer | 0.667 | 17.83 | 42.41 |
| Contention | engine-and-controller | 0.610 | 30.00 | 68.73 |
| Contention | tray | 0.054 | 12.88 | 51.88 |
| Contention | app-driver | 0.071 | 22.38 | 35.84 |
| Contention | observer | 0.068 | 18.14 | 42.71 |
| Recovery | engine-and-controller | 0.229 | 33.03 | 72.52 |
| Recovery | tray | 0.035 | 13.61 | 52.74 |
| Recovery | app-driver | 0.099 | 22.38 | 35.84 |
| Recovery | observer | 0.059 | 13.85 | 37.11 |

| 세션 | 결정 p50 / p95 ms | 감사 쓰기 p95 ms | 전체 저널 쓰기 누적 ms |
|---|---:|---:|---:|
| chrome-A | 엔진 미실행 | 해당 없음 | 149.83 |
| chrome-C | 0.0030 / 0.0089 | 0.2811 | 269.46 |
| chrome-B | 0.0020 / 0.0068 | 0.2446 | 179.28 |
| code-C | 0.0032 / 0.0176 | 0.3353 | 218.34 |
| code-A | 엔진 미실행 | 해당 없음 | 126.44 |
| code-B | 0.0027 / 0.0078 | 0.2696 | 158.50 |

누적 쓰기 벽시계 시간은 CPU 사용량이 아니다. 결정 시간 표본에는 보호 대상 평가도 포함된다. 서비스는 미실행이며 A 트레이/엔진은 미실행으로 기록한다. 순수 Idle, 관측 전용, 활성 평가, 실제 제한, 복원 상태를 각각 격리한 오버헤드 실험은 아직 아니다. 짧은 복원만의 비용을 샘플링 구간에서 분리해 주장하지 않는다. Python 레거시 대비 개선 주장도 하지 않는다.

## 검증과 재현

로컬: ResearchLab/Tray Release 빌드 경고·오류 0. 앱 계약 14, 지속 부하 입장 5, Windows CPU fixture 2, 소유 QoS 2, 로그 무결성 10 테스트 통과. 별도 Windows ResearchLab 통합과 로깅 실패/취소 통합도 통과했다. 실앱 6세션과 압박 진단은 단위 테스트와 구분한다.

커밋 bff7583의 [Windows Native CI](https://github.com/homesweetlove/RamFlow/actions/runs/43148479476) 및 [제품 Windows 빌드 CI](https://github.com/homesweetlove/RamFlow/actions/runs/43148479412) 성공. Native Windows API 515 / IPC 525 assertion 누적 통과. 관련 Python 검사와 새 소유 PID QoS 테스트, Windows 연구/로깅 통합도 CI에서 통과했다.

실행 명령은 docs/research/REAL_APP_ABC_SMOKE.ko.md에 있다. 패키지의 v4/ 각 세션을 `python scripts/research_logcheck.py --input <추출경로>/v4 --recursive`로 재검사할 수 있다. Windows 경로 길이 문제를 피하도록 짧은 추출 루트를 사용한다. ZIP 내 package-qos-v4.py는 파생 요약/아카이브 생성 절차를 기록한다. ZIP을 별도 짧은 경로에 실제 추출해 6개 봉인 세션과 2개 진단 인덱스의 해시를 다시 확인했다(roundtrip.json). SHA256은 무결성 비교이며 외부 서명/타임스탬프가 아니다.

## 논문 전에 남은 것

현재 달성은 실앱 비교 경로·측정·복원·무결성의 1회 연결 검증이다. CARP의 우수성 검증이나 논문 실험 완료가 아니다. 다음은 독립 세션 블록 반복과 순서 균형, V1 반복 전환의 원인/비용 평가, 같은 지연 목표에서 배경 처리율 비교, 입력 QoS 두 층의 사전 명시다. 이후 실물 8/16/24GB+ 장비, CPU/RAM/혼합/정상 환경, 실제 빌드 등 유한 작업 완료시간, 재활성화·페이지 재접근·starvation 및 CARP 예측 검증/소거 분석이 필요하다. System Pages Input을 per-process Hard Fault로 쓰지 않는다.

본실험 전에 지연 허용 범위·실패 조건·가설·정책/모델·분석 계획·반복 수를 독립 파일럿에 근거해 고정해야 한다. 세션 내 요청을 반복 수로 늘리거나 같은 전경 응답성 달성을 가정하지 않는다. 실패/회귀/효과 없는 조건도 포함하며 게재를 보장하지 않는다.

작업 브랜치 research/carp, [Draft PR #4](https://github.com/homesweetlove/RamFlow/pull/4). main/research/kci-baseline 및 과거 원본은 보존했다.
