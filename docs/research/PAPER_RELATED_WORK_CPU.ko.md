# CPU 중심 원고의 선행연구 대조

확인일: 2026-11-12. 기존 관련 연구를 대체하지 않는 집중 검토다. 아래의 발행처·기관 저장소·공식 개발사 자료를 확인했다. 체계적 문헌고찰, 모든 국내외 문헌의 망라, 유사 연구의 부재 증명이 아니다. 검색 시점에 공개된 자료만 확인했으며 성능 수치를 서로 다른 플랫폼에서 직접 비교하지 않았다.

## 이번 원고와 겹치는 점

| 자료 | 확인한 선행 내용 | 이번 연구에서 달라지는 범위 | 신규성에 대한 판단 |
|---|---|---|---|
| R1. Windows Efficiency mode, 2022 | 낮은 CPU base priority와 EcoQoS를 결합해 배경 작업의 전경 간섭을 줄인다. 합성 CPU 부하와 전경 작업 완료시간으로 평가한다. | 정확한 CPU 우선순위 수치·메모리 우선순위·가역적 자동 전환은 같다고 가정하지 않는다. 본 연구는 특정 V1 조합의 고정/적응/무개입과 배경 유효 진행을 비교했다. | **가장 직접적인 중복이다.** CPU 우선순위+EcoQoS와 전경 보호 자체를 새 아이디어로 쓰지 않는다. |
| R2. Windows QoS 공식 문서 | 우선순위와 QoS는 다르며, QoS가 이기종 CPU의 코어 선택 및 전력 관리에 관여한다. | 입력 워크로드의 명시적 QoS, OS readback, 생성 PID 신원, 정책/워크로드 두 단계 복원을 실험 기록에 포함했다. | QoS에 따른 CPU/코어 변화 발견을 Windows의 알려지지 않은 원리라고 주장하지 않는다. |
| R3. Heracles, TOCS 2016 | 지연 목표를 지키며 best-effort 작업에 자원을 제공하는 피드백 제어를 다룬다. | 본 연구는 Windows 데스크톱의 제한된 사용자 모드 API와 두 생성 앱 작업이며, 서비스 SLO를 제어하는 시스템을 구현한 것은 아니다. | 응답성–배경 효율 절충과 적응형 제어라는 목적은 기존 연구다. |
| R4. McClure 외, NSDI 2022 | 작은 작업에서 정적 코어 할당이 동적 재할당보다 유리할 수 있음을 보고한다. | 코어 재할당이 아니라 Windows 프로세스 속성 묶음의 전환을 측정했다. | ‘적응형이 항상 고정보다 낫지 않다’는 일반 명제도 새롭지 않다. |
| R5. Cilantro, OSDI 2023 | 실제 작업 성능의 온라인 피드백, 자원–성능 관계 학습, 추정 불확실성을 정책에 반영한다. | 평가한 C는 CPU 압박 피드백을 쓰지만 전경 지연/배경 손실 피드백이나 비용 추정 모델을 쓰지 않는다. | 비용·관측·불확실성이라는 개념을 새로 제안했다고 쓰지 않는다. CARP 구현의 존재와 실앱 검증을 구별한다. |
| R6. Prequal, NSDI 2024 | CPU 부하의 균형 대신 지연·진행 중 요청 신호로 분산 서버를 선택한다. | 같은 노드에서 CPU 압박을 개입 게이트로 쓸 때의 제한을 분석한다. 분산 부하분산기를 비교 실행한 것은 아니다. | CPU 사용률과 사용자 성능의 불일치는 선행 문제다. 직접 성능 우열 비교는 없다. |
| R7. HoseinyFarahabady·Zomaya, CLOUD 2025 | 발행처 초록은 Linux cgroup 기반 CPU/I/O 제한과 간섭 관측·예측을 설명한다. | 프로세스 속성 API와 소유 PID 가역적 제어라는 구현 범위가 다르다. | 실시간·경량·간섭 인지 제어도 선행 주제다. 상세 알고리즘·실험 우열은 원문 미열람으로 비교하지 않는다. |
| R8. 손용석 외, 2012 | Android의 대화형 태스크 식별과 우선순위 부스트·부하 분산을 설명한다. | 전경 태스크 부스트/커널 수정 대신 지정 배경 PID의 사용자 모드 제어를 평가했다. | 국내에서도 사용자 응답성 인지 스케줄링은 기존 주제다. 게재 서지는 연구실 목록으로 보완했고, 저장소 사본과 최종 본문의 동일성은 아직 대조하지 않았다. |

차별화할 수 있는 부분은 특정 Windows API 제어의 적용 범위·측정 경계·전환 상태를 재현 가능한 자료로 설명하는 실증 사례다. 플랫폼이 다르거나 로그가 상세하다는 사실만으로 독창성이 성립한다고 보지 않는다. 선행 시스템을 Windows에 직접 포팅하거나 동일 조건으로 비교한 실험은 없다.

## 확인한 원문과 서지 상태

1. Claton Hendricks. *Reduce Process Interference with Task Manager Efficiency Mode*. Microsoft, 2022-02-16. [공식 본문](https://devblogs.microsoft.com/performance-diagnostics/reduce-process-interference-with-task-manager-efficiency-mode/). 제품 개발사의 설명/평가이며 동료심사 논문이 아니다. CPU 조합·합성 부하 평가 설명을 확인했다.
2. Microsoft. *Quality of Service*. [공식 문서](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service). API·가시성·휴리스틱 분류와 코어 선택의 관계를 확인했다. 현재 시험 PC의 실제 내부 분류를 이 문서만으로 단정하지 않는다.
3. David Lo 외. *Improving Resource Efficiency at Scale with Heracles*. ACM TOCS 34, 6:1–6:33, 2016. [저자 소속기관 서지·초록](https://research.google/pubs/improving-resource-efficiency-at-scale-with-heracles/), [DOI](https://doi.org/10.1145/2882783). 이번 비교 문구는 확인한 초록의 목표/피드백 제어 범위에 한정한다.
4. Sarah McClure 외. *Efficient Scheduling Policies for Microsecond-Scale Tasks*. NSDI 2022, pp. 1–18. [발행처](https://www.usenix.org/conference/nsdi22/presentation/mcclure), [원문](https://www.usenix.org/system/files/nsdi22-paper-mcclure_2.pdf). 공식 서지·초록 및 PDF를 확인했다. 서버 코어 정책의 결과를 Windows 설정의 효과로 옮기지 않는다.
5. Romil Bhardwaj 외. *Cilantro: Performance-Aware Resource Allocation for General Objectives via Online Feedback*. OSDI 2023, pp. 623–643. [발행처 서지·초록](https://www.usenix.org/conference/osdi23/presentation/bhardwaj). 성능 피드백과 신뢰 경계라는 개념적 중복을 확인했다.
6. Bartek Wydrowski 외. *Load is not what you should balance: Introducing Prequal*. NSDI 2024, pp. 1285–1299. [발행처](https://www.usenix.org/conference/nsdi24/presentation/wydrowski), [원문](https://www.usenix.org/system/files/nsdi24-wydrowski.pdf). 제목·저자·연도·핵심 신호는 PDF/공식 서지로 대조했다. Google 소개 페이지의 저자 약칭 대신 공식 서지 이름을 기준으로 한다.
7. MohammadReza HoseinyFarahabady, Albert Y. Zomaya. *Real-Time Interference-Aware CPU and I/O Capping Mechanism for Multi-Tenant Containers*. IEEE CLOUD 2025. [DOI](https://doi.org/10.1109/CLOUD67622.2025.00039), [발행처 색인](https://ieeexplore.ieee.org/abstract/document/11120584), [주최 측 프로그램](https://services.conferences.computer.org/2025/wp-content/uploads/sites/3/2025/07/SERVICES_webprogram.pdf). 검색에 노출된 발행처 초록 및 공식 프로그램에서 제목/저자를 대조했다. 직접 발행처 페이지 열기는 418 오류였고 전문은 읽지 못했다. 상세 결과는 인용하지 않았다.
8. 손용석·허승주·유종훈·홍성수. *안드로이드 기반 스마트폰의 사용자 응답성 향상을 위한 프레임워크 지원 우선순위 부스트 기법과 로드 밸런싱 기법*. 정보과학회논문지: 시스템 및 이론, 39(6), pp. 380–386, Dec. 2012. [저자 연구실 게재 목록](https://redwood.snu.ac.kr/wordpress/index.php/domestic-journals_new/), [기관 저장소 PDF](https://s-space.snu.ac.kr/bitstream/10371/81520/2/%28CSTV12-07-09-15%29.pdf). 2026-11-12 후속 확인에서 저자 연구실 목록의 검색 색인 본문에 위 서지가 기재되어 있고 RISS 서지와 일치함을 대조했다. 연구실 페이지 직접 열기는 시간 초과였다. 저장소 PDF의 자리표시자/쪽수는 게재 서지와 다르므로 최종 게재본의 본문까지 동일하다고 주장하지 않는다. 앞선 서지 미확정 기록은 이 후속 대조로 보완한다.

국내 최신 동일 조건 연구를 모두 확인한 것은 아니다. 검색 결과가 적다는 이유로 국내 최초라고 주장하지 않는다. 이전 Acclaim·메모리 회수 비교는 [기존 문서](RELATED_WORK.ko.md)에 보존하며 CPU 중심 이번 원고의 결과 근거로 확장하지 않는다.
