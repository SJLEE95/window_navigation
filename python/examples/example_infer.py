"""INFER_PROGRESS_JSON 을 출력하는 기존 스크립트를 연결하는 세 가지 방법.

방법 A — 스크립트 수정 없음 (권장 시작점)
    python python/taskstate.py wrap --name "sample2 추론" --project 3DMaker ^
        --stage infer:추론 --result "결과=C:\\work\\out" -- python infer.py --input sample2

    · 표준 출력/오류를 로그 파일에 저장하고, INFER_PROGRESS_JSON 줄을 진행률로 바꿉니다.
    · 종료 코드 0 이면 '완료 (근거: 종료 코드)', 아니면 '실패' + 로그 마지막 줄을 기록합니다.
    · 중단은 지원하지 않는 것으로 표시됩니다(래퍼는 프로세스를 죽이지 않음).

방법 B — 이미 다른 프로그램이 실행한 작업 (로그 파일만 있음)
    python python/taskstate.py follow-log --name "sample2 추론" --log C:\\work\\logs\\infer.log --pid 12345
    · 로그를 따라 읽기만 합니다. 프로세스가 끝나도 완료/실패를 추정하지 않으므로
      모니터는 '종료 기록 없음'으로 표시합니다.

방법 C — 스크립트에 두 줄 추가 (아래 코드)
    print 하던 줄을 그대로 두고 reporter.feed_line() 에도 넘깁니다.
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from taskstate import TaskReporter


def run_inference(report):
    total = 40
    t0 = time.monotonic()
    for frame in range(1, total + 1):
        time.sleep(0.05)
        fps = frame / (time.monotonic() - t0)
        line = "INFER_PROGRESS_JSON " + json.dumps(
            {"frame": frame, "total": total, "fps": round(fps, 2), "eta_seconds": round((total - frame) / fps)})
        print(line, flush=True)  # 기존 출력 그대로
        report(line)             # 추가된 한 줄


if __name__ == "__main__":
    # wrap 아래에서 실행되면 래퍼가 만든 작업에 붙고, 단독 실행이면 새 작업을 만듭니다.
    if os.environ.get("TASKMONITOR_TASK_ID"):
        t = TaskReporter.attach()
        run_inference(t.feed_line)
        t.flush()  # 완료 기록은 래퍼가 종료 코드로 남김
    else:
        with TaskReporter("sample2 추론 (예제)", project="3DMaker", stages=[("infer", "추론")]) as t:
            t.begin_stage("infer", unit="frames")
            run_inference(t.feed_line)
            t.end_stage()
            t.succeed()
