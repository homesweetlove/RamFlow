# CARP 기아 방지와 여섯 제거 실험

## 설계와 범위

기존 비용 평가·증거 검증·단계 선택을 유지하고, `CarpPolicy.Decide`의 마지막에 모든 프로필이 공유하는 `StarvationGuard`를 적용한다. 가드는 후보 제어를 추가하지 않고, 유지 가능한 제어의 상한을 줄이거나 `ResourceAction.None`으로 복원을 요구한다. 실제 적용·복원은 기존 엔진과 `OnActionChanged`가 담당한다. 이 변경은 Engine/Program, 저널, 기본 Dry Run 설정을 수정하지 않는다.

먼저 기존 구현에서 진행량 미상인 제한이 120초 후에도 유지되는 회귀 시험의 실패를 확인했다. 이후 실제 프로세스 API를 사용하지 않는 결정론적 `StarvationTests.Run(Action<bool,string>)`과 가드를 추가했다. 새 테스트의 기본 실행기 연결은 상위 작업에서 담당한다.

## 공개 설정과 진행량 계약

```csharp
new CarpOptions {
    Ablation = CarpAblation.None,
    Starvation = new StarvationOptions {
        StallSeconds = 20,
        MaxRestrictionSeconds = 120,
        RecoverySeconds = 30
    }
};

// source가 실제로 관측한 누적량과 작업 세대를 제공할 수 있을 때만 설정한다.
evidence with {
    BackgroundCompletedUnits = completedUnits,
    BackgroundProgressEpoch = jobEpoch
};
```

| 항목 | 검증 범위 / 의미 |
|---|---|
| `CarpOptions.Ablation` | 정의된 `CarpAblation` 값 하나. 플래그 조합이 아니다. 기본값 `None` |
| `CarpOptions.Starvation` | `null` 불가. 기아 방지를 끄는 설정은 없다 |
| `StarvationOptions.StallSeconds` | 10–300초, 기본 20초 |
| `StarvationOptions.MaxRestrictionSeconds` | 30–600초이고 `StallSeconds`의 두 배 이상, 기본 120초 |
| `StarvationOptions.RecoverySeconds` | 30–3600초, 기본 30초 |
| `CarpEvidence.BackgroundCompletedUnits` | 선택적 `double?`. 같은 작업 세대에서의 유한한 0 이상 누적 완료량 |
| `CarpEvidence.BackgroundProgressEpoch` | 선택적 `string?`. 제공 시 공백이 아닌 256자 이하 작업 세대 식별자 |
| `CarpEvidence.ObservedAt` | 누적량을 실제 관측한 시각. 수신 시각으로 바꾸지 않는다 |

두 진행량 필드가 모두 있어야 정체 감시용 기준점을 만들 수 있다. 동일 세대에서 더 최신 `ObservedAt`의 누적량이 증가했을 때만 마지막 진행 시각을 갱신한다. 최초 샘플은 기준점이며, 같은 시각의 중복·변조 샘플은 진행으로 세지 않는다. 세대 변경 또는 누적량 감소는 새 기준점만 만들고 기존 정체 시한과 총 제한 시한을 초기화하지 않는다. 측정 throughput인 `MeasuredBackgroundUnitsPerSecond`도 누적 완료량을 대신하지 않는다.

진행량이 처음부터 없으면 측정된 정체라고 단정하지 않고 최대 제한 시간으로 복원한다. 한 번 진행량 감시가 시작된 뒤 필드가 빠져도 이미 설정한 정체 시한은 유지한다. 증거 전체가 사라지거나 오래됐거나 신원·경합 문맥이 맞지 않으면 기존 정책 검증이 즉시 무개입/복원을 요구한다. NaN/Infinity/음수 완료량과 잘못된 세대 문자열도 실패 시 복원한다.

## 제한 해제와 복구

상태 키는 `(PID, 프로세스 생성 신원)`이다. 권고나 Dry Run 출력만으로 제한 시간을 시작하지 않는다. `OnActionChanged`의 실제 적용 통지 또는 `PolicyInput.CurrentAction`에서 확인한 적용부터 연속 제한 시간을 센다. 누적량 정체가 `StallSeconds`를 지날 때마다 현재 남아 있는 제어를 EcoQoS → 메모리 우선순위 → CPU Sets → CPU 우선순위 순서로 하나씩 제거한다. CARP의 기존 후보 공간에는 CPU Sets가 없으며, 이 순서는 이미 관측된 제어를 안전하게 해제하기 위한 것이다.

제어가 한 개라면 첫 정체 시한에서 완전히 복원한다. 가드가 낮춘 상한은 진행량이 다시 증가해도 같은 제한 구간에서 높아지지 않는다. 최대 제한 시간에 도달하면 진행 여부와 관계없이 완전히 복원한다. 복원 요청은 실제 복원이 확인될 때까지 원인을 유지하며 반복된다. 진행 회복이나 새 비용 추정이 복원 실패를 지우지 않는다.

엔진이 단계 변경을 위해 원래 값으로 복원한 뒤 곧바로 더 약한/다른 허용 제어를 적용하는 중간 콜백은 연속 제한 시계를 초기화하지 않는다. 예정된 재적용이 없고 다음 결정에서도 `CurrentAction.None`이면 실제 복원으로 확정한다. 완전 복원이 확인된 시점부터 `RecoverySeconds` 동안 재진입을 막는다. 기존 `CooldownSeconds`가 더 길면 그 쿨다운도 그대로 적용한다. 시계가 뒤로 가면 활성 제한을 복원하도록 요구한다.

실제 OS 복원 성공과 실행 주기 준수는 엔진의 책임이다. 가드는 다음 결정에서 복원을 요구하며, 멈춘 엔진을 대신 실행하거나 OS 쓰기 실패를 성공으로 만들 수 없다. 따라서 120초는 정책의 복원 요구 시한이며, OS 수준의 무조건적인 완료 보증이 아니다.

## 제거 실험 프로필

| `CarpAblation` | 제거하는 요소 | 유지하는 요소와 해석 |
|---|---|---|
| `None` | 없음 | 전체 CARP와 기아 방지 기준 |
| `WithoutBackgroundCost` | 비용식의 background loss 항만 0으로 계산 | 원래 추정값은 감사에 남긴다. 정체 감시와 제한 시간은 유지 |
| `WithoutReaccessCost` | 비용식의 reaccess risk 항만 0으로 계산 | 나머지 비용·증거 검증·기아 방지 유지 |
| `WithoutFeedback` | 같은 PID/생성 신원·실제 경합 종류·`ModelId` 문맥에서 후보 추정 집합을 최초 유효 값으로 고정 | **피드백 제거 프로필**이다. 최신 관측 효과나 실시간 비용 모델이라고 해석하지 않는다. 최신 신원·시각·경합·신뢰도·후보 유효성 검증과 실제 진행량 기반 기아 방지는 계속 수행 |
| `WithoutMemoryAwareness` | `LowerMemory`를 포함한 후보 선택을 억제 | 메모리가 건강하다고 꾸미지 않는다. 실제 RAM/Mixed 분류와 증거 문맥을 유지한다. 해당 후보는 비용 `null`과 억제 이유를 남긴다. 별도로 입증된 CPU 후보는 선택 가능 |
| `WithoutStaging` | 제어를 한 개씩 늘리는 후보 도달성 제한만 제거 | 후보 전체의 유효성·순효용·안정 샘플·쿨다운·기아 방지는 유지 |
| `WithoutStability` | 신규 후보의 안정 샘플 수를 1로 취급하고 적응 확대 쿨다운을 제거 | 단계 제한·입력 검증·기아 방지의 강제 복구 창은 유지. 일반 제어 쿨다운과 필수 복구 창을 구분한다 |

어떤 프로필도 foreground/보호 프로세스/명시적 background 대상/whitelist/활동 불명 보호, 신원 일치, 증거 신선도, 독립 블록 수, 신뢰도, CPU/EcoQoS 설정 검증을 제거하지 않는다. 기본 Dry Run과 기존 저널·감사 실패 시 안전 동작 역시 변경하지 않는다. `WithoutMemoryAwareness`는 RAM 전용 경합에서 CPU 동작을 만들어내지 않는다. `WithoutFeedback`은 새 증거의 잘못된 후보를 고정 모델로 가리지 않으며, 고정 후보가 현재 설정에서 비활성화되면 복원한다.

## 유한 상태와 감사

기존 정책 이력, 고정 모델 문맥, 가드 상태는 각각 최대 4096개다. 고정 모델은 마지막 사용 후 1시간이 지난 문맥을 정리하며, 재사용 시 새 실험 기준점을 만든다. 가드는 활성 제한·중간 복원 상태와 아직 유효한 복구 창을 용량 확보를 위해 버리지 않는다. 무제한 상태 증가 대신 새 신원/문맥에 대해 무개입/복원을 반환한다. 비활성 가드 상태는 마지막 사용 후 1시간 뒤 정리한다. 종료가 통지되지 않은 활성 신원이 계속 쌓이면 용량 고갈 시 안전하게 새 개입이 중단될 수 있다.

결정의 기존 이유 뒤에 `ablation=<enum>`과 가드 상태가 붙는다. 주요 상태는 `starvation-continue-progress-unknown-bounded`, `starvation-continue-progress-monitoring`, `starvation-stall-release`, `starvation-stall-restore-required`, `starvation-max-restriction-restore`, `starvation-recovery-window`, `starvation-clock-regressed-restore`다. `restricted-seconds`, `stalled-seconds`, `progress-known`, `recovery-seconds`를 함께 기록한다. 진행량 미상일 때 `stalled-seconds=0`은 정체가 없다는 측정이 아니며 반드시 `progress-known=False`와 같이 해석한다. 용량/신원 실패는 `starvation-state-capacity-or-identity`로 표시한다.

고정 후보 감사 이유는 `frozen-model-estimate-feedback-removed-not-live-measurement`이다. 후보 추정·실제 throughput·실제 완료량은 서로 다른 의미로 보존하며 합성 비용을 실측 개선량으로 보고하지 않는다.

## 검증 기록

격리 실행기에서 `StarvationTests.Run` **213개 assertion 통과**를 확인했다. 기존 `ResearchPolicyTests.Run`도 별도로 실행해 **49개 assertion 통과**를 확인했다. 기존 시험의 임시 파일은 쓰기가 허용된 workspace 안으로 `TEMP`/`TMP`를 지정했다. 새 테스트는 합성 시간과 `MockResourceProvider`만 사용하며 실제 프로세스 우선순위, EcoQoS 또는 메모리를 변경하지 않는다. 포함 범위는 모든 프로필의 안전 검증/정체/최대 시간/복구 창, 진행량 미상·중복·지연·정체·세대 및 카운터 초기화, PID 재사용, 복원 실패·단계 변경 콜백·쿨다운·용량 상한, 각 제거 요소의 실제 결정 차이다.

기존 Windows+IPC 274개 assertion은 이전 작업의 확인 기록이다. 이번 하위 작업은 실제 프로세스 동작을 실행하지 않았고, 공유 브랜치의 다른 구현 파일이 작성 중인 시점에 전체 native 빌드가 새 `CostModelTests`의 미완성 타입 참조로 막혔다. 전체 공유 테스트 및 실행기 연결의 최종 확인은 상위 작업에서 순차적으로 수행한다. 이 기능 검증은 실제 앱 성능 개선·모델 보정·기아 없음의 실험적 입증이 아니다.
