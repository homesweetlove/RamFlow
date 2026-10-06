# 검증 안내

## 자동 검증

`python -m pytest -q`는 Mock을 사용해 Windows 앱을 변경하지 않습니다. 테스트 데이터는 저장소 내부 `.test-data`로 격리합니다.

- 8GB / 16GB 압박, 브라우저 다중 프로세스, AI 로딩, Commit 95%, Pagefile 부족
- 정상 상태 무개입, 전체화면 중 새 조치 금지, Deep Idle/쿨다운/백오프
- Foreground 보호 및 즉시 복원, 사용자 예외/작업 그룹/활성 작업 보호
- Memory/CPU/EcoQoS 원래 값 복원, PID 재사용, Dry Run 전환, 정상 종료
- 잘못된 설정의 원자적 거부, NaN/무한대/음수 인수 거부
- 관리자 IPC 명령 제한, 일반 권한의 실제 최적화 재개 차단, 마지막 메시지 청크 크기 제한
- 정책 비활성화 후 실패한 복원 재시도, PID 재사용 후 새 프로세스 원래 값 복원
- Pagefile 백업 보존/복원 Dry Run/PowerShell 삽입 방지
- 모델 저장소 읽기 전용 분석 및 HOT/WARM/COLD 추정

## Windows 검증

`scripts/verify_windows.py`는 문서화된 API의 실제 구조체/64비트 핸들, 자체 자식 프로세스의 Priority/EcoQoS 왕복, JSON Named Pipe 왕복을 확인합니다. 다른 사용자 앱은 변경하지 않습니다. 지원되지 않는 EcoQoS는 결과에 표시합니다.

`scripts/smoke_ui.py`는 Mock 상태로 8개 페이지를 렌더링합니다. `artifacts` 결과는 Git에 포함하지 않습니다.

`scripts/verify_packaged.py`는 빌드된 GUI/CLI 실행파일에서 실제 Qt 창 생성 및 정상 종료를 확인합니다. 빌드 스크립트에서 필수 실행하며 시스템 변경을 수행하지 않습니다. System32를 빌드 DLL 검색 경로의 앞에 두어 다른 도구의 ICU DLL 혼입을 방지합니다.

## 실사용에서 추가 확인할 항목

동일 작업을 대상으로 Dry Run, 실제 실행을 각각 여러 번 측정하세요. 8GB에서 Chrome + IDE + AI, 16GB에서 Docker + IDE + 브라우저, 게임 중 업데이트, 대용량 복사 등은 Mock 회귀가 있지만 모든 조합의 실제 하드웨어 성능을 검증한 것은 아닙니다.

현재 GUI는 일반 권한으로 동작합니다. 접근 실패는 로그에 기록하며 우선순위 조회에 실패하면 변경하지 않습니다. 관리자 Pagefile 적용/레지스트리 변경/실제 사용자 앱 종료는 자동 검증에서 실행하지 않습니다.

성능 개선 비율을 고정적으로 보장하지 않습니다. Available RAM 증가는 앱 응답성 개선과 같은 지표가 아닙니다.
