"""데모 작업 여러 개를 백그라운드로 동시에 실행합니다 (모니터 창과 무관하게 계속 돕니다).

    python demo/run_demo.py            # 약 2~3분짜리 데모
    python demo/run_demo.py --speed 3  # 3배 빠르게

재현하는 상황
  - 여러 작업 동시 표시, 여러 단계 순차 진행 (pipeline)
  - 선행 작업 대기 (dependent), 입력 파일 대기 (input_wait ← input_copy 가 .partial 로 쓰다 완성)
  - 시작 전 대기 (input_copy 가 처음 20초 동안 pending)
  - 정상 완료 / 실패 (fail) / 갱신 멈춤 후 재개 (stall) / 완료 기록 없이 종료 (crash)
  - 수정하지 않은 기존 스크립트 + INFER_PROGRESS_JSON 연결 (wrap + fake_infer)
"""
from __future__ import annotations

import argparse
import datetime as dt
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, ".."))


def spawn(args: list, log: str, env: dict) -> int:
    os.makedirs(os.path.dirname(log), exist_ok=True)
    kw = {}
    if os.name == "nt":
        kw["creationflags"] = 0x08000000 | 0x00000200  # CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP
    else:
        kw["start_new_session"] = True
    with open(log, "ab") as lf:
        p = subprocess.Popen([sys.executable, *args], stdout=lf, stderr=subprocess.STDOUT,
                             stdin=subprocess.DEVNULL, cwd=REPO, env=env, **kw)
    return p.pid


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--speed", type=float, default=1.0)
    ap.add_argument("--root", help="상태 폴더 루트 (TASKMONITOR_DIR). 모니터도 같은 값을 써야 함")
    ap.add_argument("--out", default=os.path.join(REPO, "demo_out"))
    ap.add_argument("--only", nargs="*", help="일부 시나리오만")
    a = ap.parse_args()

    env = dict(os.environ, PYTHONUNBUFFERED="1", PYTHONIOENCODING="utf-8")
    if a.root:
        env["TASKMONITOR_DIR"] = os.path.abspath(a.root)
    stamp = dt.datetime.now().strftime("%H%M%S")
    out = os.path.abspath(a.out)
    sp = str(a.speed)
    ids = {k: f"demo-{k.replace('_', '-')}-{stamp}" for k in
           ("pipeline", "dependent", "input_copy", "input_wait", "fail", "stall", "crash", "infer")}
    task = os.path.join(HERE, "demo_task.py")
    plan = {
        "pipeline": [task, "pipeline", "--id", ids["pipeline"]],
        "dependent": [task, "dependent", "--id", ids["dependent"], "--after", ids["pipeline"]],
        "input_copy": [task, "input_copy", "--id", ids["input_copy"], "--start-delay", "20"],
        "input_wait": [task, "input_wait", "--id", ids["input_wait"]],
        "fail": [task, "fail", "--id", ids["fail"], "--start-delay", "3"],
        "stall": [task, "stall", "--id", ids["stall"]],
        "crash": [task, "crash", "--id", ids["crash"], "--start-delay", "5"],
        "infer": [os.path.join(REPO, "python", "taskstate.py"), "wrap", "--id", ids["infer"],
                  "--name", "기존 추론 스크립트 (INFER_PROGRESS_JSON)", "--project", "3DMaker(데모)",
                  "--stage", "infer:추론", "--stage", "encode:인코딩",
                  "--log", os.path.join(out, ids["infer"], "run.log"), "--quiet",
                  "--", sys.executable, os.path.join(HERE, "fake_infer.py"), sp],
    }
    if os.path.exists(os.path.join(out, "inputs", "sample4.mp4")):
        os.remove(os.path.join(out, "inputs", "sample4.mp4"))  # 입력 대기 상황을 다시 재현하기 위해
    for name, args in plan.items():
        if a.only and name not in a.only:
            continue
        if name != "infer":
            args = args + ["--speed", sp, "--out", out]
        pid = spawn(args, os.path.join(out, "_launcher", f"{ids[name]}.stdout.log"), env)
        print(f"{name:11s} pid={pid:<7d} id={ids[name]}")
    root = env.get("TASKMONITOR_DIR") or os.path.join(os.environ.get("LOCALAPPDATA", "~"), "TaskMonitor")
    print(f"\n상태 폴더: {os.path.join(root, 'tasks')}\n결과 폴더: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
