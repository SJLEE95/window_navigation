"""상태 파일을 전혀 쓰지 않는 '기존 스크립트' 흉내 (자동 감지 데모용).

    python demo/plain_job.py --input sample9 [--log out.log] [--frames 300] [--fail-at 120]

--log 를 주면 tqdm 형식 진행 표시줄을 그 파일에 씁니다. 없으면 콘솔에만 씁니다.
"""
import argparse
import random
import sys
import time

ap = argparse.ArgumentParser()
ap.add_argument("--input", default="sample9")
ap.add_argument("--log")
ap.add_argument("--frames", type=int, default=300)
ap.add_argument("--per", type=float, default=0.2)
ap.add_argument("--fail-at", type=int)
a = ap.parse_args()

out = open(a.log, "a", encoding="utf-8") if a.log else sys.stdout
t0 = time.monotonic()
print(f"start {a.input}", file=out, flush=True)
for i in range(1, a.frames + 1):
    time.sleep(a.per * random.uniform(0.7, 1.3))
    sum(x * x for x in range(20000))  # CPU 사용
    el = time.monotonic() - t0
    rate = i / el
    rem = (a.frames - i) / rate
    if i % 3 == 0 or i == a.frames:
        bar = "#" * int(10 * i / a.frames)
        print(f"{a.input}: {int(100 * i / a.frames):3d}%|{bar:<10}| {i}/{a.frames} "
              f"[{int(el // 60):02d}:{int(el % 60):02d}<{int(rem // 60):02d}:{int(rem % 60):02d}, {rate:.2f}it/s]",
              file=out, flush=True)
    if a.fail_at and i == a.fail_at:
        print("Traceback (most recent call last):\nRuntimeError: decoder error (demo)", file=out, flush=True)
        sys.exit(2)
print("done", file=out, flush=True)
