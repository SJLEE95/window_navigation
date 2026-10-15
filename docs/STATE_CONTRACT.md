# 작업 상태 파일 계약 (taskmonitor/1)

여러 프로젝트(3DMaker, MpdMaker 등)의 Python 작업이 **공통 JSON 파일 하나**로 자기 상태를 기록하고,
TaskMonitor는 그 파일을 **읽기만** 해서 화면에 표시합니다. 모니터는 작업을 실행·재실행·종료하지 않습니다.

## 1. 위치

```
%LOCALAPPDATA%\TaskMonitor\
  tasks\<task_id>.json        작업 상태 (작업 프로세스가 씀, 모니터는 읽기만)
  tasks\<task_id>.json.lock   같은 작업을 여러 프로세스가 갱신할 때 쓰는 잠금 파일
  control\<task_id>.cancel    중단 요청 (모니터가 씀, 작업이 읽음)
  archive\                    "목록에서 숨기기"로 옮긴 기록
  settings.json               모니터 화면 설정
```

환경 변수 `TASKMONITOR_DIR`을 지정하면 그 폴더를 루트로 씁니다(데모·테스트용). 모니터와 작업이 같은 값을 써야 합니다.

## 2. 저장 방식 (파일이 깨지지 않게)

1. **작업 하나 = 파일 하나.** 서로 다른 작업은 서로 다른 파일을 쓰므로 충돌하지 않습니다.
2. **원자적 교체.** 같은 폴더에 `<id>.json.<pid>.<난수>.tmp`로 전부 쓴 뒤 `flush + fsync` → `os.replace()`로 교체합니다.
   읽는 쪽은 항상 "이전 완성본" 또는 "새 완성본" 중 하나만 봅니다. 반쯤 쓰인 파일은 보이지 않습니다.
3. **같은 작업을 여러 프로세스가 갱신**(예: 래퍼 + 본 스크립트)할 때는 `<id>.json.lock`에 OS 파일 잠금
   (Windows `msvcrt.locking`, POSIX `fcntl.flock`)을 잡고 *읽기 → 수정 → 원자적 교체*를 합니다.
4. Windows에서 모니터가 파일을 읽는 순간 `os.replace`가 `PermissionError`를 낼 수 있어 짧게 재시도합니다.
   모니터는 `FileShare.ReadWrite | FileShare.Delete`로 열어 교체를 막지 않으며, 파싱 실패 시 직전 정상본을 유지합니다.
5. `seq`는 쓰기마다 1씩 증가합니다(디버깅·변경 감지용).

## 3. 필드

| 필드 | 필수 | 설명 |
|---|---|---|
| `schema` | ✔ | 항상 `"taskmonitor/1"` |
| `id` | ✔ | 고유 작업 ID. 파일 이름과 같음. `[A-Za-z0-9._-]` |
| `name` | ✔ | 화면에 보일 작업 이름 |
| `project` | | 프로젝트 이름 (예: `3DMaker`) |
| `workdir` | | 작업 디렉터리 |
| `subject` | | 지금 처리 중인 입력·샘플 (예: `sample2`) |
| `state` | ✔ | 아래 상태 값 |
| `message` | | 짧은 현재 상황 한 줄 |
| `pid` | | 작업 프로세스 PID (생존 확인용) |
| `created_at` / `started_at` / `updated_at` / `finished_at` | `updated_at` ✔ | ISO 8601, 시간대 포함 (`2026-10-15T11:20:05+09:00`) |
| `stale_after_s` | | 이 시간(초) 이상 갱신이 없으면 "상태 갱신 없음" 표시. 기본 120 |
| `current_stage` | | 현재 단계의 `key` |
| `stages[]` | | 단계 목록 (순서대로) |
| `depends_on[]` | | 선행 작업 ID 목록 |
| `waiting_for` | | `{ "tasks": [...], "inputs": [...], "reason": "..." }` 대기 중일 때 |
| `log_path` | | 전체 로그 파일 경로 |
| `results[]` | | `{ "label": "...", "path": "...", "kind": "file"|"dir" }` |
| `error` | | `{ "type", "message", "detail", "stage" }` 실패 시 |
| `cancel` | | `{ "supported": true, "method": "file" }` 중단 지원 여부. 없으면 "중단 미지원" |
| `completion` | | `{ "basis": "reported"|"exit_code", "exit_code": 0 }` 완료로 판정한 근거 |
| `seq` | | 쓰기 횟수 |

### 단계(`stages[]`) 항목

| 필드 | 설명 |
|---|---|
| `key`, `name` | 단계 식별자, 화면 이름 |
| `state` | `pending` / `running` / `validating` / `succeeded` / `failed` / `skipped` / `cancelled` |
| `done`, `total`, `unit` | 처리량 (예: 462 / 599 `frames`). `total`을 모르면 생략 |
| `rate`, `rate_unit` | 작업이 보고한 처리 속도 (예: 0.78 `frames/s`) |
| `eta_seconds` | 작업이 보고한 이 단계 남은 시간(선택) |
| `expected_seconds` | 이 단계가 보통 걸리는 시간(선택). **전체 ETA는 남은 모든 단계에 이 값이 있을 때만 계산** |
| `weight` | 전체 진행률 가중치(선택). **모든 단계에 weight가 있을 때만 전체 %를 표시** |
| `started_at`, `finished_at`, `progress_at` | 시작·끝·마지막 처리량 변화 시각 |

### 작업 상태 값(`state`)

| 값 | 화면 | 의미 |
|---|---|---|
| `pending` | 대기 | 등록됐지만 아직 시작 전 |
| `waiting` | 선행 작업·입력 대기 | `waiting_for`에 무엇을 기다리는지 기록 |
| `running` | 실행 중 | |
| `validating` | 검증 중 | 생성은 끝났고 검증·보고서 저장 중 |
| `succeeded` | 완료 | **작업이 명시적으로 기록한 경우에만** |
| `failed` | 실패 | `error` 포함 |
| `cancelled` | 중단 | 중단 요청을 받아 작업이 스스로 멈춤 |

## 4. 모니터가 추가로 판단하는 것 (파일 내용을 그대로 믿지 않음)

| 상황 | 화면 표시 |
|---|---|
| `running`/`validating`인데 `updated_at`이 `stale_after_s`보다 오래됨, 프로세스는 살아 있음 | `실행 중 · 상태 갱신 없음` + 마지막 갱신 시각 (실패로 바꾸지 않음) |
| 비종료 상태인데 `pid` 프로세스가 없음 (PID 재사용은 프로세스 시작 시각으로 구분) | `종료 기록 없음` — 정상 완료로 추정하지 않음 |
| `pid`가 없고 갱신도 오래됨 | `상태 갱신 없음` (생존 여부 알 수 없음) |
| `succeeded`인데 단계 중 `succeeded`/`skipped`가 아닌 것이 있음 | `완료 기록 불일치` |
| `succeeded`인데 결과 경로가 없거나 `.partial`/`.tmp`로 끝남 | `완료 · 결과 확인 필요` |
| 생성 단계만 끝나고 검증 단계가 남음 | 전체는 `실행 중`/`검증 중` 유지 |
| 중단 요청을 보냈는데 작업이 아직 반영 안 함 | `중단 요청됨 (응답 대기)` |

## 5. ETA 규칙

- **현재 단계 ETA**: `done/total`이 있고 진행이 관측된 경우만. 단계 평균 속도(`done / 단계 경과시간`)와
  모니터가 관측한 최근 속도(EWMA)를 섞고, 표시하는 예상 시각도 완만하게 따라가게 해 흔들림을 줄입니다.
  작업이 `eta_seconds`를 보고하면 그것도 같은 방식으로 평활화합니다.
- **전체 ETA**: 현재 단계 ETA + 이후 단계들의 `expected_seconds` 합. 하나라도 모르면 **"정보 부족"**.
- 진행이 막 시작돼 근거가 부족하면 **"계산 중"**.
- 모든 ETA 앞에 `≈`와 "예상"을 붙여 추정값임을 표시합니다.

## 6. 중단

- `cancel.supported = true`인 작업만 "중단 요청" 버튼이 켜집니다.
- 모니터는 `control/<id>.cancel` 파일만 만듭니다. **프로세스를 종료하지 않습니다.**
- 작업은 `reporter.cancel_requested()`를 주기적으로 확인하고, 스스로 정리 후 `cancelled`로 기록합니다.

## 7. 예시

```json
{
  "schema": "taskmonitor/1",
  "id": "3dmaker-20261015-112005-ab12",
  "name": "3DMaker 영상 생성·측정",
  "project": "3DMaker",
  "workdir": "C:\\work\\3DMaker",
  "subject": "sample2",
  "state": "running",
  "message": "sample2 프레임 생성 중",
  "pid": 12345,
  "created_at": "2026-10-15T11:20:05+09:00",
  "started_at": "2026-10-15T11:20:05+09:00",
  "updated_at": "2026-10-15T11:31:40+09:00",
  "stale_after_s": 120,
  "current_stage": "sample2",
  "stages": [
    {"key": "sample2", "name": "sample2 영상 생성·측정", "state": "running",
     "done": 462, "total": 599, "unit": "frames", "rate": 0.78, "rate_unit": "frames/s",
     "eta_seconds": 176, "started_at": "2026-10-15T11:20:05+09:00", "progress_at": "2026-10-15T11:31:40+09:00"},
    {"key": "sample3", "name": "sample3 영상 생성·측정", "state": "pending"},
    {"key": "verify", "name": "결과 파일 검증", "state": "pending"},
    {"key": "report", "name": "비교 이미지·보고서 저장", "state": "pending"},
    {"key": "sbs", "name": "SBS 변환", "state": "pending"}
  ],
  "log_path": "C:\\work\\3DMaker\\logs\\run.log",
  "results": [{"label": "보고서", "path": "C:\\work\\3DMaker\\out\\report.html", "kind": "file"}],
  "cancel": {"supported": true, "method": "file"},
  "seq": 812
}
```
