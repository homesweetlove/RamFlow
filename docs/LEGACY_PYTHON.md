# RamFlow

Windows에서 현재 사용 중인 앱과 관련 작업을 보호하는 리소스 오케스트레이터입니다. RAM 사용률만 보고 정리하지 않습니다. 실제 압박이 생겼을 때 오래 비활성인 앱의 Memory Priority, CPU Priority, EcoQoS를 단계적으로 조정합니다.

ZIP의 WinMemoryFlow MVP를 바탕으로 확장한 **0.2.0**입니다. 기존 Python/PySide6 코드를 유지해 바로 실행할 수 있게 했습니다. C#/.NET 10 또는 WinUI 3 이식은 이번 버전에 포함되지 않습니다.

## 실행

Windows 10/11 x64에서 사용합니다. 기본값은 **Dry Run**이며 시스템 설정을 바꾸지 않고 예상 조치를 표시합니다.

포터블 빌드는 `RamFlow-0.2.0-windows-x64.zip`을 풀고 **RamFlow/RamFlow.exe**를 실행합니다. `_internal` 폴더를 함께 유지하세요. CLI는 `RamFlow-cli/RamFlow-cli.exe`입니다. 설치나 Python 설치가 필요 없습니다.

소스 실행은 Python 3.10 이상이 필요합니다. CI는 3.12, 로컬 검증은 3.14에서 수행합니다.

```powershell
python -m venv .venv
.venv\Scripts\python.exe -m pip install -e ".[dev]"
.venv\Scripts\python.exe -m ramflow ui
```

설치 후 `run.bat`로도 실행할 수 있습니다. 창을 닫으면 트레이에서 계속 동작합니다. **트레이 → 종료**로 끝내면 앱이 변경한 우선순위를 원래 값으로 복원합니다. 실제 실행은 GUI에서 Dry Run을 해제하세요. 일반 사용자 권한으로 시작하며 접근할 수 없는 프로세스는 건너뜁니다.

## 구현된 기능

| 기능 | 동작 |
|---|---|
| 메모리 모니터 | RAM, Commit, 캐시, Standby, 압축 메모리 근사, 페이지 입력/출력 |
| Memory Pressure | 비대칭 EMA, 상승 확인, 하강 히스테리시스, Commit/가용 메모리 위기 감지 |
| 통합 압박 | 메모리·CPU·디스크·배터리 관측값을 합산. 미지원 센서는 제외 |
| 활동 분류 | 최근 활동, Background, 30분 이상 Idle, 2시간 이상 Deep Idle |
| 우선순위 관리 | Idle 앱 Memory Priority/Below Normal CPU/EcoQoS, 정상화 시 원래 값 복원 |
| Foreground First | 동일 이름의 현재 앱을 보호하고 샘플 사이에도 약 250ms 간격으로 복원 확인 |
| 작업 모드 | 자동/개발/게임/AI/브라우저/문서/미디어/배터리/백그라운드 수동 선택 |
| 작업 그룹 | 개발 도구·브라우저·컴파일러, 게임 관련 음성/오디오, AI 프로세스 그룹 보호 |
| AI / LLM | 런타임/명령행 기반 감지, 사용자 등록, 모델 메모리·Commit 실행 가능성 추정 |
| Pagefile | 관측 Commit 수요와 디스크 종류 기반 권장, 명시적 적용, 최초 설정 백업/복원 |
| 모델 저장소 | 지정 폴더의 GGUF/Safetensors/ONNX 등 읽기 전용 분석, HOT/WARM/COLD 추정 |
| 사용 패턴 | 선택적 시간대 EMA 학습, Commit peak/p95 관측 |
| 벤치마크 | Before/After, CPU·메모리·디스크, 실행/스케줄링 지터, JSON/CSV 내보내기 |
| UI / 트레이 | 한국어 GUI 8개 화면, 빠른 작업 모드, Dry Run, 최적화 중지 |
| IPC | 사용자 SID별 Named Pipe, 로컬 접속, 명령/크기/숫자 검증, 관리자 명령 권한 확인 |

## 개입 정책

| 압박 | 정책 |
|---|---|
| Normal | 시스템 변경 없음. 이전에 낮춘 값만 복원 |
| Moderate | 30분 이상 Idle 앱의 Memory/CPU Priority와 EcoQoS 조정 |
| High | 2시간 이상 Deep Idle이고 150MB 이상인 앱만 Working Set 축소 후보 |
| Severe | 위 정책 + Pagefile/Commit 권장 |
| Critical | 위 정책 + 메모리 사용 상위 비보호 앱 정리 후보 표시. 자동 종료 없음 |

Working Set 축소는 틱당 최대 2개, 전역 2분 간격, 동일 프로세스 15분 이상 쿨다운이며 효과가 낮으면 백오프합니다. 전체화면에서는 새로운 변경을 중지합니다. 블랙리스트도 보호 정책과 Deep Idle 조건을 우회할 수 없습니다. CPU 압박이 80% 이상일 때는 메모리 압박이 낮아도 Idle 앱 CPU/EcoQoS 정책만 적용할 수 있습니다.

시스템/보안/드라이버/서비스, 현재 앱, AI, 컴파일·서버·미디어·다운로드 계열, 사용자 예외 목록, 활동 정보를 측정할 수 없는 앱은 보호합니다. 실행 직전에 포그라운드·세션·프로세스 생성 시각을 재확인합니다. 정상 종료, 일시 중지, Dry Run 전환, 작업 모드 변경 시 **처음 관측한 값**으로 복원합니다. 강제 종료/정전 때 복원은 보장되지 않으며 해당 프로세스 종료나 재부팅으로 프로세스 설정이 초기화됩니다.

## Pagefile과 제거

자동 Pagefile 변경과 비공식 Standby 정리 기능은 제거했습니다. Pagefile 적용은 GUI의 명시적인 확인 및 관리자 권한이 필요합니다. 첫 변경 전 백업을 보존하고 백업 실패 시 변경을 거부합니다. 변경 시 기존 Pagefile 배치를 새 권장 배치로 교체하며 재부팅이 필요할 수 있습니다. 원복은 **Pagefile → 이전 설정 복원**에서 진행합니다.

이 버전은 포터블이며 Windows 서비스/스케줄러 작업을 자동 설치하지 않습니다. 제거하려면 Pagefile을 바꾼 경우 먼저 복원하고, 설정에서 자동 시작을 해제한 뒤 트레이에서 종료하고 포터블 폴더를 삭제합니다. 데이터는 `%LOCALAPPDATA%\RamFlow`에 저장됩니다. 필요할 때 이 폴더도 삭제하세요. `RAMFLOW_HOME`으로 저장 위치를 지정할 수 있습니다.

별도 엔진은 `python -m ramflow service`로 실행합니다. 이는 SCM에 등록된 Windows 서비스가 아닌 상주 프로세스입니다. 같은 사용자 UI가 Named Pipe로 연결합니다. 서비스의 실제 변경 활성화/프로세스 종료/Pagefile 명령은 관리자 토큰을 가진 IPC 클라이언트만 호출할 수 있습니다. 일반 UI는 모니터링과 안전한 설정 변경을 사용합니다.

## CLI / 빌드 / 검증

```powershell
python -m ramflow monitor --count 10
python -m ramflow simulate avail_under_500mb --ticks 150
python -m ramflow llm-check 6.8 --extra-gb 1
python -m ramflow pagefile --mode llm --model-gb 6.8
python -m ramflow storage "D:\Models"
python -m ramflow benchmark --seconds 5
python -m pytest -q
python scripts/smoke_ui.py artifacts/dashboard.png
python scripts/verify_windows.py
./scripts/build.ps1
```

`simulate`는 Mock 데이터만 사용합니다. `verify_windows.py`는 실측과 IPC를 확인하고 **스스로 만든 테스트 자식 프로세스만** 일시적으로 변경한 뒤 복원합니다. GUI 스모크는 Mock을 사용하므로 실제 프로세스/사용자 파일을 노출하지 않습니다. GitHub Actions는 Windows에서 테스트·GUI 스모크·포터블 빌드를 수행합니다.

벤치마크의 입력 지표는 Pages Input/sec이며 hard fault 사건 개수와 동일하지 않습니다. 압축/총 Pagefile 값은 근사입니다. 앱 실행 시간 기본값은 Python 시작 시간이고 패키징 버전에서는 미측정입니다. 응답 시간은 sleep 스케줄링 지터입니다. Dry Run 측정 차이를 성능 개선으로 해석하지 마세요. 게임 프레임 시간·LLM tokens/sec·IDE 빌드 시간은 아직 측정하지 않습니다.

## 남은 범위

클라우드 모델 이동/자동 복원, GPU VRAM 및 모델별 KV cache 분석, CPU topology/affinity, I/O Priority 변경, 온도 센서, 설치 프로그램/자동 업데이트는 미구현입니다. 모델 COLD 판정은 접근/수정 시각 기반 추정이며 실제 사용 빈도와 다를 수 있습니다. 수동 배터리 모드는 상태/모드 선택을 제공하며 Windows 전원 계획을 변경하지 않습니다. 이름 기반 작업 감지는 휴리스틱으로, 알 수 없는 무입력 작업은 화이트리스트에 등록하세요.

공식 API 근거와 모듈 구성은 [설계 문서](ARCHITECTURE.md), 검증 범위는 [검증 안내](VALIDATION.md)를 참고하세요.
