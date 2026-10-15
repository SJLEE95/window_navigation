"""Python 쪽 자동 검증 (모니터 UI 없이).

    python demo/selftest.py [--speed 8]

1) 여러 프로세스가 같은 작업 파일을 동시에 갱신해도 JSON 이 깨지지 않는지
2) 데모 시나리오를 빠르게 돌려 상태 전이·최종 상태가 계약대로인지
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, ".."))
sys.path.insert(0, os.path.join(REPO, "python"))

FAILS: list = []


def check(cond: bool, msg: str) -> None:
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


WRITER = r"""
import sys, os
sys.path.insert(0, sys.argv[1])
from taskstate import TaskReporter
t = TaskReporter.attach(sys.argv[2])
n = int(sys.argv[3]); me = sys.argv[4]
for i in range(n):
    t.update(**{f"w_{me}": i + 1})
"""


def test_concurrency(root: str) -> None:
    print("[1] 동시 갱신")
    os.environ["TASKMONITOR_DIR"] = root
    from taskstate import TaskReporter, tasks_dir
    t = TaskReporter("동시성 테스트", task_id="concurrency-test", root=root)
    path = os.path.join(tasks_dir(root), "concurrency-test.json")
    stop = threading.Event()
    bad = []
    reads = [0]

    def reader():
        while not stop.is_set():
            try:
                with open(path, "rb") as f:
                    raw = f.read()
                json.loads(raw.decode("utf-8"))
                reads[0] += 1
            except (PermissionError, FileNotFoundError):
                pass
            except Exception as e:  # 깨진 JSON
                bad.append(repr(e))

    th = threading.Thread(target=reader, daemon=True)
    th.start()
    n, k = 150, 4
    env = dict(os.environ, TASKMONITOR_DIR=root)
    procs = [subprocess.Popen([sys.executable, "-c", WRITER, os.path.join(REPO, "python"), t.id, str(n), str(w)],
                              env=env) for w in range(k)]
    rcs = [p.wait() for p in procs]
    stop.set()
    th.join()
    doc = json.load(open(path, encoding="utf-8"))
    check(all(rc == 0 for rc in rcs), f"writer 프로세스 {k}개 정상 종료")
    check(not bad, f"읽기 {reads[0]}회 중 깨진 JSON 0회 (실제 {len(bad)})")
    check(all(doc.get(f"w_{w}") == n for w in range(k)), "모든 writer 의 마지막 값이 보존됨 (덮어쓰기 없음)")
    check(doc.get("seq") == n * k, f"seq = {n * k} (실제 {doc.get('seq')})")
    leftovers = [f for f in os.listdir(tasks_dir(root)) if f.endswith(".tmp")]
    check(not leftovers, "임시 파일 남지 않음")


def test_demo(root: str, speed: float) -> None:
    print(f"[2] 데모 시나리오 (speed={speed})")
    out = os.path.join(root, "demo_out")
    env = dict(os.environ, TASKMONITOR_DIR=root)
    r = subprocess.run([sys.executable, os.path.join(HERE, "run_demo.py"), "--speed", str(speed), "--root", root,
                        "--out", out], env=env, capture_output=True, text=True, encoding="utf-8")
    ids = {}
    for line in r.stdout.splitlines():
        if " id=" in line:
            ids[line.split()[0]] = line.rsplit(" id=", 1)[1].strip()
    check(len(ids) == 8, f"데모 작업 8개 실행 ({len(ids)})")
    from taskstate import read_task, parse_iso, _pid_alive
    seen = {k: set() for k in ids}
    max_gap = {k: 0.0 for k in ids}
    partial_seen = False
    deadline = time.time() + 400 / speed + 60
    while time.time() < deadline:
        docs = {k: read_task(v, root) for k, v in ids.items()}
        for k, d in docs.items():
            if not d:
                continue
            seen[k].add(d["state"])
            up = parse_iso(d["updated_at"])
            if d["state"] in ("running", "validating") and up:
                max_gap[k] = max(max_gap[k], time.time() - up.timestamp())
        if os.path.exists(os.path.join(out, "inputs", "sample4.mp4.partial")):
            partial_seen = True
        crash = docs.get("crash")
        crash_dead = bool(crash and crash.get("pid") and not _pid_alive(crash["pid"]))
        if all(d and d["state"] in ("succeeded", "failed", "cancelled") for k, d in docs.items() if k != "crash") \
                and crash_dead:
            break
        time.sleep(0.3)
    docs = {k: read_task(v, root) for k, v in ids.items()}
    for k in ("pipeline", "dependent", "input_copy", "input_wait", "stall", "infer"):
        check(docs[k]["state"] == "succeeded", f"{k}: 완료 ({docs[k]['state']})")
    check(docs["fail"]["state"] == "failed" and "ffmpeg" in (docs["fail"]["error"] or {}).get("message", ""),
          "fail: 실패 + 오류 메시지 기록")
    check(docs["fail"]["error"].get("stage") == "encode", "fail: 실패 단계 기록")
    check(docs["crash"]["state"] == "running" and not _pid_alive(docs["crash"]["pid"]),
          "crash: 프로세스는 없고 완료 기록도 없음 (모니터가 '종료 기록 없음' 표시해야 함)")
    check("waiting" in seen["dependent"], "dependent: 선행 작업 대기 상태를 거침")
    check("waiting" in seen["input_wait"], "input_wait: 입력 대기 상태를 거침")
    check("pending" in seen["input_copy"], "input_copy: 시작 전 대기(pending) 상태를 거침")
    check(partial_seen, "input_copy: .partial 파일 단계가 관측됨")
    check("validating" in seen["pipeline"], "pipeline: 검증 중 상태를 거침")
    stale = docs["stall"].get("stale_after_s", 120)
    check(max_gap["stall"] > stale, f"stall: 실행 중 갱신 공백 {max_gap['stall']:.1f}s > stale_after {stale}s")
    p = docs["pipeline"]
    check([s["state"] for s in p["stages"]] == ["succeeded"] * 5, "pipeline: 5단계 모두 완료")
    check(all(os.path.exists(r["path"]) for r in p["results"]), "pipeline: 결과 경로 존재")
    check(not os.path.exists(os.path.join(out, ids["pipeline"], "report.partial")), "pipeline: report.partial 정리됨")
    inf = docs["infer"]
    st = {s["key"]: s for s in inf["stages"]}
    check(st["infer"]["done"] == 300 and st["infer"]["total"] == 300, "infer: INFER_PROGRESS_JSON frame/total 반영")
    check(st["infer"].get("rate") is not None, "infer: fps → rate 반영")
    check(st["encode"]["state"] == "succeeded", "infer: stage 키로 다음 단계 전환")
    check(inf["completion"]["basis"] == "exit_code", "infer: 완료 근거 = 종료 코드")
    check(inf["subject"] == "sample8", "infer: sample → subject 반영")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--speed", type=float, default=8.0)
    ap.add_argument("--keep", action="store_true")
    a = ap.parse_args()
    root = tempfile.mkdtemp(prefix="taskmonitor_selftest_")
    try:
        test_concurrency(root)
        test_demo(root, a.speed)
    finally:
        if a.keep:
            print("상태 폴더 유지:", root)
        else:
            shutil.rmtree(root, ignore_errors=True)
    print("\n결과:", "모두 통과" if not FAILS else f"{len(FAILS)}개 실패")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
