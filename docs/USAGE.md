# TaskMonitor 사용 가이드

Windows에서 오래 실행되는 작업의 진행 상황을 확인하고, 자주 쓰는 앱과 폴더를 여는 작업 모니터 겸 바로가기 런처입니다.
작은 캡슐에서 실행 상태와 예상 완료 시각을 보고, 작업을 누르면 단계별 진행률·처리량·결과·로그를 확인할 수 있습니다.

파일 변환, 데이터 처리, 모델 학습, 테스트, 보고서 생성처럼 여러 단계로 이루어진 작업에 사용할 수 있습니다.
기존 Python 프로세스를 자동 감지하거나, 작업에서 로컬 상태 파일 또는 공통 진행 로그를 전달해 연결합니다.

- Windows 네이티브 WPF (.NET 10), **외부 NuGet 패키지 없음**
- 작업 ↔ 모니터 연결은 **로컬 JSON 파일 하나**(작업당 1개) — 서버·DB·로그인·AI 호출 없음
- 상태를 기록하지 않는 기존 Python 작업도 **자동 감지**해서 표시 (명령·CPU·종료 코드·로그 진행률 추정)
- 실행 중인 작업의 상태 파일을 읽어 표시하며, 작업을 강제로 종료하지 않습니다. 중단 요청을 지원하도록 연결된 작업에는 요청을 전달할 수 있습니다.
- 바로가기: 앱(.exe)·폴더·파일·`.lnk`·`.url`·웹 주소를 등록하고, 원래 아이콘 그대로 펼쳐진 패널에서 한 번 눌러 실행/열기
- Python 외의 작업도 `taskmonitor/1` 상태 파일을 기록하거나 `TASK_PROGRESS_JSON` 로그를 래퍼로 연결해 표시할 수 있습니다.

```
화면 위 가운데에 떠 있는 캡슐
        ╭─────────────────────────────────────────────────────────────────╮
        │ ▦ 앱 6 │ ◔ 데이터 처리 54% · batch-A · ≈12:38 │ ⚠ 갱신 없음 │ ✕ 실패 │ ≡ │
        ╰─────────────────────────────────────────────────────────────────╯
                 ▼ '앱'을 누르면 펼쳐지는 바로가기 패널 (원래 아이콘, 3열)
```

## 폴더 구성

| 경로 | 내용 |
|---|---|
| `docs/STATE_CONTRACT.md` | **작업 상태 파일 계약 (taskmonitor/1)** — 필드, 저장 방식, 상태 판정, ETA 규칙 |
| `docs/GENERIC_TASKS.md` | 다른 작업 연결 방법과 공통 진행 로그 예제 |
| `docs/UI.md` | 화면 구성 |
| `src/TaskMonitor/` | WPF 모니터 (`Core/`는 WPF와 무관한 판정 로직, `ProcessDiscovery.cs`는 자동 감지) |
| `src/TaskMonitor.Check/` | 같은 판정 로직을 콘솔로 출력하는 확인 도구 |
| `python/taskstate.py` | 기존 스크립트에 붙이는 helper (표준 라이브러리만) + CLI |
| `python/examples/` | 상태 기록, 공통 진행 로그, 기존 로그 연결 예제 |
| `demo/` | 데모 작업 8종, 자동 검증(`selftest.py`) |
| `tools/` | 검증용 배치 파일 |

## 빌드 · 실행

Windows 및 .NET 10 Desktop Runtime이 필요합니다. 소스 빌드에는 .NET 10 SDK가 필요하며,
Python 연결 도구와 데모는 Python 3.9 이상에서 추가 패키지 없이 사용할 수 있습니다.

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
- 기본 저장 루트는 `%LOCALAPPDATA%\TaskMonitor`이며 작업 상태는 그 아래 `tasks\`에 저장됩니다.
  `TASKMONITOR_DIR` 환경 변수 또는 `TaskMonitor.exe --dir <폴더>`로 루트를 바꿀 수 있습니다. 작업과 모니터는 같은 루트를 사용해야 합니다.
- 배포용 단일 폴더: `powershell -File build.ps1 -Publish` → `publish\TaskMonitor\` (.NET 10 Desktop Runtime 필요)
- 이미 모니터가 실행 중이면 종료한 뒤 다시 빌드하세요. 빌드 후 `start_monitor.bat`으로 작업 창을 열 수 있습니다.

## 데모

```bat
run_demo.bat              :: 모니터를 띄우고 데모 작업 8개를 백그라운드로 실행 (약 2~3분)
python demo\run_demo.py --speed 3
python demo\selftest.py   :: UI 없이 Python 쪽 자동 검증
```

| 데모 작업 | 재현하는 상황 |
|---|---|
| 다단계 처리 | 입력별 처리 → 결과 검증 → 보고서 저장 → 후처리. 단계 가중치와 예상 소요로 **전체 진행률·ETA 표시**, 중단 요청 지원 |
| 후속 작업 | 앞선 작업이 끝날 때까지 **선행 작업 대기** 후 실행 |
| 입력 준비 / 파일 처리 | 시작 전 **대기**, `.partial`로 복사 → 완성 후 다른 작업의 **입력 대기** 해제 |
| 실패 처리 | 처리 도중 **실패** + 오류·traceback 기록 |
| 갱신 중단과 재개 | 실행 중 **갱신이 멈춤** → "실행 중 · 상태 갱신 없음" → 재개 후 완료 |
| 종료 기록 누락 | 완료 기록 없이 프로세스 종료 → **"종료 기록 없음"** |
| 기존 로그 연결 | `INFER_PROGRESS_JSON` 출력을 래퍼로 연결. 이후 단계 소요를 모르면 전체 ETA **"정보 부족"** |

## 바로가기 런처

캡슐의 **`앱 N`** 버튼을 누르면 바로가기 패널이 펼쳐지고, 아이콘을 누르면 앱이 실행되거나 폴더·파일·웹 주소가 열립니다.
패널 밖을 누르거나 실행하면 접힙니다(작업 창 → 바로가기 → "패널 고정"을 켜면 고정).

- **등록**: 패널의 `+ 추가`(파일·앱 / 폴더), 작업 창 **바로가기** 페이지(파일·앱 / 폴더 / 웹 주소), 또는 파일·폴더·`.lnk`를 **캡슐이나 바로가기 페이지에 끌어다 놓기**.
- **종류**: 앱(.exe/.bat/.cmd, 인자 수정 가능), 폴더(탐색기로 열기), 파일(연결된 프로그램), `.lnk`(바로가기 파일 그대로 실행 — 원래 인자·시작 폴더·관리자 권한 설정 유지), `.url`·웹 주소(선택한 브라우저).
- **웹 브라우저**: 설정 → **웹 바로가기 브라우저**에서 Chrome(초기값) 또는 Windows 기본 브라우저를 선택합니다. Chrome이 없으면 Windows 기본 브라우저로 엽니다. 이 선택은 모니터의 웹 바로가기에만 적용됩니다.
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
  로그에 `TASK_PROGRESS_JSON`, `INFER_PROGRESS_JSON` 또는 tqdm 진행 표시가 있으면 진행률·속도·예상 완료 시각까지 표시합니다.
- **작업 폴더의 상태 기록 읽기**: 명령 인자로 준 폴더(`--batch` 등)에 `status.json`(또는 `progress.json`·`state.json`)이 있으면
  그 기록을 우선 사용합니다. 하위 폴더의 `status.json`(상태 + `condition_key`/`stage`/`step` 등)은 **단위 작업**으로,
  `manifest.json`·`plan.json`·`request.json`의 `segments × conditions`(또는 `steps`)는 **전체 계획**으로 읽어
  "완료 8/15 · 진행: batch-A · 파일 변환"처럼 끝난 것·진행 중·남은 것을 단계 목록으로 보여 줍니다.
  상태 기록의 `"log"`가 가리키는 로그에서 현재 단계 처리량을 읽고, 남은 단계는 **같은 조건의 완료 소요 평균**으로 전체 예상 완료(≈)를 계산합니다.
  확인: 배치 폴더를 `tools\diag_workstatus.bat`에 끌어다 놓으면 읽은 결과가 `.taskmonitor_test\workstatus.log`에 나옵니다.
- **상태 표시**: "실행 중"(로그 진행 기준), "실행 중 · 로그 갱신 없음", "실행 중 · 활동 없음"(CPU 사용이 오래 없음 — 멈춤/대기/GPU 작업 구분 불가),
  "종료 · 코드 0"(**완료로 표시하지 않음**, 결과는 직접 확인), "오류 종료 · 코드 N", "종료 · 코드 모름"(모니터가 꺼져 있는 동안 끝남).
- 모니터가 쓰는 것은 `%LOCALAPPDATA%\TaskMonitor\auto\*.json`(자동 감지 기록)과 `auto\_discovery.log`(감지 판단 기록)뿐이며, 프로세스는 건드리지 않습니다.
- 안 잡힐 때: `auto\_discovery.log`에서 제외 사유를 보거나 `tools\diag_detect.bat`(감지 코드를 콘솔로 실행)를 쓰세요.
  대상 이름·제외 목록·최소 실행 시간은 `settings.json`의 `auto_detect_*` 항목으로 바꿀 수 있습니다.

## 작업 연결

### UI에서 실행 중인 작업 연결

1. 작업 화면의 **+ 작업 연결**을 누릅니다. 이미 자동 감지된 작업은 상세의 **연결 설정**을 누르면 해당 프로세스가 선택됩니다.
2. 이름·명령·PID로 프로세스를 찾아 선택합니다. 확인할 수 있는 상태 파일과 로그 경로를 자동으로 채웁니다.
3. 필요한 경우 **찾기…**로 상태 JSON이나 로그를 바꾸고 **미리보기**를 확인합니다.
4. **연결 저장**을 누르면 기존 프로세스를 그대로 모니터링합니다. 연결 정보는 모니터 재시작 후에도 유지됩니다.

**진행 정보 연결 항목**을 펼치면 작업 상태·현재 단계·처리한 수량·전체 수량·입력 이름을 JSON 항목과 연결할 수 있습니다.
중첩 항목은 `current.done`처럼 표시합니다. 전체 수량을 직접 입력할 수도 있으며, 모르면 비워 두면 됩니다.
현재 입력 번호와 전체 입력 개수는 프레임 처리량과 다른 단위이므로 자동으로 섞지 않습니다.

파일과 로그는 읽기만 하며 작업 실행·재시작이나 원본 파일 변경은 하지 않습니다. 로그만 연결하면 지원하는 진행 형식을 읽고,
상태 파일의 명시적 완료 기록이 없는 작업은 프로세스 종료만으로 완료라고 표시하지 않습니다.
JSON에 전체 단계 계획이 없으면 연결 이후의 현재 단계와 수량을 표시하며, 이전 단계·미실행 단계를 임의로 만들지 않습니다.
상태 파일이 쓰는 도중 잠시 읽히지 않으면 마지막 정상값을 유지하고 연결 오류를 표시한 뒤 다음 갱신에 다시 읽습니다.

### 코드·명령줄에서 연결

`python/taskstate.py` 한 파일만 복사하면 됩니다(또는 `PYTHONPATH`에 `python/` 추가).

`TASK_PROGRESS_JSON`의 `done/total/unit`으로 파일 수·반복 횟수 등을 전달하고,
단계 계획을 선언하면 완료·실행 중·대기 목록을 함께 보여 줍니다. [다른 작업 연결 가이드와 실행 예제](GENERIC_TASKS.md)를 참고하세요.

| 방식 | 적합한 경우 | 표시 정보 |
|---|---|---|
| 자동 감지 | 기존 Python 작업을 수정하지 않고 확인 | 실행 여부·CPU 활동·관찰된 종료 코드·연결된 로그의 진행률 |
| 공통 진행 로그 + 래퍼 | 진행 로그를 출력하는 명령을 실행 | 처리량·단계 전환·미리 선언한 대기 단계·종료 코드에 따른 완료와 실패 |
| 상태 파일 기록 | 정확한 단계·완료 판정이 필요한 작업 | 단계 계획·대기 사유·검증·결과·오류·중단 요청 지원 |

**1) Python 코드에서 상태 기록**

```python
from taskstate import TaskReporter

def run(items):
    with TaskReporter("데이터 처리", project="배치 작업",
                      stages=[("process", "데이터 처리"), ("verify", "결과 검증")],
                      cancel_supported=True) as task:
        task.begin_stage("process", total=len(items), unit="items")
        for index, item in enumerate(items, start=1):
            # 여기에 실제 처리 코드
            task.progress(index)             # 기본 1초마다 모아서 기록
            if task.cancel_requested():
                task.cancelled()
                return
        task.end_stage()
        task.begin_stage("verify", validating=True)
        # 여기에 결과 검증 코드
        task.end_stage()
        task.succeed()                       # 작업 전체의 명시적 완료 기록
```

선행 작업·입력 대기: `task.wait_for_tasks([other_id])`, `task.wait_for_inputs([path])` (`.partial`/`.tmp`는 완성본으로 보지 않음).
결과 파일·폴더는 `task.add_result("보고서", "out/report.html")`처럼 등록합니다.
전체 진행률은 모든 단계에 `weight`가, 전체 ETA는 이후 단계에 `expected_seconds`가 있을 때만 표시됩니다.

**2) 공통 진행 로그를 래퍼로 연결**

작업에서 다음 형식의 JSON을 한 줄씩 출력하고 flush합니다. `done`은 필수이며 나머지 필드는 선택 사항입니다.

```text
TASK_PROGRESS_JSON {"stage":"convert","done":12,"total":100,"unit":"files","rate":2.5,"subject":"batch-A"}
```

단위는 `files`, `items`, `epochs`, `checks` 등 작업에 맞게 지정합니다. 다음 명령은 저장소에 포함된 예제를 실행합니다.

```bat
python python\taskstate.py wrap --name "파일 처리" --stage prepare:준비 --stage convert:변환 --stage verify:검증 -- python python\examples\example_generic_progress.py
```

stdout/stderr를 로그 파일에 저장하고 `done/total/rate/eta_seconds`를 진행률로 반영합니다.
`stage`가 바뀌면 이전 단계를 완료하고 다음 단계를 시작하며, `subject`는 처리 중인 입력 이름으로 표시합니다.
`--stage`로 계획을 미리 선언해야 아직 실행하지 않은 단계까지 대기로 보입니다.
종료 코드 0이면 완료로 기록하고 근거를 종료 코드로 남깁니다. 다른 종료 코드는 실패와 마지막 로그를 기록합니다.
Python 외 실행 파일도 `--` 뒤에 지정할 수 있으며, 기존 `INFER_PROGRESS_JSON` 형식도 계속 지원합니다.

**3) 이미 다른 프로그램이 실행한 작업**

```bat
python python\taskstate.py follow-log --name "데이터 처리" --log C:\work\logs\run.log --pid 12345
python python\taskstate.py register --name "외부 작업" --pid 12345 --log C:\work\logs\run.log
```

`follow-log`는 진행 로그를 따라 읽고, `register`는 작업 정보와 PID·로그 경로를 등록합니다.
명시적인 종료 기록 없이 프로세스가 끝나면 완료·실패를 추정하지 않고 "종료 기록 없음"으로 표시합니다.
배치 파일에서 상태만 바꿀 때: `python python\taskstate.py set --id <ID> --state succeeded`.

## 저장 방식 (깨지지 않게)

작업 하나 = 파일 하나(`tasks\<id>.json`). 쓰기는 **잠금(`<id>.json.lock`) → 현재 파일 읽기 → 수정 → 임시 파일에 쓰고 fsync → `os.replace`**.
읽는 쪽은 이전 완성본이나 새 완성본만 봅니다. 모니터는 `FileShare.ReadWrite|Delete`로 읽고, 실패하면 직전 정상본을 유지합니다.
자세한 내용은 `docs/STATE_CONTRACT.md`.

## 검증 도구

저장소 루트에서 실행합니다.

```bat
python demo\selftest.py
python -m unittest discover -s demo -p test_progress.py
dotnet run --project src\TaskMonitor.Check -- --progress-selftest
dotnet run --project src\TaskMonitor.Check -- --connection-selftest .taskmonitor_test\connections
tools\test_shortcuts.bat
```

- `demo/selftest.py`: 여러 프로세스의 동시 갱신, 작업 상태 전이, 선행 작업·입력 대기, 오류와 종료 기록 검증
- `demo/test_progress.py`: 공통 진행 로그와 기존 형식, 단계 전환, 래퍼의 완료·실패 처리 검증
- `--progress-selftest`: 모니터의 JSON·tqdm 진행 로그 해석 검증
- `--connection-selftest`: JSON 항목 연결, 중복 등록 방지, 연결 복원, 불완전한 파일에서의 복구와 종료 처리 검증 (자체 테스트 프로세스 사용)
- `tools/test_shortcuts.bat`: 바로가기 종류 판별, 실행 정보, 아이콘 추출, 저장과 백업 복구 검증
- `tools/verify_auto.bat`: 기존 Python 작업의 자동 감지와 로그 연결 확인

## 제한 사항

- 자동 감지는 **추정**입니다. 표준 출력을 파일로 남기지 않는 스크립트는 진행률을 알 수 없고("실행 중"과 CPU만 표시),
  모니터가 꺼져 있는 동안 끝난 작업은 종료 코드를 모릅니다. 정확한 단계·완료 판정이 필요한 작업은 `taskstate`를 붙이세요.
- 일반 문장 로그만으로 아직 실행하지 않은 단계나 전체 수량을 정확히 알 수는 없습니다. 완료·실행 중·대기 목록 전체를 표시하려면 단계 계획과 전환 기록이 필요합니다.

- **ETA는 추정값**입니다. 단계 평균 속도와 최근 속도를 섞고 완만하게 따라가게 했지만, 속도가 크게 바뀌는 작업에서는 오차가 큽니다.
  모니터를 재시작하면 최근 속도 이력은 다시 쌓입니다(단계 평균은 파일의 시작 시각으로 바로 계산).
- `pid`가 없는 작업은 생존 확인을 못 하므로 "상태 갱신 없음"까지만 판단합니다. 다른 PC에서 기록한 작업도 마찬가지입니다.
- 중단은 작업이 `cancel_requested()`를 확인하는 경우에만 동작합니다. `wrap`·`follow-log`로 연결한 작업은 "중단 미지원"으로 표시됩니다.
- 상태 폴더는 1초마다 확인합니다(파일 수십 개 기준). 수천 개가 쌓이면 "목록에서 숨기기"(archive 이동)로 정리하세요.
- 캡슐을 위쪽 귀퉁이에 배치하면 최대화된 창의 닫기 버튼을 가릴 수 있어,
  위쪽에 두려면 "화면 위쪽에 고정(공간 예약)"을 권장합니다.
- 설치 프로그램·자동 업데이트는 없습니다. `--dir`로 상태 폴더를 바꾸면 그 폴더마다 모니터 하나만 뜹니다.
