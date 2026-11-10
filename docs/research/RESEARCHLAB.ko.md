# 실제 Windows 계측 실행

후속 CPU 파일럿은 [실측 기록](PHASE3_CPU_PILOT.ko.md)을 따른다. 고부하 비교 설정은 `research_lab_adapter.py config --background-threads 12 --allow-high-cpu`로 명시적으로 선택할 수 있다. 모든 비교군에 같은 스레드 수를 전달하며 네이티브 장치별 상한과 필수 안전 검사는 그대로 적용된다. 현재 조치 후 경합 필터는 정책 효과로 인한 상태 전환을 제외할 수 있으므로, 이 분석을 확증 비교로 쓰기 전에 조치 전 적격성 관측을 구현해야 한다.

기존 스케줄을 유지하고 `scripts/ResearchLab`·`research_lab_adapter.py`를 연결했다. 새 경로는 일회용 합성 전경/배경 프로세스만 생성한다. Chrome/VS Code·실제 빌드 어댑터는 아직 구현하지 않았으며 아래 수치를 실제 앱 입력 지연으로 해석하지 않는다.

```powershell
dotnet build scripts/ResearchLab -c Release
$lab = (Resolve-Path scripts/ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe).Path
& $lab calibrate --output work/calibration-NEW --regimes healthy --actions 0,1,3,4 --fit-blocks 3 --validation-blocks 3 --seconds 5 --background-threads 1 --seed 20261110
& $lab train --input work/calibration-NEW --model work/frozen-model-NEW.json
& $lab validate --model work/frozen-model-NEW.json --output work/model-validation-NEW
python scripts/research_lab_adapter.py config --lab $lab --model work/frozen-model-NEW.json --output work/matrix-NEW.json --seconds 5 --blocks 6 --variant WithoutBackgroundCost
python scripts/research_matrix.py --config work/matrix-NEW.json --output work/matrix-run-NEW --workloads healthy --execute
python scripts/research_analyze.py --input work/matrix-run-NEW --output work/analysis-NEW --foreground-tolerance-ms 1.0 --bootstrap-iterations 10000
```

`1.0` 허용 범위는 명령 형식 예시이며 확정한 본실험 기준이 아니다. 모든 출력은 새 경로여야 한다. 모델 후보 채택 수 0은 성공적인 비용 모델이 아니며 그대로 거부 자료로 남긴다. 여러 하드웨어/작업은 `train --output work/models-NEW`로 분리한다. 학습용 경로와 최종 비교용 경로를 섞지 않는다.

## 후보·상태·안전

후보 값은 0=무개입, 1=CPU Below Normal, 3=CPU+EcoQoS, 4=메모리 우선순위, 5=CPU+메모리, 7=CPU+EcoQoS+메모리다. 실제 API는 생성한 배경 PID+생성 신원만 통과한다. 원래 값을 파일 저널에 먼저 쓰고 마지막 신원/전경 보호를 거쳐 적용하며, 종료 전에 원복 readback을 비교한다. Working Set 축소는 거부한다. 기존 사용자 RAM Flow가 감지되면 중단하며 사용자 앱이나 엔진을 종료하지 않는다.

전경 역할과 배경 역할의 apphost 이름을 분리해 같은 이름 보호를 우회하는 대신 서로 다른 생성 대상임을 명시한다. 전경 역할은 **실제 OS 포커스 창이 아닌 합성 작업 역할**이다. 개인 브라우저/IDE를 조작하지 않는다. 내부 Stopwatch 작업 시간과 부모 왕복 시간을 함께 기록하고, 5회 작업 워밍업은 보고 대상에서 제외한다. 엔진 첫 JIT와 로그 비용은 현재 짧은 파일럿에 포함될 수 있다.

최소 30개 측정 요청을 요구하고 제한 시간을 넘기면 실패를 남긴다. 기본 스레드는 적으며 전체 논리 CPU 수의 절반 이내다. `--allow-high-cpu --background-threads <논리 CPU 수, 최대32>`는 명시적인 높은 CPU 실험이다. 이번 전 코어 예비 시도는 실패했으므로 안정화 전 본실험에 쓰지 않는다. 실패 수 기본 2회에서 calibration을 중단하고 미시작 시행을 manifest/결과에 보존한다.

초기 가용 RAM 2 GiB 미만 또는 Commit 85% 초과에서는 실행을 거부한다. 실행 중 가용 512 MiB 미만 또는 Commit 90% 초과면 자체 작업을 중단한다. RAM을 고갈시키는 압박 생성기·pagefile 변경·캐시 전체 삭제는 없다. `--regime ram`은 RAM 중심 접근 패턴을 요청할 뿐 실제 RAM 압박을 보증하지 않는다. 실제 센서 분류가 다르면 `regime_attained=false`이며 효능 분석에서 제외한다. 물리 8/16/24+ GiB 장치와 적절한 격리 부하 준비가 추가로 필요하다.

## 실제 비교군과 소거

A는 호스트의 Engine 미생성, B는 CARP와 같은 관측·판단의 Dry Run, C는 고정 CPU/EcoQoS, D는 기존 V1, E는 동결 모델의 CARP다. F는 `--variant`로 여섯 프로필 중 하나를 정한다. 각각 별도 config/run/model hash를 보존한다. `WithoutMemoryAwareness`의 현재 조작 정의는 메모리 조치 억제이며 완전한 경합 분류기 제거 실험과 다르다. 이를 CPU/RAM 분류 자체의 이득 증명으로 부르지 않는다.

진행량은 자체 SHA/page-walk 완료 반복 수다. `--background-units`로 유한 작업을 정할 수 있고 관찰 중 완료되면 실제 완료시간을 기록한다. 완료하지 않았으면 null과 우측 검열 상태를 남기며 처리율로 완료시간을 추정하지 않는다. 기본 연속 부하는 완료시간이 해당 없음이다. 실제 빌드 완료시간·산출물 검증은 별도 어댑터의 미완료 과제다.

## 오버헤드·실패·분석

독립 helper는 지정한 PID+생성 신원과 자신의 CPU·Private Bytes·Working Set만 읽는다. 단계 경계의 CPU 구간이 섞이면 null이며 0으로 대체하지 않는다. 별도 타이밍 행은 정책 결정과 실제 JSONL/CSV 쓰기 호출 벽시계 시간이다. 호스트에는 엔진·오케스트레이션·컨트롤러 비용이 함께 있고, 트레이/서비스는 띄우지 않았으므로 미측정이다. idle은 엔진 생성 전 호스트 참조 구간이다. 배포 제품 전체 idle 비용으로 부르지 않는다.

원본 stdout/진행량/시스템 스냅샷/감사/독립 관측·source snapshot·바이너리 hash·원복 결과를 보존한다. 개발 중 수집한 파일럿은 dirty 상태와 당시 binary hash로 식별하며 최종 커밋 바이너리의 성능으로 재표기하지 않는다. 스케줄이 보존하는 native 원본과 전달 복사본은 `archive-alias.json`의 동일 바이트 hash로 연결해 독립 반복으로 중복 계산하지 않는다. 선언하지 않은 중복은 모두 제외한다.

분석기는 독립 세션 블록을 단위로 p50/p95·충분한 요청의 p99·대응 bootstrap CI·효과 크기·동일 응답 허용 범위·기술적 Pareto·소거/하드웨어/오버헤드/실패 표와 그래프를 만든다. p-value는 임의로 생성하지 않으며 외부 사전 고정 family의 값이 주어질 때만 Holm 보정한다. 이번 결과는 파일럿이고 본실험 사전 동결이나 KCI 투고 가능한 효과 검증이 끝난 상태가 아니다.
