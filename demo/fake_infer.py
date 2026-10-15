"""기존 추론 스크립트 흉내: 표준 출력에 INFER_PROGRESS_JSON 한 줄씩 출력.

taskstate 를 전혀 import 하지 않습니다. 이런 스크립트는 수정 없이
`python python/taskstate.py wrap --name ... -- python demo/fake_infer.py` 로 연결합니다.
"""
import json
import random
import sys
import time

speed = float(sys.argv[1]) if len(sys.argv) > 1 else 1.0
total = 300
t0 = time.monotonic()
print("loading model ... (demo)", flush=True)
time.sleep(2 / speed)
for frame in range(1, total + 1):
    time.sleep(random.uniform(0.1, 0.22) / speed)
    el = time.monotonic() - t0
    fps = frame / el
    if frame % 5 == 0 or frame == total:
        print("INFER_PROGRESS_JSON " + json.dumps(
            {"frame": frame, "total": total, "fps": round(fps, 2),
             "eta_seconds": round((total - frame) / fps), "sample": "sample8"}), flush=True)
    if frame % 50 == 0:
        print(f"frame {frame}: ok", flush=True)
# 두 번째 단계: JSON 에 stage 키를 넣으면 래퍼가 단계를 넘깁니다.
for frame in range(1, 61):
    time.sleep(0.15 / speed)
    if frame % 5 == 0:
        print("INFER_PROGRESS_JSON " + json.dumps({"stage": "encode", "frame": frame, "total": 60}), flush=True)
print("done", flush=True)
