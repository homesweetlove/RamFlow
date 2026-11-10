# ResearchLab 결과 분석 도구

`scripts/research_analyze.py`는 Python 3.10 이상 **표준 라이브러리만** 사용하는 읽기 전용 분석기다. ResearchLab의 재귀 `**/outcome.json`을 읽고 새 디렉터리에 CSV·JSON·SVG·Markdown을 생성한다. 실험, 실제 부하, 엔진, 관측기, 모델 학습을 실행하지 않는다. C# 학습 DTO와도 독립적이다. 의존성 설치가 필요 없다.

산출물은 기술통계와 탐색적 대응 비교다. 조건 A 하나의 개선, Pareto 위치, bootstrap CI, 합성 fixture만으로 정책 성공이나 실제 앱 성능을 선언하지 않는다. 테스트에 쓰이는 숫자는 모두 `SYNTHETIC TEST DATA`로 표시한 가상 자료다.

## 실행

저장소 루트에서 다음과 같이 실행한다. `--foreground-tolerance-ms`는 **필수**이며, 결과를 보기 전에 정한 허용 전경 p95 증가량(ms)을 넣는다. 아래 `1.0`은 명령 형식 설명용 숫자이며 권장 임곗값이나 사전등록된 값이 아니다.

```powershell
python -B -S scripts/research_analyze.py --input work/research/run-001 --output work/research/analysis-001 --foreground-tolerance-ms 1.0 --bootstrap-iterations 10000 --seed 20261110
```

출력은 입력 트리 **밖의 새 디렉터리**여야 한다. 빈 디렉터리라도 이미 존재하면 거부한다. `--force`, 덮어쓰기, 이어 쓰기 기능은 없다. 잘못된 인수·선택 manifest는 출력 생성 전에 거부한다. 쓰기 중 오류가 발생하면 부분 디렉터리를 남기며 재실행 시 새 경로를 사용한다. 원본 파일은 수정하지 않는다. 입력은 분석 중 변경하지 않는 동결된 스냅샷이어야 한다.

| 인수 | 의미 |
| --- | --- |
| `--input PATH` | 재귀 탐색할 기존 디렉터리, 필수 |
| `--output PATH` | 존재하지 않는 출력 디렉터리, 필수 |
| `--foreground-tolerance-ms NUMBER` | 대응 블록 평균 전경 p95의 허용 증가량, 유한·0 이상, 필수 |
| `--bootstrap-iterations INT` | 기본 2000, 최소 100. 충분한 반복 수가 적은 독립 블록 수를 보완하지는 않음 |
| `--seed INT` | 기본 20261110. 대비별 실제 seed도 출력에 기록 |
| `--expected-outcomes PATH` | 예정 결과 경로를 명시해 파일 자체가 없는 시행을 보존하는 선택 JSON |
| `--p-values PATH` | 외부에서 계산한 사전 선언 family의 완전한 p-value 집합, 선택 JSON |

`-S`는 site 패키지를 로드하지 않는다. 그래프도 SVG 문자열을 직접 작성하므로 matplotlib, NumPy, 브라우저, 외부 폰트, 인터넷 없이 생성된다.

## 입력 계약: schema_version 2

파일명은 정확히 `outcome.json`이어야 한다. `reset.outcome.json`, `warmup.outcome.json`, 구버전 스케줄, 로그를 측정 결과로 취급하지 않는다. 다음 필드를 검증하며 알 수 없는 추가 필드는 원본 outcome에 보존한다.

| 필드 | 형식·의미 |
| --- | --- |
| `schema_version` | 정수 `2` |
| `session_block_id`, `hardware_id`, `workload_id` | 비어 있지 않은 문자열 |
| `split` | `fit`, `validation`, `test`, `pilot` |
| `condition` | `A`–`F` 또는 `calibration` |
| `regime` | **실제 관측** `cpu`, `ram`, `mixed`, `healthy` |
| `intended_regime` | 의도한 regime 문자열. 관측 regime을 대체하지 않음 |
| `regime_attained` | 실제 조건 달성 여부, Boolean |
| `candidate_action` | 정수 `0`, `1`, `3`, `4`, `5`, `7`. 정책 동작을 실행하거나 추론하지 않음 |
| `status` | `completed`, `failed`, `aborted`, `invalid` |
| `foreground_latency_ms` | 유한·0 이상 숫자 배열. 빈 배열은 보존하되 효과 분석에서 제외 |
| `foreground_wall_ms` | 선택 배열. 있으면 동일 요청 수의 유한·0 이상 숫자 배열 |
| `background_units` | 유한·0 이상 유용 작업량 |
| `measurement_seconds` | 유한·0 이상 측정 창. 0이면 보존하되 효과 분석에서 제외 |
| `background_completion_seconds`, `reaccess_ms` | 유한·0 이상 숫자 또는 명시적 `null` |
| `correctness_passed`, `restored` | Boolean |
| `policy_variant` | 비어 있지 않은 문자열. F의 제거 기전도 명확히 구분 |
| `model_id` | 비어 있지 않은 문자열 또는 명시적 `null` |
| `overhead_samples` | 아래 overhead 객체 배열. 빈 배열 허용 |
| `failures` | 원래 실패 세부사항 배열. 문자열·객체를 그대로 보존 |

각 overhead 객체의 필수 필드는 다음과 같다.

- `phase`: `idle`, `observe`, `evaluate`, `intervene`, `restore`
- `role`: `engine-host`, `foreground-fixture`, `background-fixture`, `observer`, `tray`, `service`
- `cpu_percent`, `private_bytes`, `working_set_bytes`: 유한·0 이상 숫자
- `decision_ms`, `log_write_ms`: 유한·0 이상 숫자 또는 명시적 `null`

숫자 자리에 Boolean, 음수, `NaN`/`Infinity`, overflow된 JSON 실수, 중복 JSON 키, 필수 필드 누락은 허용하지 않는다. CPU percent의 코어 수 정규화는 producer 정의에 따르며 분석기가 100% 상한이나 다른 정규화를 가정하지 않는다. 완료시간·재접근 결측을 처리량에서 역산하지 않는다.

## 제외·실패·없는 파일 보존

`ledger.json`은 발견하거나 명시적으로 예정한 결과마다 한 행을 갖는다. 상대 경로, 원본 SHA-256, 상태, schema 검증 오류, 효과 분석 포함 여부, 제외 사유, 원래 outcome 전체를 담는다. JSON 파싱 실패 시 가능한 원시 텍스트와 원본 해시를 남긴다. 깨진 UTF-8은 표시용 대체 문자를 쓰므로 정확한 원본 바이트는 원래 파일과 해시로 확인한다. 링크로 입력 경계 밖의 파일을 읽지 않는다.

효과 분석 포함 조건은 유효한 schema, `completed`, calibration이 아님, `regime_attained=true`, `correctness_passed=true`, `restored=true`, 빈 `failures`, 전경 요청 존재, 양수 측정 창이다. 입력에 잘못 보고된 실제 regime을 의도한 regime으로 다시 분류하지 않는다. regime 달성 여부 자체는 생산자의 측정·검증 책임이다.

동일 `(split, hardware, workload, observed regime, session block, condition, policy variant, model ID)`가 여러 파일에 나오면 모두 `duplicate_policy_block`으로 제외한다. 가장 좋은 결과, 처음 파일, 마지막 파일을 선택하지 않는다. 실패·중단·미복구·잘못된 결과가 지연 0이나 성공으로 변환되지 않는다. `failures.*`는 실행 실패뿐 아니라 calibration 등 모든 효과 분석 제외를 담는 ledger의 부분집합이다.

파일이 없는 시행은 재귀 탐색만으로 알 수 없다. 예정표를 다음 형식으로 명시해야 `status=missing`, `missing_outcome`으로 남는다. 경로는 입력 디렉터리 기준 상대 경로이며 `..`와 외부 경로는 거부한다. 선택적으로 pairing 식별자를 넣을 수 있다. 존재하는 결과가 선언된 식별자와 다르면 효과 분석에서 제외한다.

```json
{
  "outcomes": [
    {
      "path": "block-001/A/outcome.json",
      "session_block_id": "block-001",
      "condition": "A",
      "hardware_id": "machine-01",
      "workload_id": "fixture-01",
      "split": "test",
      "regime": "cpu"
    },
    {
      "path": "block-001/E/outcome.json",
      "session_block_id": "block-001",
      "condition": "E"
    }
  ]
}
```

manifest가 없으면 미발견 시행을 열거할 수 없다는 경고가 항상 표시된다. 기존 runner의 `schedule.json`을 이 manifest 형식으로 자동 해석하거나 구버전 결과를 v2로 승격하지 않는다.

## 독립 단위와 대응 추정

요청·프레임·overhead 샘플 수를 독립 N으로 사용하지 않는다. 먼저 각 outcome/블록의 전경 p50·p95를 계산한다. 분위수는 정렬한 값의 위치 `(n−1)q`에서 선형 보간하는 type 7이다. **p99는 해당 블록·해당 채널에 1000개 이상 값이 있을 때만** 계산한다. 여러 작은 블록의 요청을 모아 p99를 만들지 않는다. wall 지표도 latency와 별도로 같은 규칙을 적용한다.

배경 처리량은 `background_units / measurement_seconds`이다. 장치별 표는 블록 요약값의 동일 가중 평균이다. 요청이 1개인 블록과 999개인 블록의 가중치는 동일하다. 지표별 유효 블록 수를 따로 기록하며 `null`을 0으로 대체하지 않는다.

각 `(split, hardware_id, workload_id, observed regime)` 층 안에서 정책의 모든 쌍을 비교한다. 정책 정체성은 `(condition, policy_variant, model_id)`다. 조건/variant/model의 결정적 순서에서 앞을 baseline, 뒤를 candidate로 삼으며 부호는 항상 **candidate − baseline**이다. 장치·workload·regime·split을 넘는 대응이나 평균을 만들지 않는다. F는 동일 층·동일 블록의 E와 비교한 별도 ablation 표에도 들어간다.

각 대비에 대해 양쪽의 유효한 결과가 있는 동일 `session_block_id`만 대응한다. 지표별 결측 쌍도 별도로 센다. `pairing.*`은 각 정책쌍의 알려진 블록 합집합, 파일 경로, 짝 부재와 제외 사유를 보여 준다. schema 자체가 깨졌거나 없는 파일의 상세 내용은 ledger를 확인한다.

대응 블록 값 `(xᵢ, yᵢ)`에 대해 다음을 출력한다.

- 원 단위 평균 차이: `mean(yᵢ−xᵢ)`, ms·s·units/s
- 평균 비: `mean(yᵢ)/mean(xᵢ)`. 블록 비의 평균이 아니며 baseline 평균이 0이면 정의하지 않음
- 대응 표준화 효과 `d_z = mean(yᵢ−xᵢ) / sample_sd(yᵢ−xᵢ)`. 표본 SD는 `n−1` 분모, 작은 표본 보정 없음

95% percentile bootstrap은 N개의 **대응 블록 쌍 전체**를 복원추출하고 매번 같은 추정량을 계산한다. 요청 배열에서 재표집하지 않으며 baseline/candidate를 따로 섞지 않는다. master seed와 대비 식별자의 SHA-256에서 실제 seed를 결정하므로 입력 탐색 순서와 무관하다. 점 추정은 한 쌍에서도 가능하지만 **N<2의 CI는 `null`**이다.

분모가 0인 비, SD가 0인 `d_z`, 해당 bootstrap draw 수를 명시한다. undefined draw가 하나라도 있으면 해당 지표 CI 전체를 `null`로 남기며 정의되는 draw만 골라 interval을 만들지 않는다. 특히 소표본 `d_z` CI는 자주 정의되지 않는다. `d_z`는 의존 구조에 민감하므로 일반적인 독립집단 Cohen d나 다른 실험의 효과 크기와 직접 비교하지 않는다.

블록 독립성, 무작위 순서, carryover 통제, 장치 대표성은 파일 형식이 보증하지 않는다. 소수 블록, 완전사례 선택, 결측·실패 편향도 CI로 해결되지 않는다. `fit`·`validation`·`pilot`을 `test`와 섞거나 모델 선택에 재사용하지 않는다. 모델을 학습하거나 최적 정책을 선택하지 않는다.

## 같은 응답 수준 비교와 Pareto

`same_response.*`는 양 정책의 전경 p95와 배경 처리량이 모두 있는 **같은 완전 블록 집합**을 사용한다. 전경·배경 bootstrap에 같은 seed와 블록 순서를 적용하므로 같은 draw에서 같은 블록이 선택된다.

`same_response_descriptive=true`는 다음 기술적 조건을 함께 만족할 때만 표시한다.

1. 완전 대응 블록이 최소 2개다.
2. `mean(candidate FG p95 − baseline FG p95) <= --foreground-tolerance-ms`다.
3. 배경 처리량 평균 차이가 양수다.

이는 점 추정 조건이다. 별도 필드로 전경 차이 CI 상한의 tolerance 이내 여부, 배경 차이 CI 하한의 0 초과 여부도 기록한다. 두 marginal 95% CI를 함께 확인해도 공동 95% 보장, equivalence/noninferiority 검정, 다중성 보정, 정책 성공 판정이 되지 않는다. A만 있으면 비교 행 자체가 없다.

`pareto.svg`는 대비별 패널에 전경 p95(ms, 왼쪽 선호)와 배경 처리량(units/s, 위쪽 선호)을 그린다. 같은 대응 블록의 원시 점과 동일 가중 평균을 함께 표시한다. 원은 baseline, 마름모는 candidate이며 색만으로 구분하지 않는다. 두 평균이 서로 지배하지 않을 때만 점 사이를 점선으로 연결한다. 점선은 관측 두 점 사이의 기술적 표시이며 다른 운영점의 실현 가능성·효과 유의성·최적 정책을 주장하지 않는다. 비교마다 표본이 달라질 수 있으므로 패널의 N을 확인한다.

## 사전 p-value와 Holm

bootstrap CI로 p-value를 만들지 않는다. 외부 파일이 없으면 `p_values.*`는 빈 표다. Holm은 다음과 같이 **사전에 선언했고 완전하다고 명시한 family**의 유효한 원래 p-value가 모두 제공될 때만 적용한다.

```json
{
  "families": [
    {
      "family_id": "synthetic-format-example",
      "predeclared": true,
      "complete": true,
      "hypotheses": [
        {
          "hypothesis_id": "fg-hypothesis",
          "test": "externally specified paired-block test",
          "contrast": "predeclared cohort and foreground contrast",
          "p_value": 0.01
        },
        {
          "hypothesis_id": "bg-hypothesis",
          "test": "externally specified paired-block test",
          "contrast": "predeclared cohort and background contrast",
          "p_value": 0.04
        }
      ]
    }
  ]
}
```

위 값들은 형식 설명용 합성 숫자이며 실험 결과가 아니다. p-value는 유한한 `[0,1]` 숫자여야 하고 family ID 및 family 안 hypothesis ID는 중복될 수 없다. 가설 ID·검정명·대비 설명도 필수다. `predeclared` 또는 `complete`가 빠지거나 false면 파일 전체를 거부한다. 정렬된 p-value에 `(m−rank+1)`을 곱하고 누적 최댓값과 1 상한을 적용한다. family 크기를 결과에 따라 줄이거나 결측 가설을 제외하지 않는다.

분석기는 외부 p-value의 계산법·사전등록 날짜·family 완전성의 진실성을 인증할 수 없다. 외부 metadata를 보존하고 `supplied_external_predeclared`로 출처를 명시하며 자동으로 결과 대비와 연결하거나 유의성 별표를 그리지 않는다.

## 산출물

| 파일 | 내용 |
| --- | --- |
| `analysis.json` | 전체 분석, seed·방법·제한·경고·provenance |
| `SUMMARY.md` | 장치별 요약, tolerance 비교, 방법·제한 |
| `ledger.csv/json` | 원본 outcome, 해시, 파싱/검증 오류, 모든 제외·알려진 결측 |
| `blocks.csv/json` | 요청을 합치지 않은 블록 p50·p95·p99, wall, 처리량, 완료·재접근 |
| `per_hardware.csv/json` | split/장치/workload/실제 regime/정책/모델별 동일 가중 평균과 유효 N |
| `comparisons.csv/json` | 모든 대응 대비, 원 단위 차이·평균 비·`d_z`·95% CI·결측 N |
| `pairing.csv/json` | 정책쌍별 알려진 블록 합집합과 짝/제외 진단 |
| `same_response.csv/json` | 명시적 tolerance를 사용하는 공동 완전사례 FG/BG 비교 |
| `ablations.csv/json` | 동일 층·블록 E에 대한 F variant/model별 대비 |
| `overhead_blocks.csv/json` | outcome×role×phase 안에서 먼저 계산한 overhead 평균·유효 샘플 수 |
| `overhead.csv/json` | 역할·단계·상태·효과 포함 여부를 분리한 블록 동일 가중 평균 |
| `failures.csv/json` | 효과 분석에서 제외한 모든 ledger 행 |
| `p_values.csv/json` | 외부 사전 family 원 p-value와 Holm 보정값만 |
| `pareto.csv/json/svg` | 대비별 같은 완전 블록의 FG/BG 원시 좌표와 평균 |
| `effect_*.svg` | 원 단위 차이 forest plot. 지표별 별도 척도·단위·paired N·CI |
| `overhead_*.svg` | CPU·메모리·결정·로그 비용의 역할/단계별 기술통계, 0 기준선 |
| `failures.svg` | eligible/excluded 상태별 outcome 수. 독립 N과 구분 |
| `manifest.json` | 분석기·선택 입력 해시, 생성 파일 크기·SHA-256. manifest 자신 제외 |

overhead는 샘플 수가 많은 블록이 더 큰 가중치를 갖지 않는다. `decision_ms`/`log_write_ms`가 전부 `null`이면 결측으로 남긴다. 중복 결과의 overhead 원자료는 보존하되 중복 블록 평균을 효과 증거로 사용하지 않는다. overhead에서 `n_samples`는 관측량 진단이며 독립 N이 아니다.

CSV의 배열·객체 셀은 JSON으로 직렬화한다. `null` 스칼라는 빈 셀이며 수치 0과 구분한다. 빈 표도 header와 빈 JSON 배열이 있다. 모든 SVG는 title/description, 텍스트·축·단위·범례 의미를 갖는 독립 벡터 파일이다. 외부 URL·래스터·스크립트를 포함하지 않는다. 텍스트 폰트 모양은 보는 시스템의 폰트에 의존한다. 실제 학술지 규격이나 접근성 인증을 뜻하지 않는다.

## 합성 검증

```powershell
python -B -S scripts/test_research_analyze.py
```

fixture는 전체 v2 객체에 합성 라벨을 붙여 임시 디렉터리에서만 사용한다. 테스트는 요청 수가 다른 블록의 동일 가중치, 블록별 p99 경계, 짝 전체 재표집, 고정 seed와 손으로 확인 가능한 CI, 실패/중단/invalid/결측 보존, 잘못된 숫자와 schema, 장치·부하·regime·split 경계, 중복 배제, F variant/model 분리, 역할·단계별 overhead, calibration 제외, 명시적 사전 family의 Holm, tolerance 필수, 새 출력/덮어쓰기 금지, SVG XML·외부 자원 부재를 검사한다. 실제 workload나 C# fixture 실행은 포함하지 않는다.
