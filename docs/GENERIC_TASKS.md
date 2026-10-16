# 다른 작업 연결하기

모니터의 화면과 상태 파일(`taskmonitor/1`)은 프로젝트에 독립적입니다. 영상뿐 아니라 파일 변환, 데이터 처리, 학습, 테스트에도 같은 단계 목록을 쓸 수 있습니다.

| 작업이 제공하는 정보 | 화면에서 표시할 수 있는 것 |
|---|---|
| 프로세스만 있음 | 실행 여부, CPU 활동, 시작·종료와 관찰된 종료 코드 |
| 연결된 로그에 tqdm 또는 공통 진행 JSON이 있음 | 현재 단계 처리량·진행률·속도·단계 ETA |
| 단계 계획과 전환 기록도 있음 | 완료·실행 중·대기 단계 목록 |
| 단계별 예상 소요도 있음 | 전체 ETA (전체 진행률은 단계별 `weight`도 필요) |

일반 문장 로그만으로 아직 실행하지 않은 단계나 총량을 정확히 알 수는 없습니다. 단계 계획을 한 번 선언하고 실제 진행을 전달하는 방식이 가장 확실합니다.

이미 실행 중인 작업은 **작업 → + 작업 연결**에서 프로세스와 상태 JSON·로그를 선택해 연결할 수 있습니다.
자체 JSON도 항목을 자동 선택하고 미리보기를 제공하며, 필요하면 **진행 정보 연결 항목**에서 바꿀 수 있습니다.
전체 수량을 모르면 비워 두세요. 자세한 흐름은 [사용 가이드](USAGE.md#ui에서-실행-중인-작업-연결)를 참고하세요.

## 1. Python 작업에서 상태 파일 기록

`python/taskstate.py`를 가져와 기존 작업의 시작·진행·끝에 붙입니다. 아래 예상 소요와 가중치는 예시이므로 실제 작업에 맞춥니다.

```python
from taskstate import TaskReporter

stages = [
    {"key": "prepare", "name": "입력 준비", "weight": 1, "expected_seconds": 10},
    {"key": "convert", "name": "파일 변환", "weight": 8, "expected_seconds": 80},
    {"key": "verify", "name": "결과 검증", "weight": 1, "expected_seconds": 10},
]
with TaskReporter("파일 처리", project="내 프로젝트", stages=stages) as t:
    for stage in stages:
        t.begin_stage(stage["key"], total=100, unit="files")
        for done in range(1, 101):
            # 여기에 실제 파일 준비 / 변환 / 검증 코드
            t.progress(done)
        t.end_stage()
    t.succeed()
```

처음부터 모든 단계가 보이고 아직 시작하지 않은 단계는 대기로 남습니다. `succeed()`를 호출해야 작업 전체 완료로 기록됩니다. 오류가 나면 context manager가 실패와 traceback을 기록합니다.

## 2. 언어에 상관없이 공통 진행 로그 출력

한 줄마다 다음 접두어와 JSON을 출력하고 flush합니다.

```text
TASK_PROGRESS_JSON {"stage":"convert","done":12,"total":100,"unit":"files","rate":2.5,"eta_seconds":35.2,"subject":"batch-A"}
```

| 필드 | 의미 |
|---|---|
| `done` | 필수. 처리한 수량, 0 이상인 유한한 숫자 |
| `total` | 선택. 전체 수량, 0보다 크고 `done` 이상 |
| `unit` | 선택. `files`, `epochs`, `items`, `checks` 등. 기본 `items` |
| `rate` | 선택. 초당 처리량, 0 이상 |
| `eta_seconds` | 선택. 현재 단계 남은 초, 0 이상 |
| `stage` | 선택. 단계 키. 래퍼에서는 키가 바뀌면 이전 단계를 완료하고 다음 단계를 시작 |
| `subject` | 선택. 지금 처리 중인 입력 이름 |

잘못된 JSON이나 음수·무한대·수량 범위를 벗어난 진행률은 무시합니다. 기존 `INFER_PROGRESS_JSON`과 자동 감지의 tqdm 읽기도 계속 지원합니다.

### 래퍼로 연결

명령의 stdout/stderr를 로그에 남기면서 공통 JSON을 읽습니다. Python 외 실행 파일도 `--` 뒤에 지정할 수 있습니다.

```bat
python python\taskstate.py wrap --name "파일 처리" --stage prepare:준비 --stage convert:변환 --stage verify:검증 -- python python\examples\example_generic_progress.py
```

저장소 루트에서 실행하면 약 9초 동안 단위가 `items → files → checks`로 바뀌는 예제를 볼 수 있습니다. `--stage`로 계획을 미리 지정해야 아직 실행하지 않은 단계까지 대기로 보입니다. 예측 소요는 `--stage convert:변환@80`처럼 초 단위로 지정할 수 있습니다.

래퍼는 단계 전환을 순차 실행으로 해석합니다. 병렬 단계·재시도·명시적인 실패와 검증 상태가 필요하면 위의 `TaskReporter` 또는 [상태 파일 계약](STATE_CONTRACT.md)을 사용하세요.

래퍼는 명령의 종료 코드 0을 완료 근거로 기록합니다. 실행되지 않은 계획 단계는 건너뜀으로 남기며, 종료 코드가 0이 아니면 실패와 마지막 로그를 기록합니다. 진행 JSON의 `done == total`만으로 작업 전체가 완료되지는 않습니다.

### 기존 실행 작업에 연결

자동 감지가 해당 로그 파일을 연결할 수 있으면 공통 JSON도 바로 읽습니다. 파일을 자동 연결하지 못하면 로그와 PID를 직접 지정합니다.

```bat
python python\taskstate.py follow-log --name "데이터 처리" --log C:\work\run.log --pid 12345 --stage prepare:준비 --stage convert:변환 --stage verify:검증
```

`follow-log`는 기본적으로 연결 이후의 새 로그부터 읽습니다. 앞부분부터 읽으려면 `--from-start`를 추가하세요. 이 방식은 프로세스가 끝나도 완료·실패를 임의로 추정하지 않습니다.

## 검증

```bat
python -m unittest discover -s demo -p test_progress.py
dotnet run --project src\TaskMonitor.Check -- --progress-selftest
```
