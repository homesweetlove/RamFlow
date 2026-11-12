# RAM Flow 실제 앱 A/B/C 스모크 결과

전체 스모크는 **미통과**다. 실앱 실행기·기록·안전 경로를 구현했고, 성공 1개·실패 2개·미실행 3개를 그대로 보존했다. B/C의 실측 API 수준 동등성과 V1의 성능 우위는 아직 입증되지 않았다.

## 실제 실행

| 앱 | 조건 | 결과 | 확인 내용 |
|---|---|---|---|
| Chrome | A: 엔진·트레이 없음 | 완료 | 전경 114건, 3단계 원시값/요약·정리·완료 마커·해시 통과 |
| Chrome | C: 기존 V1 | 실패 | 전경 136건 정확성 확인, 실제 조합 적용/readback 통과, Recovery 내부 복원 실패; 종료 원복 성공 |
| Chrome | B: 고정 V1 동일 조합 | 시작 중단 | Commit 약 89.9%, 안전 기준 85% 초과. 앱·배경·엔진을 생성하지 않음 |
| VS Code | C / A / B | 미실행 | 시험 창 컴퓨터 사용 앱 승인 시간초과; 이후 시스템 여유 메모리도 부족해 추가 실행 중단 |

계획 순서는 Chrome A→C→B, VS Code C→A→B였다. 슬롯당 한 번만 시도했고 실패 슬롯을 대체하지 않았다. 두 Chrome 실행의 측정 250건은 모두 작업 정확성 검사를 통과했다. C의 실패 자료는 유효 성능 비교에서 제외하며 요청 수를 독립 반복으로 취급하지 않는다.

실앱 측정 소스는 **699661fb1e181773b4807bd6d33bb1ce911bdf2c**, 세 실행 모두 source manifest의 Dirty=false다. 설치 Chrome은 **157.0.8103.39**이며 바이너리 SHA256은 `96d69d49ba2b17da5a836b691f7c16b64e4da0f3d367631b414ba280bed9b2c2`다. 11월 10일 표의 Chrome 버전/코드/측정 끝점과 동일하다고 주장하지 않는다. 이번 주 지표는 클릭 핸들러 진입→두 번째 rAF이며 CDP/실행기 왕복 시간도 별도 기록했다. 물리적 입력→화면 표시 지연은 아니다.

## 실패가 드러낸 조건

C에서 Normal의 Commit은 73.5–75.3%, Recovery는 77.5–79.2%였고 메모리 압박은 Moderate로 유지됐다. CPU 경쟁 스레드를 줄여도 RAM 압박은 해제되지 않았다. V1의 실제 정책 판단은 계속 `v1-eligible`이었다. 따라서 Recovery 안의 자동 복원이 확인되지 않았고 스모크를 실패 처리했다. 정책을 바꾸거나 Paused로 강제 복원한 뒤 성공으로 보고하지 않았다.

C의 소유 배경 PID에 적용한 OS readback은 CPU `0x4000`(BelowNormal), MemoryPriority `3`, EcoQoS PowerControl/PowerState `1/1`, CPU Sets `[]`였다. 요청값과 실제값은 일치했다. 종료 원복은 원래 CPU `0x20`, MemoryPriority `5`, PowerControl/State `0/0`, CPU Sets `[]`와 일치했다. 잘못된 전경 쓰기 시도/개입은 0건이다. 스코프 제한은 생성한 배경 PID+생성 FILETIME에만 쓰기를 허용했다. A/C 앱 Job 정리 후 잔류 PID는 없고 강제 Job 종료도 필요하지 않았다.

B는 조치 전에 중단돼 **B/C 동등 readback 검증은 미완료**다. v1 B 로그의 restored=false는 원래 설정을 읽을 배경 프로세스 자체가 없었던 상황이다. 복원 실패로 해석하지 않는다. 이 원본은 수정하지 않았고 차기 v2는 이런 경우 null과 `not-required-no-background-launched`를 기록한다.

추가 읽기 전용 관측에서 가용 RAM은 약 230MB까지 낮아졌다. 다른 앱을 종료하거나 시스템 설정을 바꾸지 않고 부하 실행·로컬 빌드를 중단했다. 압박의 발생 원인을 임의로 특정하지 않았다.

## 구현과 무결성

- 기존 소스는 접근 가능한 저장소/전체 Git ref의 파일 이력과 관련 ZIP 11개의 항목 목록에서 복구하지 못했다. 전체 디스크에 없다고 단정하지 않는다. 새 어댑터는 과거 결과와 분리했다.
- Chrome은 새 프로필·로컬 생성 대시보드·CDP, VS Code는 새 사용자/확장 프로필·개발 확장 호스트·생성 TypeScript 작업 공간을 사용한다. VS Code 확장 창 활성화까지의 시도는 기록했지만 완성/저장 기능 통과는 아직 주장하지 않는다.
- 앱은 suspended로 생성해 소유 Windows Job에 넣고 재개한다. 정리는 소유 Job 창/구성원에 한정한다. 원래 개인 앱/프로필을 프로세스 이름으로 찾아 종료하지 않는다.
- B는 기존 FixedResourcePolicy의 CPU/EcoQoS 두 개 조합을 그대로 쓰지 않고, V1 설정에 맞는 CPU/EcoQoS/메모리 조합 7을 동일 엔진 적용/복원 경로로 전달한다. 모든 쓰기 직후 OS readback+생성 시각을 기록하고 불일치면 실패한다.
- Normal→Contention→Recovery는 동일한 배경 프로세스의 활성 스레드 1→12→1이다. 고정 32MiB 데이터의 SHA256 완료 1회를 유효 작업 단위로 세고 경계 누적값 차이/작업자 시간 차이로 처리율을 계산한다. RAM 단독/혼합 압박이나 실제 배경 빌드의 완료시간 실험은 아니다.
- `fd17548` 저널/완료 마커/인덱스를 재사용하고 새 evidence profile에서 단계 경계, 전경 요청 원시값과 p50/p95·배열 요약, 배경 원시 누적값, API readback, 원복, 소스·바이너리 해시를 검사한다. 실패도 봉인하되 성능 성공으로 취급하지 않는다.
- 세 실행의 인덱스·해시·종료 상태 무결성은 모두 통과했다. 이는 C/B가 스모크 성공이라는 뜻이 아니다. 미실행 VS Code에는 가짜 완료 마커나 측정값을 만들지 않았다.

수정 파일은 `scripts/ResearchLab/AppStudy.cs`, `FixtureWorker.cs`, `EvidenceJournal.cs`, `LabInfrastructure.cs`, `Program.cs`, `scripts/real_apps/*`, `research_app_pilot.py`, `research_app_analyze.py`, `research_logcheck.py`, 두 앱 테스트 파일, `build-native.ps1`, 연구 프로토콜 문서다. 제품 Core 정책 코드는 이번에 변경하지 않았다.

## 실제 오버헤드 기록

다음은 **실패한 C 세션의 Contention 구간 진단값**이다. 유효 성능 비교나 관리 프로그램 일반 성능 수치로 사용하지 않는다. CPU는 16개 논리 프로세서 전체에 정규화한 관측 평균, 메모리는 해당 구간 최대값이다. 각 행 CPU 유효 표본은 103개이며 자원 읽기 실패는 0개다.

| 프로세스 역할 | CPU 관측 평균 | Private Bytes 최대 | Working Set 최대 |
|---|---:|---:|---:|
| engine-and-controller | 0.187% | 34.33 MiB | 71.75 MiB |
| app-driver | 0.045% | 22.91 MiB | 36.83 MiB |
| tray | 0.030% | 12.79 MiB | 52.77 MiB |
| observer | 0.054% | 17.88 MiB | 42.39 MiB |

엔진은 실행기와 같은 프로세스여서 엔진 단독 비용을 분리하지 못했다. 실제 트레이는 별도 격리 IPC로 실행·계측했다. 서비스는 미실행이며 A의 엔진/트레이도 미실행으로 표시했다. 없는 구성 요소를 CPU/RAM 0으로 채우지 않았다. 단계별 전체 자료는 overheads CSV에 있다.

C의 보호 전경 포함 정책 판단 74회 평균은 0.0150ms, 감사 sink 쓰기 평균은 0.3847ms, 전체 저널 직렬화/쓰기/flush 누적 wall time은 100.974ms다. source copy·인덱싱·다른 원시 스트림 쓰기 비용까지 포함한 총 로그 비용은 아니다. System Pages Input은 시스템 전체이며 프로세스별 Hard Fault는 null이다. 시작/대기용 Python 부모 실행기와 컴퓨터 사용 도구의 상주 비용은 독립 계측 대상에 포함되지 않았으므로 전체 측정계 비용 분해는 남았다.

## 검증과 남은 작업

로컬: 새 앱 계약/분석 테스트 13개, 기존 무결성 10개, 통계 분석 25개, 어댑터 5개, 매트릭스 28개 통과. Windows API·IPC는 실제 실행 권한에서 총 527 assertions 통과했다. 첫 샌드박스 실행의 자원 setter 실패는 별도이며 재검사로 구분했다. 기존 disposable 실행기와 강제 종료/취소/실패 봉인/원시 스트림/alias 무결성 통합 검사도 통과했다. v1 ResearchLab/트레이 빌드는 경고·오류 0개였다.

v1 실패를 반영한 다음 실행기 v2는 개입 전 5초/10개 이상 실제 센서로 Healthy 기준선을 고정하고 부적합하면 엔진/경쟁 부하 생성 전에 중단한다. v1 실패 슬롯은 닫고 새 계획 디렉터리를 요구한다. 전역 자원 부족으로 수정 v2의 로컬 빌드·실앱 재실행은 하지 않았고 Windows CI에서 빌드를 검증한다. 구현 09b9f8f의 [Native Windows CI](https://github.com/homesweetlove/RamFlow/actions/runs/42981909820)와 [기존 제품 CI](https://github.com/homesweetlove/RamFlow/actions/runs/42981909658)는 모두 성공했다. 수정 v2 ResearchLab 빌드와 Windows API/IPC 525 assertions, 계약 검사 12개 및 기존 disposable/실패 로그 통합 검사가 실제로 실행·통과했다. 로컬 최신 계약 검사는 미실행 슬롯 회귀를 더해 13개다. CI와 로컬의 assertion 2개 차이는 관리자/비관리자 검사 분기 차이다. CI는 Chrome·VS Code 6세션 성능 실험을 실행한 것이 아니다. VS Code 실제 completion/save·B/C 동등 조치·C Recovery 복원·6슬롯 전체 통과는 남았다.

다음 실행은 시험 환경의 실제 Healthy 기준선 확보 및 VS Code 시험 창 접근 이후 새 v2 계획에서 진행한다. [실행 명령/프로토콜](https://github.com/homesweetlove/RamFlow/blob/research/carp/docs/research/REAL_APP_ABC_SMOKE.ko.md)을 사용한다. 본실험 반복 수·CI·효과 크기·우수성·논문 게재 가능성을 이 스모크로 확정하지 않는다.

브랜치는 `research/carp`, [Draft PR #4](https://github.com/homesweetlove/RamFlow/pull/4)이다. 현재 구현 커밋 `09b9f8f`, 추가 분석 회귀 검사 `dfaa197`이며 실앱 측정 커밋 `699661f`와 구분한다. main과 `research/kci-baseline`, 기존 11월 9/10일 원시 기록은 변경하지 않았다.

배포 자료: `RamFlow-real-app-ABC-smoke-summary.json`, `RamFlow-real-app-ABC-metrics.csv`, `RamFlow-real-app-ABC-overheads.csv`, `RamFlow-real-app-ABC-smoke-evidence.zip`. 증거 ZIP에는 성공·실패·미실행 계획 및 연결 진단을 포함하며 런타임 프로필/작업 공간은 원래 work 경로에 보존하고 ZIP에는 넣지 않았다.
