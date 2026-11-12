# 관련 연구 보강: 적응형 배경 제어의 근거와 반증 조건

확인일 **2026-11-10**. 검색 대상 기간은 **2021-01-01–2026-11-10**이며, 아래 **8개 1차 자료**를 선정했다. Windows 공식 자료 3개(한국어 문서 포함), 정식 학회 논문 3편, 개발자 RFC 1건, 사전공개 논문 1편이다. [기존 관련 연구](RELATED_WORK.ko.md)의 MemClean·Mem Reduct·Windows QoS·Acclaim·Heracles 비교를 보완한다. 이번 작업은 문헌 확인이며 새 성능 실험이나 동시 진행 중인 구현 변경의 검증은 수행하지 않았다. 체계적 문헌고찰이나 신규성 증명으로 해석하지 않는다.

## 1. 간결한 근거 행렬

| ID·출처 | 원문에서 확인한 근거 | CARP에 주는 의미·겹치는 선행 개념 | 한계·불리한 조건 |
| --- | --- | --- | --- |
| [U1 — EcoQoS, 2021](https://devblogs.microsoft.com/performance-diagnostics/introducing-ecoqos/) | Windows 10 Insider Preview Build 21359부터 EcoQoS를 소개하며, API로 표시한 작업의 효율적인 코어·주파수 선택을 설명한다. | 배경 작업의 전력·열 영향을 줄이는 수단은 기존 OS 기능이다. EcoQoS 호출 자체는 새 정책이 아니다. | 도입 당시 하드웨어 지원·튜닝 범위의 설명이다. 전경 지연이나 배경 완료시간의 보장이 아니며 모든 현재 PC에 같은 효과를 옮길 수 없다. |
| [U2 — Efficiency mode, 2022](https://devblogs.microsoft.com/performance-diagnostics/reduce-process-interference-with-task-manager-efficiency-mode/) | 낮은 base priority와 EcoQoS를 결합한다. 공개 평가는 코어별 정상 우선순위의 쉬지 않는 합성 CPU 작업 아래 앱 실행·Start 메뉴 등의 완료시간을 비교한다. | 전경 보호와 CPU 제어 묶음의 직접적인 Windows 선행 사례다. 고정 정책을 강한 기준선으로 남겨야 한다. | 개발사 평가이며 RAM 조치의 독립 효과·배경 유용 처리량 손실까지 입증하지 않는다. 이를 메모리 최적화의 성능 근거로 인용하지 않는다. |
| [U3 — 서비스 품질, 한국어 공식 문서, 2025 갱신](https://learn.microsoft.com/ko-kr/windows/win32/procthread/quality-of-service) | QoS와 스케줄링 우선순위를 구분한다. 창의 포커스·가시성·오디오에 따른 분류 및 배터리에서 무입력 후 전경 QoS가 내려가는 기능을 설명한다. | 무개입 A에도 OS의 전경 인지 정책이 존재한다. 한국어 자료이나 국내 독립 연구는 아니다. | 무입력 자동화 시험은 QoS 변화로 왜곡될 수 있다. 전원·입력 조건과 OS build를 기록해야 하며, 문서상 기능이 모든 시험 장치에서 활성화됐다고 가정하지 않는다. |
| [U4 — TMO, ASPLOS 2022](https://www.cs.cmu.edu/~dskarlat/publications/tmo_asplos22.pdf) | §3.2·§4.3은 PSI 기반 피드백과 메모리 접근 지연에 대한 앱별 민감도를 다룬다. 같은 fault/promotion 빈도도 저장장치 속도에 따라 의미가 다를 수 있다. | 고정 메모리 임계값보다 실제 손실·재접근 비용을 보자는 근거다. 민감도 기반 적응이나 재접근 비용 고려 자체는 선행 개념이다. | Linux 커널·컨테이너 offloading이다. §5.2에서는 zswap/SSD backend 선택이 수동이며, §5.3은 reclaim 관측 비용이 목표 paging 빈도에 따라 증가함을 설명한다. Windows 메모리 우선순위의 효과와 동일시하지 않는다. |
| [U5 — Cilantro, OSDI 2023](https://romilb.com/files/cilantro_osdi23_paper.pdf) | 온라인 자원–성능 학습과 신뢰구간을 정책에 연결한다. §5·§7.3은 피드백 결측, 예측 오차, 지속적인 부하 급변 및 정책 계산 비용을 다룬다. | CPU 사용률 같은 대리지표보다 실제 latency/throughput을 목표로 삼고 추정 불확실성을 판단에 포함하는 설계는 이미 존재한다. | 단일 교환 가능한 자원 유형이 기본이다. 지속적인 극단적 부하 변동은 성능을 해치며 단조 효용 가정에도 한계가 있다. 인위적 신뢰구간 오차 실험은 임의의 앱·장치 OOD 보장이 아니다. |
| [U6 — EEVDF 기아 RFC, 2024](https://lkml.indiana.edu/hypermail/linux/kernel/2402.3/05860.html) | 저자는 계층적 cgroup의 deadline 갱신·재가중과 지역 runqueue 검사 때문에 특정 조건에서 다른 작업이 실행되지 않을 수 있는 경로와 수정안을 제시한다. | 평균 지연·전체 진행량만으로 개별 작업의 기아를 배제할 수 없다는 구현 사례다. | Linux 개발 메일의 RFC 보존본이다. 채택·수정 완료·현행 커널의 결함 또는 Windows의 동일 결함을 확인한 자료가 아니다. |
| [U7 — Credit Fairness, IJCAI 2026](https://ijcai.org/proceedings/2026/0051.pdf) | 시간에 따라 수요가 변하는 반복 할당에서 순간적 max–min 공정성과 누적 자원 수혜의 공정성을 구분한다. 특정 가정 아래 공정성·효율·전략적 성질을 모두 만족시키는 데 한계가 있음을 보인다. | 배경 손실 평균에 더해 작업별 누적 양보·진행량을 보자는 근거다. 이는 본 연구로의 설계적 추론이다. | 동질 자원·고정 기여량·상한 선형 효용 등의 이론 모형이다. Windows의 유한 최대 대기시간이나 완료시간 보장으로 옮길 수 없다. |
| [U8 — EdgePace v1, 2026 사전공개](https://arxiv.org/html/2610.37307v1) | §2·§3·§4.2는 연산 여유가 있어도 공유 메모리 대역폭 경쟁이 전경을 지연시킬 수 있음을 보이고, 압박·deadline 여유·출력 freshness를 함께 판단한다. | CPU 사용률만으로 간섭을 판별하기 어렵고, 배경의 지연 한계도 함께 고려해야 한다. Hold뿐 아니라 Bypass를 선택하는 적응도 선행 사례다. | Android 휴대전화·Jetson 및 수정한 추론 runtime의 평가다. Windows 일반 프로세스에 같은 제어 권한은 없다. 그래프 분할·센서 수집 비용과 배경 처리량 손실이 있으며, 사전공개 결과를 독립 재현·심사 완료 결과로 쓰지 않는다. |

## 2. 판단 모델과 반드시 드러낼 실패

- **왜 적응형인가, 언제 고정형이 더 나은가.** U4·U5는 작업·장치·부하에 따라 자원–성능 관계가 변한다는 근거다. 그러나 안정된 CPU 경쟁에서는 고정 제어가 충분하고 모델·관측 비용도 작을 수 있다. 짧은 burst는 센서 지연·진입 안정화·쿨다운보다 빨라 적응형이 늦게 반응할 수 있다. 이는 CARP에서 시험할 가설이지 선행 논문이 입증한 Windows 결과가 아니다. [현 프로토콜](PROTOCOL.ko.md)의 C는 CPU 중심이고 E는 메모리 조치도 허용하므로, **동일 CPU 후보만 허용한 E와 C의 추가 비교** 없이 C–E 차이를 적응형 판단만의 효과라고 부르지 않는다. 추가 비교는 제안이며 수행하지 않았다.
- **비용·지연·처리량의 단위를 연결한다.** [CARP 명세](CARP.ko.md)의 `J(a) = -w_f g_a + w_b b_a + w_r r_a + w_o o_a`, `J(NONE)=0`에서 정규화 분모·예측 horizon·가중치는 별도 검증 대상이다. 전경 p95의 ms, 배경 유용 작업/s, 유한 작업의 제출→완료시간, 재접근 위험, 관리 비용을 원단위로도 제시한다. 처리량 감소율을 같은 크기의 완료시간 증가율로 바꾸지 않는다. 예상 손실이 예산을 넘는 경우와 개입의 순이득이 불확실한 경우에는 무개입도 비교 대상이다.
- **예측 정확도·OOD.** 명세의 외부 입력 `confidence >= 0.8`은 보정된 80% 정답률이나 80% 신뢰구간을 뜻하지 않는다. 관측 TTL·PID 신원 검사는 오래된 자료·대상 혼동을 막지만 새 앱 버전·RAM 크기·저장장치·전원 상태의 분포 이탈 탐지를 대신하지 않는다. 세션·앱·장치를 통째로 분리한 검증에서 후보별 이득/손실 오차, 이득 부호 오판, 배경 예산 초과율, 무개입 비율을 함께 보고한다. 오차 구간을 쓰면 실제 coverage도 측정한다. U5의 제한된 노이즈 실험을 OOD 정확도로 인용하지 않는다. 반사실적 최적 조치와의 regret은 같은 조건의 후보별 실측을 확보했을 때만 산출한다.
- **CPU와 RAM을 분리하고 무효 조치도 남긴다.** U2의 CPU 제어 효과가 RAM 효과는 아니다. 메모리 용량·commit 압박, paging 비용, 공유 대역폭 경쟁도 서로 다르다(U4·U8). 메모리 우선순위는 강제 할당 해제·working set trim·대역폭 예약과 다르다([기존 문서 §3](RELATED_WORK.ko.md#3-windows-메모리-지표의-해석)). healthy·CPU 중심·RAM 중심·mixed에서 CPU-only와 memory-only 절제를 비교한다. 용량/재접근 위험을 잘못 판단한 RAM 조치는 배경 재접근과 I/O를 늘릴 수 있다. 전경이 기다리는 언어 서버·빌드 같은 의존 작업을 낮추면 전경도 느려질 수 있다. OS가 이미 충분히 보호하거나 병목이 제어 불가능한 경우의 무효·악화도 결과다. 마지막 세 문장은 검증할 실패 가설이다.
- **기아는 평균 손실과 별도 제약이다.** U6은 구현 경로의 기아 사례, U7은 누적 공정성의 이론 근거이며 같은 보장은 아니다. 작업별 최대 시작 대기, 실행 중 연속 무진행 구간, 누적 유용 진행, 미완료/검열을 긴 부하 구간에서 측정한다. 본 실험 전에 대기 한계와 관측 창당 최소 유용 진행 기준을 정한다. 큐의 시작 보류와 실행 중 작업의 저우선순위를 구분한다. 비용 점수·쿨다운·원복만으로 유한 대기를 보장했다고 쓰지 않는다. aging·양보 예산·진행 구제의 효과를 주장하려면 실제 구현과 시험이 필요하며, 구제 때문에 전경 지연이 악화되는 절충도 보고해야 한다.
- **오버헤드는 순효과에 포함한다.** U8 Table 2의 PMU read 중앙값은 2 ms 간격에서 80.9 μs(간격의 4.04%), 20 ms에서 130.8 μs(0.65%)다. 이 비율은 전체 CPU 사용률이나 앱 slowdown이 아니다. U5 Fig. 13도 모델 갱신·정책별 할당 계산 시간을 분리한다. CARP에서는 센서·모델 입력·판단·동기 로그·setter·복구의 총비용을 측정하고, 같은 구성의 B–A와 E–B를 구분한다. 짧은 샘플 주기는 반응성을 높이는 대신 비용·교란을 늘릴 수 있다. 긴 주기는 burst를 놓치거나 낡은 예측을 사용할 수 있다. 평가에서 오버헤드를 사후 차감해 이득을 만들지 않는다.

## 3. 검증 가능한 차별점 후보

아래는 **선행 개념의 발명 주장이 아닌, 이 Windows 구현에서 검증할 가설**이다. 실용적 차이·비열등 한계·손실 예산은 본 실험 전에 정하고, 현재 미확정 값을 결과처럼 채우지 않는다.

| 후보 | 필요한 비교·관측 | 기여를 지지하지 못하는 결과 |
| --- | --- | --- |
| N1. 비용을 고려한 개입/무개입이 고정 CPU 제어보다 나은 절충을 낸다. | 동일 대상·후보·보호·관측 비용 아래 CPU 제한 E–C 대응 비교. healthy와 경쟁 조건 모두에서 전경 지연·배경 진행량을 측정한다. | 조치 구성 차이를 제거하면 이득이 사라짐, 배경 예산 초과, 또는 고정형과 차이 없이 관리 비용만 증가. |
| N2. 불확실성을 반영한 판단이 잘못된 개입을 줄인다. | 미사용 앱/장치 및 오차·지연·결측 입력에서 동일 후보·비용 항목·신원/TTL 보호를 유지한 불확실성 반영/미반영 비교를 제안한다. 이득 부호 오판·예산 초과·무개입 비율을 함께 제시한다. | 높은 입력 confidence에도 손실 한계 위반. 항상 무개입하여 위험만 줄인 결과는 유효한 성능 개선 증거가 아니다. |
| N3. 메모리 민감도·재접근 비용이 CPU 제어와 구별되는 설명력을 가진다. | RAM/mixed의 CPU-only·memory-only 및 재접근 항 제외 절제. 지연·유용 진행·귀속 가능한 page-in/재접근 자료를 함께 수집한다. | CPU 조치만으로 동일 효과, 비용 항이 선택을 바꾸지 않음, 또는 resident 감소만 있고 작업 성능 이득 없음. |
| N4. 정상 상태 비용과 장기 배경 진행을 함께 감사할 수 있다. | 전체 상주 비용·실제 조치/원복·작업별 대기/진행·미완료를 보존하고 사전 한계로 판정한다. | 평균은 좋아도 특정 작업 기아, 비용 한계 초과, 복구 미확인. 로그 존재나 짧은 기능 시험만으로 성능·기아 방지를 입증하지 못함. |

신규성은 위의 구체적 결과와 공개 가능한 재현 자료에 달려 있다. 전경 인지, 적응형 제어, 신뢰구간, 비용 가중합, 무개입 또는 공정성 개념만으로 **최초·유일·일반적 우월성**을 주장하지 않는다.

## 4. 정확한 서지와 원문 확인 범위

URL 확인·접근 시도일은 모두 2026-11-10이다. 논문 제목·저자·연도·식별자를 1차 본문/공식 서지와 대조했다. DOI가 없는 문서에 식별자를 만들어 넣지 않았다. 아래 DOI 미제공 표기는 해당 공식 서지의 표기 범위이며 DOI의 절대적 부재를 단정하지 않는다.

- **U1.** Raymond Li. **2021-04-21**. *Introducing EcoQoS*. Microsoft, Performance and Diagnostics Dev Blog. [공식 원문](https://devblogs.microsoft.com/performance-diagnostics/introducing-ecoqos/). Introduction·Motivation·Supported Hardware·API Support 확인. 학회 논문 아님; DOI 해당 없음.
- **U2.** Claton Hendricks. **2022-02-16**. *Reduce Process Interference with Task Manager Efficiency Mode*. Microsoft, Performance and Diagnostics Dev Blog. [공식 원문](https://devblogs.microsoft.com/performance-diagnostics/reduce-process-interference-with-task-manager-efficiency-mode/). The Feature Explained·Impact의 시험 조건 확인. 학회 논문 아님; DOI 해당 없음.
- **U3.** Microsoft. *서비스 품질* (*Quality of Service*). Microsoft Learn, Win32 apps. 페이지 표시 최종 갱신 **2025-12-09**. [한국어 공식 원문](https://learn.microsoft.com/ko-kr/windows/win32/procthread/quality-of-service). QoS 수준·분류·사용자 비활성 기능과 벤치마크 주의 문단 확인. 최초 발행일은 이 페이지에서 확인하지 못함; DOI 해당 없음. 기존 R3의 한국어·현행 문서 보강이며 독립 논문 1편을 추가한 것으로 세지 않는다.
- **U4.** Johannes Weiner, Niket Agarwal, Dan Schatzberg, Leon Yang, Hao Wang, Blaise Sanouillet, Bikash Sharma, Tejun Heo, Mayank Jain, Chunqiang Tang, Dimitrios Skarlatos. **2022**. *TMO: Transparent Memory Offloading in Datacenters*. Proceedings of the **27th ACM International Conference on Architectural Support for Programming Languages and Operating Systems (ASPLOS '22)**, **2022-02-28–03-04**, Lausanne, Switzerland. ACM, **13 pages**(원문 ACM Reference Format). **DOI [10.1145/3503222.3507731](https://doi.org/10.1145/3503222.3507731)**. [저자 대학 공개 PDF](https://www.cs.cmu.edu/~dskarlat/publications/tmo_asplos22.pdf). 첫 장 서지 및 §3.2·§4.3·§5.2–5.3 본문 확인. DOI 값은 PDF에서 확인했으나 DOI landing page는 도구로 열리지 않았다; 최종 proceedings 페이지 범위는 별도로 확정하지 않았다.
- **U5.** Romil Bhardwaj, Kirthevasan Kandasamy, Asim Biswal, Wenshuo Guo, Benjamin Hindman, Joseph Gonzalez, Michael Jordan, Ion Stoica. **2023**. *Cilantro: Performance-Aware Resource Allocation for General Objectives via Online Feedback*. **17th USENIX Symposium on Operating Systems Design and Implementation (OSDI 23)**, **2023-07-10–12**, Boston, MA, pp. **623–643**. USENIX Association; ISBN **978-1-939133-34-2**. [공식 서지](https://www.usenix.org/conference/osdi23/presentation/bhardwaj), [저자 공개 PDF](https://romilb.com/files/cilantro_osdi23_paper.pdf). 공식 검색 색인의 BibTeX와 저자 PDF 표지·본문 §1·§5·§7.3·Fig. 13 대조. 공식 BibTeX에 DOI 미제공. 공식 페이지/PDF 직접 요청은 403으로 실패해 저자 사본으로 본문을 확인했다.
- **U6.** Tobias Huschle. **2024-02-28**. *[RFC] sched/eevdf: avoid task starvation in cgroups*. Linux Kernel Mailing List 개발 메일. [저자 메일·patch 보존본](https://lkml.indiana.edu/hypermail/linux/kernel/2402.3/05860.html). 날짜·계층 예시·`update_deadline` 수정 diff 확인. 심사 논문·채택된 커널 명세 아님; DOI 해당 없음. lore.kernel.org 직접 요청은 403으로 실패했고 원 메일의 Message-ID·채택 commit은 확정하지 않았다.
- **U7.** Seyed Majid Zahedi, Rupert Freeman. **2026**. *Credit Fairness: Online Fairness in Shared Resource Pools*. **Proceedings of the Thirty-Fifth International Joint Conference on Artificial Intelligence (IJCAI-26)**, Main Track, pp. **446–453**. **DOI [10.24963/ijcai.2026/51](https://doi.org/10.24963/ijcai.2026/51)**. [공식 서지·본문](https://ijcai.org/proceedings/2026/51), [공식 PDF](https://ijcai.org/proceedings/2026/0051.pdf). §1–3·Table 1의 모형·공정성 구분 확인. 서지의 저자·페이지·DOI와 PDF 첫 장 대조; BibTeX 다운로드는 Cache miss였으므로 사용하지 않았다.
- **U8.** Teng Mei, Chengxuan Pei, Weijun Wang, Jie Wu, Marco Canini, Yunming Xiao. **2026-10-27** 제출, **arXiv:2610.37307v1 [cs.PF]**. *EdgePace: Deadline- and Freshness-Aware LLM Scheduling on Unified-Memory Platforms*. **표시 DOI [10.48550/arXiv.2610.37307](https://doi.org/10.48550/arXiv.2610.37307)**(arXiv/DataCite 식별자; v1 페이지에 **pending registration** 표시, 등록·해결 완료 미확인). [공식 메타데이터](https://arxiv.org/abs/2610.37307v1), [버전 고정 HTML](https://arxiv.org/html/2610.37307v1), [PDF](https://arxiv.org/pdf/2610.37307v1). §2–4·Table 2·Appendix B 확인. 확인한 레코드에 학회 게재 정보 없음. Comments는 15 pages라고 하지만 도구가 읽은 PDF는 19쪽으로 보고되어 페이지 수를 확정 서지로 쓰지 않았다. PDF 이미지 캡처는 Cache miss로 실패했으며 수치 확인은 HTML/추출 텍스트 범위다.

## 5. 검색·접근 실패와 남은 공백

| 실제 검색·확인 경로 | 결과·제한 |
| --- | --- |
| `Windows 11 EcoQoS April 2021 Microsoft foreground background scheduling`; `2021 2022 2023 2024 2025 foreground background latency throughput memory contention scheduling USENIX` | Windows 공식 자료와 데이터센터/모바일 인접 연구를 분리해 선정했다. 2026 후보는 별도로 arXiv 제출일·버전·원문을 확인했다. 지정 기간의 모든 연구를 포괄한 검색은 아니다. |
| `site.kci.go.kr 2021 2022 2023 2024 "메모리" "전경"`; `site.koreascience.kr "스케줄링" "기아" 2021 2022 2023 2024`; `site.riss.kr "윈도우" "스케줄링" "2024"` | 이번 결과에서 기간·주제·1차 원문을 함께 검증할 국내 Windows 데스크톱 논문을 확보하지 못했다. 검색어의 통신/영상 등 동음이의 결과는 제외했다. **국내 연구가 없다는 결론은 아니다.** 한국어 공식 자료는 U3으로 확보했다. |
| USENIX Cilantro 공식 페이지/공식 PDF, lore.kernel.org, TMO DOI landing page | 직접 접근 실패를 U4–U6에 명시했다. 저자 PDF·개발 메일 보존본으로 대체한 범위와 공식 검색 색인 확인을 구분했다. 403을 자료 부재로 해석하지 않았다. |
| Sinan 저자 PDF `https://www.csl.cornell.edu/~delimitrou/papers/2021.asplos.sinan.pdf` | 직접 열람이 timeout으로 실패했다. 이번 8개 근거에는 포함하지 않았고 상세 알고리즘·성능을 인용하지 않았다. |
| IJCAI BibTeX, EdgePace PDF 이미지 캡처·페이지 메타데이터 | Cache miss와 페이지 수 불일치를 U7–U8에 남겼다. 열람·시각 검수·서지 내보내기 성공을 서로 대체하지 않았다. |

남은 공백은 **같은 Windows 장치의 전경 지연·배경 진행·모델 오차·개입 비용·장기 기아를 함께 실측한 CARP 근거**다. 인접 플랫폼 논문이나 API 문서는 이 공백을 메우는 실험 결과가 아니다. 본 문서의 반증 조건을 통과하기 전까지 우월성·예측 정확도·기아 방지·낮은 오버헤드의 수치를 확정하지 않는다.
