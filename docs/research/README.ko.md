# RAM Flow KCI 연구 작업 안내

최신 실앱 연결 검증(2026-11-12): [QoS 진단·v4 A/B/C 6세션 보고서](evidence/2026-11-12-qos-v4/REPORT.ko.md) · [원시 증거 ZIP](evidence/2026-11-12-qos-v4/raw-evidence.zip) · [실행 명령](REAL_APP_ABC_SMOKE.ko.md). 새 격리 Chrome/VS Code 어댑터에서 실제 개입·회복·B/C OS readback 일치·두 단계 원복·무결성 검사를 통과했다. 모든 조건의 배경 워크로드가 정상 QoS를 명시하는 별도 입력층이며, 기본 QoS의 [v2/v3 실패 기록](evidence/2026-11-12-real-app-v2-v3/REPORT.ko.md)과 합치지 않는다. C의 반복 적용/복원, 효과 없는 Chrome 조건, VS Code 지연/배경 처리율 상충도 보존했다. n=1 연결 검증으로 CARP 우위·같은 응답성에서의 처리율 우위를 입증하지 않는다.

검사 전 기록 점검: [로그 계약·누락 보완·무결성 검증](LOGGING_CONTRACT.ko.md). 새 실행은 evidence-v1 원시 기록과 정리 후 완료/해시 목록을 생성하며, 미완료·변경된 새 기록은 학습/분석에서 거부한다. 실제 앱 실행기 연결과 재추출 검증은 위 v4 증거를 참조한다. 아래 최초 구현 경계/미완료 목록은 후속 실측 문서와 함께 읽는다.

추가 개발: [실측 비용 모델](MEASURED_MODEL.ko.md) · [실행 가능한 Windows 경로](RESEARCHLAB.ko.md) · [starvation/소거](STARVATION_ABLATIONS.ko.md) · [추가 실측·실패·검증](PHASE2_RESULTS.ko.md). 아래 기존 단계 기록과 새 파일럿 결과를 구분한다.

목표는 TKIPS를 우선 대상으로 한 KCI 등재 학술지의 정규 연구논문이다. 현재 산출물은 비용 정책의 구현·안전 검증·실험 골격이며 **CARP의 성능 우위나 논문 채택을 입증한 결과는 없다**.

## 읽을 순서

1. [기준선·버전·자료 감사](BASELINE_AUDIT.ko.md), [고정한 파일 해시](baseline-manifest.json)
2. [CARP 수식·의사코드·아키텍처·제약](CARP.ko.md)
3. [선행연구 비교와 검증 범위](RELATED_WORK.ko.md)
4. [실험·소거·통계 프로토콜](PROTOCOL.ko.md)
5. [학술지 공식 규정·AI 공개·비용·미확인 항목](JOURNALS.ko.md)
6. [논문 개요와 빈 표·그림 양식](PAPER_OUTLINE.ko.md)
7. [구현 계획](IMPLEMENTATION_PLAN.ko.md), [검증 결과](VALIDATION.ko.md)

## 실제로 구현한 경계

- `Settings.PolicyName`: 기본 `v1`, 비교용 `fixed`, 별도 `carp`.
- `ResourcePolicy.cs`: 동일 스냅샷 입력과 V1/고정 정책 판단 계약. `CarpPolicy.cs`: 설명 가능한 비용·단계·진입 안정화·쿨다운.
- `Engine.cs`: 판단과 적용 경계, 보호 재검사, 원래 값 저널·복구, 관측·결정·조치 감사 이벤트.
- `JsonPolicyObservationSource`: 연구 호스트가 명시적으로 지정하는 파일 입력. 앱 지연을 직접 계측하는 모델은 포함하지 않는다.
- `FileResearchAuditSink`: 실행 manifest, JSONL 원본, CSV 인덱스. 원래 엔진 저널과 함께 보존한다.
- [비교 실행 골격](../../scripts/research/README.md): A–F × CPU/RAM/혼합/건강 상태의 seed 고정 Williams 교차 순서, 새 디렉터리 전용 기록, 실패·중단·결측 보존. 실제 앱 어댑터는 아직 없다.
- [ResearchReplay](../../scripts/ResearchReplay/README.md): 같은 `PolicyInput` JSONL의 V1/fixed/CARP 순수 판단 비교. Windows 조치를 하지 않는다.

일반 서비스에는 CARP 관측 소스가 주입되지 않는다. 설정에서 CARP를 선택하는 것만으로 앱 지연 측정이나 검증된 비용 모델이 생기지 않으며, 이 경우 자원 후보는 무개입한다. API 연구 통합은 명시적 별도 `dataRoot`, `allowRemote:false`를 사용하고 disposable 대상만 허용하는 provider 경계를 추가해야 한다. 기존 사용자 설정에 정책을 자동으로 덮어쓰지 않았다.

## 재현성 체크리스트와 남은 단계

- [x] 기준선 branch/commit, 공개 태그·release digest와 로컬 파일 hash 차이 고정.
- [x] 기존 연구 문서·CSV/JSON·실패 해석 보존, 안전·단위·Windows setter/복구·IPC 검사.
- [x] 정책/버전·설정/바이너리 hash·입력/후보/사유·적용/복구 기록 수단.
- [x] A–F 스케줄·실행 기록 골격과 결측을 결과로 꾸미지 않는 처리.
- [ ] 11월 당시 작업별 원본·고정 소스·바이너리 확보 및 공개 가능 범위 확인.
- [ ] Chrome/VS Code/실제 빌드/AI 또는 다중 작업의 격리 계측·정확성·처리량 어댑터.
- [ ] 후보별 예측값의 정규화·독립 파일럿 검증·모델 hash/버전·불확실성 보정.
- [ ] 큐 시작 보류의 대기/완료 비용을 비용식에 통합. 현재는 보수적 안전 가드.
- [ ] 독립 관찰기로 관측/로그/제어 오버헤드 실측. tick 소요 시간은 대체 근거가 아님.
- [ ] 파일럿 뒤 가중치·반복 수·일치 허용 범위·중단 기준·분석·다중 비교 계획 사전 고정.
- [ ] 실물 8/16/24+ GiB PC 반복·소거, 대응 블록 CI·효과 크기·Holm·Pareto 평가.
- [ ] Windows CI 결과, 원시 데이터/실패/실행 manifest 공개·다른 환경 독립 재현.
- [ ] 현재 TKIPS 규정의 미확인 사항·저자 실제 기여·AI 사용 이력·최종 원고 인간 검증.

재생·모의 검사·setter 기능 검사는 성능 파일럿이나 독립 실험 반복을 대신하지 않는다. 위 미완료 항목은 투고 수준의 실증 기여를 위해 해결해야 한다.

후속 실행: [CPU 압박 실제 파일럿](PHASE3_CPU_PILOT.ko.md). 27보정·18비교·전체 원복을 기록했으며, 거부 모델과 사후 경합 필터의 한계·동시 회귀 검사 이탈을 공개한다. 확증 실험이나 CARP 우위 결과가 아니다.

현재 수정과 새 자료: [조치 전 프로토콜](PRE_INTERVENTION_PROTOCOL.ko.md) · [수정 후 재실험](PHASE4_PRE_INTERVENTION_RESULTS.ko.md). 조치 전 입력을 고정한 새27보정/18비교에서 모델2개를 채택했고 비교17개가 유효했다. CARP는 비용상 무개입이었다. 관측기 준비 실패1개는 원복·보존했으며 후속 수정의 별도 기능 시행으로 대체하지 않는다.
