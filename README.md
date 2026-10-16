# TaskMonitor (Window_Navigation)

여러 프로젝트의 장시간 Python 작업(영상 생성·인코딩·분석·측정)을 **한 번 띄워 두면 화면 위 가운데의 작은 다크 캡슐**로 보여 주고,
자주 쓰는 앱·폴더 **바로가기 런처**도 겸하는 Windows 프로그램입니다. "돌고 있나? / 얼마나 남았나? / 끝났나?"를 캡슐의 진행 링으로 보고,
누르면 작업 창이 열립니다.

- Windows 네이티브 WPF (.NET 10), **외부 NuGet 패키지 없음**
- 작업 ↔ 모니터 연결은 **로컬 JSON 파일 하나**(작업당 1개) — 서버·DB·로그인·AI 호출 없음
- 상태를 기록하지 않는 기존 Python 작업도 **자동 감지**해서 표시 (명령·CPU·종료 코드·로그 진행률 추정)
- 모니터는 상태 파일을 **읽기만** 합니다. 작업을 실행·재실행·강제 종료하지 않습니다.
- 바로가기: 앱(.exe)·폴더·파일·`.lnk`·`.url`·웹 주소를 등록하고, 원래 아이콘 그대로 펼쳐진 패널에서 한 번 눌러 실행/열기
- 3DMaker·MpdMaker 코드는 건드리지 않습니다. Python 작업은 그대로 외부 프로세스입니다.

```
화면 위 가운데에 떠 있는 캡슐 (시안 B)
        ╭─────────────────────────────────────────────────────────────────╮
        │ ▦ 앱 6 │ ◔ 3DMaker 54% · sample2 · ≈12:38 │ ⚠ 갱신 없음 │ ✕ 실패 │ ≡ │
        ╰─────────────────────────────────────────────────────────────────╯
                 ▼ '앱'을 누르면 펼쳐지는 바로가기 패널 (원래 아이콘, 3열)
```

## 폴더 구성

| 경로 | 내용 |
|---|---|
| `docs/STATE_CONTRACT.md` | **작업 상태 파일 계약 (taskmonitor/1)** — 필드, 저장 방식, 상태 판정, ETA 규칙 |
| `docs/UI.md` | 화면 구성 |
| `src/TaskMonitor/` | WPF 모니터 (`Core/`는 WPF와 무관한 판정 로직, `ProcessDiscovery.cs`는 자동 감지) |
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

- 실행하면 캡슐만 뜹니다(기본: 화면 위 가운데, 항상 위, 포커스를 빼앗지 않음, 작업 표시줄·Alt+Tab에 안 보임).
- 캡슐의 작업 칩이나 `≡`를 누르면 작업 창(다크, 사이드바: **작업 / 바로가기 / 설정**)이 열립니다. 창을 닫아도 캡슐과 작업은 그대로입니다.
- 모니터를 완전히 끄려면 캡슐 **우클릭 → 모니터 종료** 또는 작업 창 왼쪽 아래 ⏻ (작업은 계속 실행).
- 캡슐 우클릭 메뉴 / 설정 페이지: 위치(위·아래 × 왼쪽·가운데·오른쪽), 항상 위, 전체 화면 프로그램 실행 시 숨기기,
  Python 작업 자동 감지, **Windows 시작 시 실행**.
- 캡슐의 빈 곳을 끌어 놓으면 가장 가까운 자리(6곳 중)에 붙습니다(다른 모니터로 옮기면 그 모니터 기준).
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

## 바로가기 런처

캡슐의 **`앱 N`** 버튼을 누르면 바로가기 패널이 펼쳐지고, 아이콘을 누르면 앱이 실행되거나 폴더·파일·웹 주소가 열립니다.
패널 밖을 누르거나 실행하면 접힙니다(작업 창 → 바로가기 → "패널 고정"을 켜면 고정).

- **등록**: 패널의 `+ 추가`(파일·앱 / 폴더), 작업 창 **바로가기** 페이지(파일·앱 / 폴더 / 웹 주소), 또는 파일·폴더·`.lnk`를 **캡슐이나 바로가기 페이지에 끌어다 놓기**.
- **종류**: 앱(.exe/.bat/.cmd, 인자 수정 가능), 폴더(탐색기로 열기), 파일(연결된 프로그램), `.lnk`(바로가기 파일 그대로 실행 — 원래 인자·시작 폴더·관리자 권한 설정 유지), `.url`·웹 주소(기본 브라우저).
- **아이콘**: Windows 셸이 탐색기와 같은 방식으로 뽑은 원래 아이콘(48px)을 씁니다. `.lnk`는 지정 아이콘 → 대상 아이콘 순.
- **관리**: 바로가기 페이지에서 이름·인자 고치기, 실행, 위치 열기, ▲▼ 순서, 제거(대상 파일은 건드리지 않음). 패널 타일 우클릭으로도 가능.
- 대상이 없어진 바로가기는 흐리게 표시되고 눌러도 실행하지 않습니다.
- 저장: `%LOCALAPPDATA%\TaskMonitor\shortcuts.json` (+ `.bak` 백업, 원자적 교체).
- 확인: `tools\test_shortcuts.bat` — 종류 판별, `.lnk` 대상·인자 읽기, 실행 정보, 저장·백업 복구, 아이콘 추출을 실행 없이 점검.

## 자동 감지 (상태를 기록하지 않는 Python 작업)

`taskstate`를 붙이지 않은 스크립트도 모니터가 스스로 찾아 목록·캡슐에 표시합니다. 끄려면 캡슐 우클릭 또는 설정 페이지 → "실행 중인 Python 작업 자동 감지".

- **대상**: 현재 사용자의 `python` / `pythonw` 프로세스(스토어판·`py` 런처가 실행하는 `python3.12` 같은 이름 포함) 중 15초 이상 실행된 것.
  IDE·Jupyter·pip·린터 등 도구 프로세스와, 이미 `taskstate`로 기록 중인 작업은 제외합니다.
- **알 수 있는 것**: 실행 명령, 작업 폴더, 입력 이름(`--input` 등), 시작 시각, CPU 사용, **종료 코드**(모니터가 켜져 있는 동안 끝난 경우).
- **로그 추정**: 명령 인자의 `.log`/`.txt` → 작업·스크립트 폴더(및 `log`/`logs`/`output` 하위)에서 시작 이후 갱신된 `.log` 중
  **파일 이름에 그 작업의 입력 이름이 들어간 것**. 같은 폴더에 다른 작업이 있어 구분할 수 없으면 추정하지 않습니다.
  로그에 `INFER_PROGRESS_JSON` 또는 tqdm 진행 표시가 있으면 진행률·속도·예상 완료 시각까지 표시합니다.
- **작업 폴더의 상태 기록 읽기**: 명령 인자로 준 폴더(`--batch` 등)에 `status.json`(또는 `progress.json`·`state.json`)이 있으면
  그 기록을 우선 사용합니다. 하위 폴더의 `status.json`(상태 + `condition_key`/`stage`/`step` 등)은 **단위 작업**으로,
  `manifest.json`·`plan.json`·`request.json`의 `segments × conditions`(또는 `steps`)는 **전체 계획**으로 읽어
  "완료 8/15 · 진행: sample7_F4725_F5024 · 경계를 완만하게 한 고정식"처럼 끝난 것·진행 중·남은 것을 단계 목록으로 보여 줍니다.
  상태 기록의 `"log"`가 가리키는 로그에서 현재 단계 처리량을 읽고, 남은 단계는 **같은 조건의 완료 소요 평균**으로 전체 예상 완료(≈)를 계산합니다.
  확인: 배치 폴더를 `tools\diag_workstatus.bat`에 끌어다 놓으면 읽은 결과가 `.taskmonitor_test\workstatus.log`에 나옵니다.
- **상태 표시**: "실행 중"(로그 진행 기준), "실행 중 · 로그 갱신 없음", "실행 중 · 활동 없음"(CPU 사용이 오래 없음 — 멈춤/대기/GPU 작업 구분 불가),
  "종료 · 코드 0"(**완료로 표시하지 않음**, 결과는 직접 확인), "오류 종료 · 코드 N", "종료 · 코드 모름"(모니터가 꺼져 있는 동안 끝남).
- 모니터가 쓰는 것은 `%LOCALAPPDATA%\TaskMonitor\auto\*.json`(자동 감지 기록)과 `auto\_discovery.log`(감지 판단 기록)뿐이며, 프로세스는 건드리지 않습니다.
- 안 잡힐 때: `auto\_discovery.log`에서 제외 사유를 보거나 `tools\diag_detect.bat`(감지 코드를 콘솔로 실행)를 쓰세요.
  대상 이름·제외 목록·최소 실행 시간은 `settings.json`의 `auto_detect_*` 항목으로 바꿀 수 있습니다.

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
| Python 자동 검증 (`demo/selftest.py`, Windows) | 29개 항목 모두 통과 — 4개 프로세스 × 150회 동시 갱신 중 약 11만 회 읽기에서 깨진 JSON 0회, 덮어쓰기 0회 |
| 1. 여러 작업 동시 표시 | 8개 데모 작업이 바·목록에 동시에 표시 |
| 2. 여러 단계 순차 진행 | 파이프라인 5단계, 단계별 처리량·속도·진행률, 단계/전체 진행률 분리 |
| 3. 선행 작업·입력 대기 | "선행 작업 대기"(대상 작업 상태 함께 표시), "입력 대기"(`.partial` 작성 중에는 해제되지 않음) |
| 4. 완료·실패 | 완료 / 실패(오류 메시지·단계·traceback·로그 오류 줄) / 중단 요청 → 작업이 스스로 "중단", 선행 작업이 중단되자 후속 작업은 실패 기록 |
| 5. 갱신 멈춤 | "실행 중 · 상태 갱신 없음" + 마지막 갱신 시각, 재개 후 정상 진행. 완료 기록 없이 끝난 프로세스는 "종료 기록 없음" |
| 6. 닫았다 다시 열기 | 모니터 종료 후 재실행 시 기존 작업·기록 그대로 표시, 작업은 영향 없음 |
| 7. 결과·로그 열기 | 결과 파일/폴더 열기, 전체 로그 열기 동작 확인. 사라진 결과는 "완료 · 결과 확인 필요" |

| 자동 감지 — 실제 작업 | 3DMaker `-m dev.scripts.run_dibr_hole_color --batch …`(37분 45초, 코드 0)과 MPDMaker `run_sbs.py`(코드 0, 로그 자동 연결)를 상태 기록 없이 감지 |
| 자동 감지 — 데모 (`tools\verify_auto.bat`) | 로그 있는 작업: tqdm 진행률·ETA 표시 / 로그 없는 작업: "로그 파일을 찾지 못함"(남의 로그를 가져오지 않음) / `exit 2` 작업: "오류 종료 · 코드 2". Python 3.11(python.exe)·3.12 스토어판(python3.12.exe) 모두 감지 |

검증 중 발견해 고친 것: 프로세스가 마지막 기록을 쓰고 바로 끝날 때 1초 정도 "종료 기록 없음"이 보이던 경쟁 상태,
상태가 바뀔 때 목록 줄이 움직이던 문제, 바가 너무 길어 목록 버튼이 잘리던 문제, 배치 파일의 한글·`chcp` 해석 오류,
자동 감지가 `python3.12` 이름의 프로세스를 놓치던 문제, 같은 폴더의 다른 작업 로그를 진행률로 잘못 가져오던 문제.

## 남은 제한

- 자동 감지는 **추정**입니다. 표준 출력을 파일로 남기지 않는 스크립트는 진행률을 알 수 없고("실행 중"과 CPU만 표시),
  모니터가 꺼져 있는 동안 끝난 작업은 종료 코드를 모릅니다. 정확한 단계·완료 판정이 필요한 작업은 `taskstate`를 붙이세요.

- **ETA는 추정값**입니다. 단계 평균 속도와 최근 속도를 섞고 완만하게 따라가게 했지만, 속도가 크게 바뀌는 작업에서는 오차가 큽니다.
  모니터를 재시작하면 최근 속도 이력은 다시 쌓입니다(단계 평균은 파일의 시작 시각으로 바로 계산).
- `pid`가 없는 작업은 생존 확인을 못 하므로 "상태 갱신 없음"까지만 판단합니다. 다른 PC에서 기록한 작업도 마찬가지입니다.
- 중단은 작업이 `cancel_requested()`를 확인하는 경우에만 동작합니다. `wrap`·`follow-log`로 연결한 작업은 "중단 미지원"으로 표시됩니다.
- 상태 폴더는 1초마다 확인합니다(파일 수십 개 기준). 수천 개가 쌓이면 "목록에서 숨기기"(archive 이동)로 정리하세요.
- 바는 작업 표시줄 위 귀퉁이에 기본 배치됩니다. 위쪽 귀퉁이를 고르면 최대화된 창의 닫기 버튼을 가릴 수 있어,
  위쪽에 두려면 "화면 위쪽에 고정(공간 예약)"을 권장합니다.
- 설치 프로그램·자동 업데이트는 없습니다. `--dir`로 상태 폴더를 바꾸면 그 폴더마다 모니터 하나만 뜹니다.
