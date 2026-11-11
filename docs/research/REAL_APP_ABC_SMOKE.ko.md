# 실제 앱 A/B/C 연결 검증

현재 실행 버전은 `real-app-abc-v3`다. v2의 32 MiB SHA256/12스레드 부하에서는 실제 CPU가 V1의 80% 임계값에 닿지 않아 두 C가 무개입으로 끝났다. 이 자료를 변경하거나 성공으로 재분류하지 않는다. v3는 CPU 계산 경로를 분리한 `xorshift64-1000000-v1` 작업 단위와 14스레드(16논리CPU 장비에서 2개 여유)를 사용한다. 각 단위/단계 경계의 체크섬을 독립 golden vector로 검사한다. 처리율 단위가 달라 v1/v2의 SHA 처리율과 합치지 않는다. 새 실행 전 `python scripts/research_cpu_preflight.py --output work/NEW-cpu-check --threads 14`로 실제 CPU가 임계값에 도달하는지 확인한다. 부하 스레드 수 자체를 실제 압박으로 간주하지 않는다. 별도 원시 결과와 사전 기준선을 유지하며 본실험 효과를 주장하지 않는다.

2026-11-11~12 실앱 실행에서 v3도 앱 워밍업 이후 80%를 지속하지 못했다. 초기 부하 점검의 3개 상승 표본만으로 통과시키던 규칙은 불충분했다. 이후 사전 검사만 `cpu-preflight-v2-sustained`로 보강했다: 정상 워밍업 5초, 부하 점검 6초, 유효 표본 6개 이상·80% 이상 표본에서 CPU≥80%·마지막 3개도 CPU≥80%를 요구한다. 새 계획은 이 결과와 동일 관리 바이너리 해시·스레드 수·작업 단위가 맞는 `--preflight` 증거가 없으면 생성/실행하지 않는다. 기존 자료는 초기 규칙/소스 그대로 보존하고 스모크 성공으로 재분류하지 않았다. 지속 압박이 확인되기 전 추가 정책 비교를 진행하지 않는다.

보존한 v2 프로토콜 `real-app-abc-v2`는 기존 CARP A–F 조건명과 다르다. A는 엔진·트레이 미실행, B는 고정 V1 조합(CPU BelowNormal, EcoQoS execution-speed 비트, 메모리 우선순위 3), C는 기존 V1 적응 제어다. CPU Sets·Working Set 강제 축소는 끈다. 동일한 엔진 적용/저널/복원 경로를 사용하고 매 API 쓰기 직후 PID 생성 시각과 OS readback을 검증한다. B는 Recovery에도 고정 제한을 유지하고 세션 종료 시 복원한다. C는 Recovery 안에서 복원을 확인해야 통과한다.

최초 v1 스모크에서는 Normal/Recovery라는 부하 명칭과 실제 압박 해제가 일치하지 않았다. C에서 기존 메모리 압박이 Moderate로 유지됐고 종료 때만 정상 복원됐다. 이를 성공으로 바꾸지 않았다. v2는 실제 앱 준비 후 엔진 생성 및 경쟁 부하 시작 전에 5초/10개 이상 센서 스냅샷으로 `PreInterventionWindow`의 Healthy·안정성·도달 여부를 고정한다. 부적합하면 개입 전에 실패로 봉인한다. 기존 v1 계획은 닫고 새 v2 디렉터리를 요구한다. 파일럿 결과·바이너리·소스 버전은 합치지 않는다.

Chrome은 새 프로필의 20,000개 생성 레코드 검색·정렬을 CDP 입력으로 실행한다. 지연의 주 지표는 DOM 클릭 핸들러 진입부터 두 번째 rAF 콜백까지이며 CDP 왕복 시간도 별도 기록한다. VS Code는 새 사용자/확장 프로필과 개발 확장 호스트에서 5,000개 생성 TypeScript 레코드를 편집해 completion provider API 및 save API를 측정한다. 저장 후 실제 파일 내용을 검증한다. 두 지표 모두 물리적 입력→화면 표시 지연은 아니다. 예전 실행 소스를 복구한 것이 아니므로 2026-11-10 결과와 직접 합치지 않는다.

Normal → Contention → Recovery는 동일한 소유 배경 프로세스의 활성 계산 스레드를 1 → 12 → 1로 바꾸는 CPU 중심 스모크다. 각 작업 단위는 고정 32 MiB 데이터의 SHA256 계산 완료 한 번이다. 정상/회복 구간의 작업자 수가 다르므로 세 구간의 처리율을 그대로 정책 효과로 비교하지 않는다. 실제 CPU/RAM 센서를 보존하고 의도한 부하를 실측 압박으로 간주하지 않는다. RAM 단독·혼합 실험이나 재활성화 지연 실험은 이 스모크의 범위가 아니다.

`fd17548` 저널·완료 마커·SHA256 인덱스를 그대로 사용하며 별도 evidence profile로 원시 요청/단계 경계/처리량/백분위수/설정 원복을 검사한다. 프로필과 작업 디렉터리는 임시 생성한 별도 런타임 경로에 보존하고, 봉인된 증거에는 소스·버전·바이너리 해시·원시 측정·실패와 정리 결과를 보관한다. 앱은 suspended 상태로 생성해 소유 Windows Job에 넣은 후 재개한다. 정리는 해당 Job의 창과 구성원에 한정한다. 개인 앱을 프로세스 이름으로 찾아 종료하지 않는다.

Windows 전경 전환이 거부되면 시작 단계에서 실제 시험 창을 활성화해야 한다. 준비 후에는 매 요청 전후 실제 OS 전경 PID+정확한 생성 FILETIME을 확인한다. 소유 창이 아니면 실패한다. 포커스 제한을 바꾸거나 전경 PID를 합성하지 않는다. 컴퓨터 사용 도구를 통한 시작 활성화는 측정 구간 밖에서만 한다. [Microsoft 전경 전환 제한](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow), [소유 Job과 자식 프로세스](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).

계측기는 엔진+실행기 호스트, 트레이, 앱 드라이버, 배경 작업, 자기 자신을 각각 PID+생성시각으로 읽는다. 엔진은 호스트와 같은 프로세스이므로 엔진 단독 CPU로 해석하지 않는다. 서비스는 미실행, A의 엔진/트레이는 미실행으로 표시하며 0으로 대체하지 않는다. 센서 원시 행의 동작 상태 태그와 저널의 UTC 단계 경계를 결합해 단계별 비용을 집계한다. 결정 시간·감사 쓰기 시간·전체 저널 기록 시간은 CPU 사용량과 구분한다. System Pages Input은 프로세스별 Hard Fault가 아니다.

## 실행

설치된 Python의 `psutil`, `websocket-client`, Chrome, VS Code와 .NET 10이 필요하다. 앱/런타임/패키지 버전은 실행 시 기록한다. 개인 프로필은 사용하지 않는다.

```powershell
dotnet build scripts/ResearchLab -c Release
dotnet build native/RamFlow.Tray -c Release
python scripts/test_research_apps.py
python scripts/test_research_logcheck.py
python scripts/test_real_app_adapters_windows.py --run
python scripts/research_cpu_preflight.py --output work/NEW-cpu-check --threads 14
if ($LASTEXITCODE -ne 0) { throw '지속 CPU 압박 미확인: 추가 정책 비교 중단' }
python scripts/research_app_pilot.py --plan --output work/NEW-pilot --preflight work/NEW-cpu-check --contention-threads 14
# pilot-plan.json 순서대로 각 슬롯을 딱 한 번씩 실행:
python scripts/research_app_pilot.py --output work/NEW-pilot --app chrome --condition A
# C, B 및 code의 예정 순서도 동일하게 실행한다. 실패 슬롯을 덮어쓰지 않는다.
python scripts/research_logcheck.py --input work/NEW-pilot --recursive
python scripts/research_app_analyze.py --input work/NEW-pilot --output work/NEW-analysis
```

빌드·테스트·분석을 측정과 동시에 실행하지 않는다. 한 앱/조건당 한 세션, 총 6개 스모크만 계획한다. 실패/미실행도 보고한다. 요청 수를 독립 반복 수로 세지 않으며 CI·유의성·우수성·본실험 N을 이 자료로 확정하지 않는다. 모든 연결 검사가 통과한 뒤 별도 파일럿과 본실험 계획이 필요하다.
