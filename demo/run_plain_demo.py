"""자동 감지 데모: 상태 기록을 하지 않는 스크립트 3개를 백그라운드로 실행.

    python demo/run_plain_demo.py [--out demo_out]

 - sample9 : tqdm 진행 표시를 로그 파일에 씀 → 진행률·ETA 표시, 종료 코드 0
 - sample10: 콘솔에만 출력(로그 없음) → '진행 정보 없음', CPU 사용만 표시
 - sample11: 로그에 쓰다가 종료 코드 2로 끝남 → '오류 종료 · 코드 2'
"""
import argparse
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ap = argparse.ArgumentParser()
ap.add_argument("--out", default=os.path.join(HERE, "..", "demo_out"))
a = ap.parse_args()
out = os.path.abspath(os.path.join(a.out, "plain"))
os.makedirs(out, exist_ok=True)
kw = {"creationflags": 0x08000000 | 0x00000200} if os.name == "nt" else {"start_new_session": True}
job = os.path.join(HERE, "plain_job.py")
jobs = [
    ["--input", "sample9", "--log", os.path.join(out, "sample9.log"), "--frames", "200"],
    ["--input", "sample10", "--frames", "200"],
    ["--input", "sample11", "--log", os.path.join(out, "sample11.log"), "--frames", "200", "--fail-at", "100"],
]
for args in jobs:
    p = subprocess.Popen([sys.executable, job, *args], cwd=out, stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **kw)
    print(f"pid={p.pid} {' '.join(args[:2])}")
print("모니터는 약 15초 뒤부터 이 프로세스들을 자동으로 표시합니다.")
