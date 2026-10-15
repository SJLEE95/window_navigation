# TaskMonitor (Window_Navigation)

여러 프로젝트의 장시간 Python 작업(영상 생성·인코딩·분석·측정)을 **한 번 띄워 두면 화면 귀퉁이의 한 줄 바**로 보여 주는
Windows 프로그램입니다. "돌고 있나? / 얼마나 남았나? / 끝났나?"를 AI에게 묻는 대신 바를 보고, 칩을 누르면 상세 창이 열립니다.

- Windows 네이티브 WPF (.NET 10), **외부 NuGet 패키지 없음**
- 작업 ↔ 모니터 연결은 **로컬 JSON 파일 하나**(작업당 1개) — 서버·DB·로그인·AI 호출 없음
- 모니터는 상태 파일을 **읽기만** 합니다. 작업을 실행·재실행·강제 종료하지 않습니다.
- 3DMaker·MpdMaker 코드는 건드리지 않습니다. Python 작업은 그대로 외부 프로세스입니다.

```
화면 오른쪽 아래(작업 표시줄 바로 위)에 붙는 바
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│⋮⋮ ▶ 실행 2 ⏸ 대기 1 ⚠ 확인 1 ✕ 실패 1 │≡ 목록│ ⚠ 갱신 없음 측정 작업 · 30% │ ▶ 실행 중 3DMaker… · 54% · ≈12:38 │ +3 │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

## 폴더 구성

| 경로 | 내용 |
|---|---|
| `docs/STATE_CONTRACT.md` | **작업 상태 파일 계약 (taskmonitor/1)** — 필드, 저장 방식, 상태 판정, ETA 규칙 |
| `docs/UI.md` | 화면 구성 |
| `src/TaskMonitor/` | WPF 모니터 (`Core/`는 WPF와 무관한 판정 로직) |
| `src/TaskMonitor.Check/` | 같은 판정 로직을 콘솔로 출력하는 확인 도구 |
| `python/taskstate.py` | 기존 스크립트에 붙이는 helper (표준 라이브러리만) + CLI |
| `python/examples/` | 파이프라인 연결 예제, `INFER_PROGRESS_JSON` 연결 예제 |
| `demo/` | 데모 작업 8종, 자동 검증(`selftest.py`) |
| `tools/` | 검증용 배치 파일 |

## 빌드 · 실행

```bat
build.bat                 :: 또는  dotnet build TaskMonitor.slnx -c Release
src\TaskMonitor\bin\Release\net10.0-windows\TaskMonitor.exe
```

- 실행하면 바만 뜹니다. 칩 또는 `≡ 목록`을 누르면 작업 목록·상세 창이 열립니다.
- 상세 창을 닫아도 바와 작업은 그대로입니다. 모니터를 완전히 끄려면 바 **우클릭 → 모니터 종료** (작업은 계속 실행).
- 바 우클릭 메뉴: 위치(네 귀퉁이 / 화면 위·아래에 공간 예약 고정), 항상 위, 전체 화면 프로그램 실행 시 숨기기,
  최근 완료 작업 표시, **Windows 시작 시 실행**, 상태 폴더 열기.
- 바 왼쪽 `⋮⋮`를 끌어 놓으면 가장 가까운 귀퉁이에 붙습니다(다른 모니터로 옮기면 그 모니터 기준).
- 상태 폴더 기본값: `%LOCALAPPDATA%\TaskMonitor\tasks`. 바꾸려면 `TASKMONITOR_DIR` 환경 변수 또는 `TaskMonitor.exe --dir <폴더>`.
- 배포용 단일 폴더: `powershell -File build.ps1 -Publish` → `publish\TaskMonitor\` (.NET 10 Desktop Runtime 필요)

## 데모

```bat
run_demo.bat              :: 모니터를 띄우고 데모 작업 8개를 백그라운드로 실행 (약 2~3분)
python demo\run_demo.py --speed 3
python demo\selftest.py   :: UI 없이 Python 쪽 자동 검증
```

| 데모 작업 | 재현하는 상황 |
|---|---|
| 3DMaker 데모 파이프라인 | sample2 → sample3 → 검증(검증 중) → 보고서(`.partial` 후 이름 변경) → SBS. 단계 가중치·평소 소요가 있어 **전체 진행률·전체 ETA 표시**. 중단 지원 |
| MpdMaker 데모 | 파이프라인 완료를 **선행 작업 대기** 후 실행 |
| sample4 입력 준비 / 인코딩 | 시작 전 **대기**, `.partial`로 복사 → 완성 후 다른 작업의 **입력 대기** 해제 |
| 인코딩 테스트 | 31%에서 **실패** + 오류·traceback 기록 |
| 측정 작업 | 실행 중 **갱신이 멈춤** → "실행 중 · 상태 갱신 없음"(실패로 바꾸지 않음) → 재개 후 완료 |
| 영상 생성 | 완료 기록 없이 프로세스 종료 → **"종료 기록 없음"** |
| 기존 추론 스크립트 | 수정 없는 스크립트의 `INFER_PROGRESS_JSON` 출력을 래퍼로 연결. 이후 단계 소요를 몰라 전체 ETA **"정보 부족"** |

## Python 연결

`python/taskstate.py` 한 파일만 복사하면 됩니다(또는 `PYTHONPATH`에 `python/` 추가).

**1) 스크립트에 몇 줄 추가** (`python/examples/example_pipeline.py`)

```python
from taskstate import TaskReporter

with TaskReporter("3DMaker 영상 생성·측정", project="3DMaker",
                  stages=[("sample2", "sample2 영상 생성·측정"), ("sample3", "sample3 영상 생성·측정"),
                          ("verify", "결과 파일 검증"), ("report", "비교 이미지·보고서 저장"), ("sbs", "SBS 변환")],
                  log_path="out/run.log", cancel_supported=True) as t:
    t.begin_stage("sample2", total=599, unit="frames", subject="sample2")
    for i in range(599):
        ...                                   # 기존 처리 그대로
        t.progress(i + 1)                     # 1초에 한 번만 파일에 씀
        if t.cancel_requested():              # 모니터의 "중단 요청"
            t.cancelled(); return
    t.end_stage()
    t.begin_stage("verify", validating=True)  # 생성이 끝나도 전체는 '검증 중'
    ...
    t.add_result("보고서", "out/report.html")
    t.succeed()                               # 명시적 완료 기록 (없으면 완료로 보지 않음)
```

선행 작업·입력 대기: `t.wait_for_tasks([other_id])`, `t.wait_for_inputs([path])` (`.partial`/`.tmp`는 완성본으로 보지 않음).
전체 진행률은 모든 단계에 `weight`가, 전체 ETA는 이후 단계에 `expected_seconds`가 있을 때만 표시됩니다.

**2) 스크립트 수정 없이** — `INFER_PROGRESS_JSON {"frame":462,"total":599,"fps":0.78,"eta_seconds":176}` 를 출력하는 경우

```bat
python python\taskstate.py wrap --name "sample2 추론" --project 3DMaker --stage infer:추론 -- python infer.py --input sample2
```

stdout/stderr를 로그 파일에 저장하고 `frame/total/fps/eta_seconds`를 진행률로 반영합니다. JSON에 `"stage"`가 있으면 단계 전환,
`"sample"`이 있으면 처리 중인 입력으로 표시합니다. 종료 코드 0이면 "완료(근거: 종료 코드)", 아니면 실패 + 로그 마지막 줄.

**3) 이미 다른 프로그램이 실행한 작업**

```bat
python python\taskstate.py follow-log --name "sample2 추론" --log C:\work\logs\infer.log --pid 12345
python python\taskstate.py register --name "외부 인코딩" --pid 12345 --log C:\work\enc.log
```

로그만 따라 읽습니다. 프로세스가 끝나도 완료/실패를 추정하지 않으므로 모니터는 "종료 기록 없음"으로 표시합니다.
배치 파일에서 상태만 바꿀 때: `python python\taskstate.py set --id <ID> --state succeeded`.

## 저장 방식 (깨지지 않게)

작업 하나 = 파일 하나(`tasks\<id>.json`). 쓰기는 **잠금(`<id>.json.lock`) → 현재 파일 읽기 → 수정 → 임시 파일에 쓰고 fsync → `os.replace`**.
읽는 쪽은 이전 완성본이나 새 완성본만 봅니다. 모니터는 `FileShare.ReadWrite|Delete`로 읽고, 실패하면 직전 정상본을 유지합니다.
자세한 내용은 `docs/STATE_CONTRACT.md`.

## 검증 결과 (2026-10-15, Windows 11 · .NET SDK 10.0.401 · Python 3.11.5)

| 완료 기준 | 결과 |
|---|---|
| 빌드 | `dotnet build -c Release` 경고 0 · 오류 0 |
| Python 자동 검증 (`demo/selftest.py`, Windows) | 31개 항목 모두 통과 — 4개 프로세스 × 150회 동시 갱신 중 10만여 회 읽기에서 깨진 JSON 0회, 덮어쓰기 0회 |
| 1. 여러 작업 동시 표시 | 8개 데모 작업이 바·목록에 동시에 표시 |
| 2. 여러 단계 순차 진행 | 파이프라인 5단계, 단계별 처리량·속도·진행률, 단계/전체 진행률 분리 |
| 3. 선행 작업·입력 대기 | "선행 작업 대기"(대상 작업 상태 함께 표시), "입력 대기"(`.partial` 작성 중에는 해제되지 않음) |
| 4. 완료·실패 | 완료 / 실패(오류 메시지·단계·traceback·로그 오류 줄) / 중단 요청 → 작업이 스스로 "중단", 선행 작업이 중단되자 후속 작업은 실패 기록 |
| 5. 갱신 멈춤 | "실행 중 · 상태 갱신 없음" + 마지막 갱신 시각, 재개 후 정상 진행. 완료 기록 없이 끝난 프로세스는 "종료 기록 없음" |
| 6. 닫았다 다시 열기 | 모니터 종료 후 재실행 시 기존 작업·기록 그대로 표시, 작업은 영향 없음 |
| 7. 결과·로그 열기 | 결과 파일/폴더 열기, 전체 로그 열기 동작 확인. 사라진 결과는 "완료 · 결과 확인 필요" |

검증 중 발견해 고친 것: 프로세스가 마지막 기록을 쓰고 바로 끝날 때 1초 정도 "종료 기록 없음"이 보이던 경쟁 상태,
상태가 바뀔 때 목록 줄이 움직이던 문제, 바가 너무 길어 목록 버튼이 잘리던 문제, 배치 파일의 한글·`chcp` 해석 오류.

## 남은 제한

- **ETA는 추정값**입니다. 단계 평균 속도와 최근 속도를 섞고 완만하게 따라가게 했지만, 속도가 크게 바뀌는 작업에서는 오차가 큽니다.
  모니터를 재시작하면 최근 속도 이력은 다시 쌓입니다(단계 평균은 파일의 시작 시각으로 바로 계산).
- `pid`가 없는 작업은 생존 확인을 못 하므로 "상태 갱신 없음"까지만 판단합니다. 다른 PC에서 기록한 작업도 마찬가지입니다.
- 중단은 작업이 `cancel_requested()`를 확인하는 경우에만 동작합니다. `wrap`·`follow-log`로 연결한 작업은 "중단 미지원"으로 표시됩니다.
- 상태 폴더는 1초마다 확인합니다(파일 수십 개 기준). 수천 개가 쌓이면 "목록에서 숨기기"(archive 이동)로 정리하세요.
- 바는 작업 표시줄 위 귀퉁이에 기본 배치됩니다. 위쪽 귀퉁이를 고르면 최대화된 창의 닫기 버튼을 가릴 수 있어,
  위쪽에 두려면 "화면 위쪽에 고정(공간 예약)"을 권장합니다.
- 설치 프로그램·자동 업데이트는 없습니다. `--dir`로 상태 폴더를 바꾸면 그 폴더마다 모니터 하나만 뜹니다.
