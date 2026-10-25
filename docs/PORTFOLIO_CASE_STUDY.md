# RamFlow — 제한된 하드웨어에서 더 나은 작업 경험을 만들기

**포트폴리오용 프로젝트 사례 / 작성 상태: 구현 확인 + 일부 성능 검증, 실제 사용자 효용 추가 측정 예정**

## 1. 왜 시작했나

RAM 가격 상승과 증설 비용에 대한 부담은 좋은 컴퓨터를 매번 새로 구매하기 어려운 사람들에게 현실적인 문제입니다. 특히 브라우저, IDE, 여러 앱을 동시에 쓰는 PC에서는 **이미 갖고 있는 메모리를 어떻게 관리할지**도 중요합니다.

제가 탐구한 질문은 다음과 같습니다.

> “물리 RAM을 늘리지 않고도, 제한된 PC에서 사용자가 현재 집중하는 작업을 더 안정적으로 유지할 수 있을까?”

목적은 숫자상의 RAM 여유 공간을 최대한 늘리는 것이 아닙니다. **현재 작업의 응답성·안정성을 해치지 않으면서 부족한 자원을 더 잘 배분하는 것**입니다.

## 2. 문제를 어떻게 정의했나

일반적인 '메모리 청소' 접근은 작업 중인 앱까지 무리하게 메모리에서 밀어내거나, 결과적으로 다음 접근에서 페이지를 다시 읽도록 만들 수 있습니다. RamFlow는 이 위험을 피하기 위해 **활성 작업 보호와 안전한 복원**을 핵심 요구사항으로 삼았습니다.

- **사용자:** 메모리 증설이 어렵거나 비용을 아끼려는 8 GB·16 GB Windows PC 사용자
- **제약:** Windows의 기존 메모리 관리와 협력해야 하며, 시스템 안정성·중요 작업·데이터를 해치면 안 됨
- **성공 기준:** 동일 사양·동일 작업에서 실행/전환/완료 시간, 페이지 입출력, RamFlow 오버헤드와 역효과까지 비교할 수 있어야 함

## 3. 기술적 접근

**관측 → 판단 → 제한적 개입 → 복원**이라는 반복 가능한 흐름으로 설계했습니다.

1. RAM/Commit/페이지 입출력과 CPU 등 지원되는 신호를 관측하고, EMA·히스테리시스·연속 확인을 통해 일시적 변화와 지속적 압박을 구분합니다.
2. Foreground, 시스템·보안, AI, 컴파일, 음악, 렌더 등의 중요한 작업을 보호합니다. 정상 상태나 전체화면에서는 개입을 억제합니다.
3. 오랫동안 비활성인 대상에 한해 Memory Priority, CPU Below Normal/EcoQoS 등을 제한적으로 조정하며, Working Set 축소는 압박 정도·대기 시간·쿨다운을 통과한 경우에만 수행합니다.
4. 대상의 PID 재사용, 소유권·세션, 원래 정책을 확인한 뒤 복원합니다. 강제 종료에 대비해 복원 저널을 유지합니다.

이 과정에서 **자동 프로세스 종료, 커널 후킹, Standby List 일괄 제거**는 채택하지 않았습니다. 기본 실행 모드는 Dry Run이며 사용자의 승인을 받기 전에는 실제 변경을 하지 않습니다.

## 4. 구현과 검증

- Windows 앱: **C#/.NET 10 + WPF**, 별도 엔진, 사용자 SID별 Named Pipe
- 기존 자동 검증: Windows API 왕복 및 IPC 검증, Mock 안전성 테스트, 저장소 자체 테스트, 실제 WPF 창 렌더링과 설치 흐름
- 저사양을 위한 **프로그램 자체 개선**: 0.3.3 대비 0.3.4 WPF 시작·9개 화면 순회에서 최대 상주량 중앙값 **347.6 → 165.0 MiB (약 53% 감소)**, Private Bytes **263.5 → 80.6 MiB (약 69% 감소)**. 두 값 모두 **RamFlow 앱 자체**의 측정 결과입니다.

> 이 수치는 RamFlow가 다른 앱을 53% 빠르게 만들었다거나, 사용 가능한 물리 RAM이 53% 증가했다는 뜻이 아닙니다. 제품 효과는 별도의 실사용 작업 측정이 필요합니다.

구체적인 측정 원문은 [검증 문서](VALIDATION.md), 향후 실사용 A/B/C 측정 계획은 [저사양 벤치마크 프로토콜](LOW_MEMORY_BENCHMARK.md)에 분리했습니다.

## 5. 개발하면서 배운 점과 다음 실험

- **절약과 성능 사이에는 트레이드오프가 있습니다.** Working Set을 줄이면 재접근 시 지연이 생길 수 있으므로 '얼마나 줄였나'보다 '사용자 작업이 빨라졌나'를 확인해야 합니다.
- **최적화 유틸리티는 스스로 가벼워야 합니다.** RamFlow 0.3.4의 UI 메모리 사용량 개선은 이 문제를 다시 돌아보게 한 측정입니다.
- **안전한 비개입이 유용한 결과일 수도 있습니다.** 압박이 없다면 건드리지 않고, 변경했다면 되돌릴 수 있어야 합니다.

다음 단계는 실제 8 GB 기기에서 브라우저+IDE 작업을 **미실행(Windows 기본 상태) / Dry Run / 안전한 실제 조정**으로 나눠 반복 측정하고, 결과가 악화되는 경우까지 공개하는 것입니다. 이 실험 결과가 나오기 전에는 모든 저사양 PC에서의 속도 향상을 약속하지 않습니다.

---

## Short English summary

**RamFlow — Making the most of the memory users already have.**

Motivated by rising upgrade costs, RamFlow explores whether constrained Windows PCs can better support active work through pressure-aware, conservative resource management. It protects foreground and important processes, intervenes only in sufficiently idle workloads, and restores original settings. It cannot create RAM or guarantee a performance boost.

Version 0.3.4 also reduced **RamFlow's own WPF process memory footprint** in a documented three-run comparison (347.6 → 165.0 MiB peak working-set median; 263.5 → 80.6 MiB private bytes). These are **not** measurements of other applications becoming faster. Repeatable 8 GB user-workflow benchmarks remain planned.

Source: [RamFlow](../README.md) · [Validation](VALIDATION.md) · [Benchmark plan](LOW_MEMORY_BENCHMARK.md).
