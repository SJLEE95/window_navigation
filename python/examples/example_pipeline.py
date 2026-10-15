"""기존 파이프라인 스크립트에 TaskReporter 를 붙이는 예.

바뀌는 부분은 '# [TM]' 표시가 있는 줄뿐입니다. 처리 알고리즘(generate_and_measure 등)은 그대로입니다.
"""
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))  # 또는 taskstate.py 를 프로젝트에 복사
from taskstate import TaskReporter  # [TM]


def generate_and_measure(sample, on_frame):      # 기존 함수라고 가정
    total = 30
    for i in range(total):
        time.sleep(0.1)
        on_frame(i + 1, total)


def main():
    samples = ["sample2", "sample3"]
    stages = [(s, f"{s} 영상 생성·측정") for s in samples] + [  # [TM] 단계 목록
        ("verify", "결과 파일 검증"), ("report", "비교 이미지·보고서 저장"), ("sbs", "SBS 변환")]
    os.makedirs("out", exist_ok=True)
    with TaskReporter("3DMaker 영상 생성·측정", project="3DMaker", stages=stages,      # [TM]
                      log_path="out/run.log", cancel_supported=True) as t:              # [TM]
        for s in samples:
            t.begin_stage(s, total=30, unit="frames", subject=s)                       # [TM]
            generate_and_measure(s, lambda done, total: t.progress(done, total))       # [TM]
            if t.cancel_requested():                                                    # [TM]
                t.cancelled()                                                           # [TM]
                return
            t.end_stage()                                                               # [TM]
        t.begin_stage("verify", validating=True)                                        # [TM]
        time.sleep(1)
        t.end_stage()                                                                   # [TM]
        t.begin_stage("report")                                                         # [TM]
        with open("out/report.html", "w", encoding="utf-8") as f:
            f.write("<h1>report</h1>")
        t.add_result("보고서", "out/report.html")                                        # [TM]
        t.end_stage()                                                                   # [TM]
        t.skip_stage("sbs", "이번 실행에서는 생략")                                       # [TM]
        t.succeed()                                                                     # [TM] 명시적 완료 기록
        # succeed() 없이 with 블록을 벗어나면 '완료 기록 없이 종료'로 failed 기록됩니다.


if __name__ == "__main__":
    main()
