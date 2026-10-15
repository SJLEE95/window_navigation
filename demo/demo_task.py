"""TaskMonitor 데모 작업. 실제 영상 작업 없이 상태 변화만 재현합니다.

    python demo/demo_task.py <scenario> --id <task_id> [--speed 1.0] [--out demo_out]

시나리오: pipeline, dependent, input_copy, input_wait, fail, stall, crash
"""
from __future__ import annotations

import argparse
import os
import random
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "python"))
from taskstate import TaskReporter  # noqa: E402

SPEED = 1.0


def sleep(sec: float) -> None:
    time.sleep(max(0.0, sec / SPEED))


def atomic_dir_publish(final_dir: str, files: dict) -> None:
    """결과를 <dir>.partial 에 만들고 마지막에 이름을 바꿔 공개 (중간 상태를 완성본으로 오인하지 않게)."""
    partial = final_dir + ".partial"
    os.makedirs(partial, exist_ok=True)
    for name, text in files.items():
        with open(os.path.join(partial, name), "w", encoding="utf-8") as f:
            f.write(text)
        sleep(1.5)
    if os.path.exists(final_dir):
        import shutil
        shutil.rmtree(final_dir)
    os.replace(partial, final_dir)


def frames(t: TaskReporter, key: str, total: int, per: float, *, fail_at: int | None = None,
           stall_at: int | None = None, stall_sec: float = 0, crash_at: int | None = None,
           jitter: float = 0.35) -> bool:
    """단계 처리 루프. 중단 요청이 오면 False."""
    t0 = time.monotonic()
    for i in range(total):
        sleep(per * random.uniform(1 - jitter, 1 + jitter))
        done = i + 1
        el = max(1e-6, time.monotonic() - t0)
        t.progress(done, total, stage=key, rate=round(done / el, 2), rate_unit="frames/s")
        if done % 20 == 0:
            t.log(f"[{key}] {done}/{total}")
        if fail_at is not None and done == fail_at:
            t.log(f"[{key}] ERROR ffmpeg: Invalid data found when processing input (demo)")
            raise RuntimeError("ffmpeg: Invalid data found when processing input (데모 실패)")
        if crash_at is not None and done == crash_at:
            t.flush()
            t.log(f"[{key}] 프로세스가 완료 기록 없이 갑자기 종료됩니다 (데모)")
            os._exit(3)
        if stall_at is not None and done == stall_at:
            t.flush()
            t.log(f"[{key}] 응답 없음 상태 재현: {stall_sec:.0f}초 동안 상태 갱신 중단")
            sleep(stall_sec)
            t.log(f"[{key}] 다시 진행")
        if t.cancel_requested():
            return False
    t.flush()
    return True


def scenario_pipeline(a) -> None:
    out = os.path.join(a.out, a.id)
    with TaskReporter("3DMaker 데모 파이프라인", task_id=a.id, project="3DMaker(데모)", workdir=out,
                      subject="sample2", log_path=os.path.join(out, "run.log"), cancel_supported=True,
                      stale_after_s=30,
                      stages=[{"key": "sample2", "name": "sample2 영상 생성·측정", "expected_seconds": 30 / SPEED, "weight": 3},
                              {"key": "sample3", "name": "sample3 영상 생성·측정", "expected_seconds": 30 / SPEED, "weight": 3},
                              {"key": "verify", "name": "결과 파일 검증", "expected_seconds": 9 / SPEED, "weight": 1},
                              {"key": "report", "name": "비교 이미지·보고서 저장", "expected_seconds": 8 / SPEED, "weight": 1},
                              {"key": "sbs", "name": "SBS 변환", "expected_seconds": 15 / SPEED, "weight": 2}]) as t:
        os.makedirs(out, exist_ok=True)
        t.log("파이프라인 시작")
        for key, sample in (("sample2", "sample2"), ("sample3", "sample3")):
            t.begin_stage(key, total=120, unit="frames", subject=sample, message=f"{sample} 프레임 생성 중")
            if not frames(t, key, 120, 0.25):
                t.cancelled()
                return
            with open(os.path.join(out, f"{sample}.mp4"), "w") as f:
                f.write("fake video\n")
            t.end_stage(key)
        t.begin_stage("verify", total=3, unit="files", validating=True, subject="sample2, sample3",
                      message="결과 파일 검증 중")
        for i in range(3):
            sleep(3)
            t.progress(i + 1, 3, stage="verify", force=True)
        t.end_stage("verify")
        t.begin_stage("report", total=None, message="보고서를 report.partial 에 저장 중")
        atomic_dir_publish(os.path.join(out, "report"), {
            "compare.png.txt": "비교 이미지 자리 (데모)\n",
            "report.html": "<html><meta charset='utf-8'><body><h1>데모 보고서</h1><p>sample2, sample3 측정 완료</p></body></html>\n",
        })
        t.end_stage("report")
        t.add_result("보고서", os.path.join(out, "report", "report.html"), "file")
        t.add_result("결과 폴더", out, "dir")
        t.begin_stage("sbs", total=60, unit="frames", subject="sample2+sample3", message="SBS 변환 중")
        if not frames(t, "sbs", 60, 0.25):
            t.cancelled()
            return
        with open(os.path.join(out, "sbs.mp4"), "w") as f:
            f.write("fake sbs\n")
        t.end_stage("sbs")
        t.add_result("SBS 영상", os.path.join(out, "sbs.mp4"), "file")
        t.log("파이프라인 완료")
        t.succeed("모든 단계 완료")


def scenario_dependent(a) -> None:
    out = os.path.join(a.out, a.id)
    with TaskReporter("MpdMaker 데모: 파이프라인 결과 분석", task_id=a.id, project="MpdMaker(데모)", workdir=out,
                      log_path=os.path.join(out, "run.log"), depends_on=[a.after],
                      stages=[{"key": "analyze", "name": "결과 분석"},
                              {"key": "verify", "name": "분석 결과 검증"}]) as t:
        os.makedirs(out, exist_ok=True)
        t.log(f"선행 작업 {a.after} 대기")
        t.wait_for_tasks([a.after], poll=2)
        t.log("선행 작업 완료 확인")
        t.begin_stage("analyze", total=40, unit="items", subject=a.after)
        frames(t, "analyze", 40, 0.3)
        t.end_stage("analyze")
        t.begin_stage("verify", validating=True, message="분석 결과 검증 중")
        sleep(4)
        with open(os.path.join(out, "analysis.csv"), "w", encoding="utf-8") as f:
            f.write("sample,psnr\nsample2,38.1\nsample3,37.4\n")
        t.end_stage("verify")
        t.add_result("분석 CSV", os.path.join(out, "analysis.csv"), "file")
        t.succeed()


def scenario_input_copy(a) -> None:
    out = os.path.join(a.out, "inputs")
    with TaskReporter("sample4 입력 준비 (복사)", task_id=a.id, project="데모", workdir=out,
                      log_path=os.path.join(a.out, a.id + ".log"),
                      stages=[{"key": "copy", "name": "sample4.mp4 복사"}]) as t:
        os.makedirs(out, exist_ok=True)
        if a.start_delay:  # 등록만 된 '대기' 상태를 보여주기 위해 시작 전 잠시 멈춤
            t.message(f"{a.start_delay:.0f}초 후 시작 예정")
            sleep(a.start_delay)
        t.begin_stage("copy", total=50, unit="MB", subject="sample4.mp4")
        part = os.path.join(out, "sample4.mp4.partial")
        with open(part, "w") as f:
            for i in range(50):
                f.write("x" * 1024)
                f.flush()
                sleep(0.8)
                t.progress(i + 1, 50, stage="copy")
        os.replace(part, os.path.join(out, "sample4.mp4"))
        t.end_stage("copy")
        t.add_result("입력 파일", os.path.join(out, "sample4.mp4"), "file")
        t.succeed()


def scenario_input_wait(a) -> None:
    out = os.path.join(a.out, a.id)
    inp = os.path.join(a.out, "inputs", "sample4.mp4")
    with TaskReporter("sample4 인코딩 (입력 대기)", task_id=a.id, project="3DMaker(데모)", workdir=out,
                      log_path=os.path.join(out, "run.log"), subject="sample4",
                      stages=[{"key": "encode", "name": "sample4 인코딩"}]) as t:
        os.makedirs(out, exist_ok=True)
        t.log(f"입력 대기: {inp} (.partial 은 완성본으로 보지 않음)")
        t.wait_for_inputs([inp], poll=2)
        t.begin_stage("encode", total=80, unit="frames")
        frames(t, "encode", 80, 0.2)
        with open(os.path.join(out, "sample4_enc.mp4"), "w") as f:
            f.write("fake\n")
        t.end_stage("encode")
        t.add_result("인코딩 결과", os.path.join(out, "sample4_enc.mp4"), "file")
        t.succeed()


def scenario_fail(a) -> None:
    out = os.path.join(a.out, a.id)
    with TaskReporter("인코딩 테스트 (실패 데모)", task_id=a.id, project="3DMaker(데모)", workdir=out,
                      subject="sample5", log_path=os.path.join(out, "run.log"),
                      stages=[{"key": "encode", "name": "sample5 인코딩"},
                              {"key": "verify", "name": "결과 검증"}]) as t:
        os.makedirs(out, exist_ok=True)
        t.begin_stage("encode", total=200, unit="frames")
        frames(t, "encode", 200, 0.2, fail_at=62)  # 예외 → with 블록이 failed 로 기록


def scenario_stall(a) -> None:
    out = os.path.join(a.out, a.id)
    with TaskReporter("측정 작업 (갱신 멈춤 데모)", task_id=a.id, project="MpdMaker(데모)", workdir=out,
                      subject="sample6", log_path=os.path.join(out, "run.log"), stale_after_s=max(5, int(20 / SPEED)),
                      stages=[{"key": "measure", "name": "sample6 측정"}]) as t:
        os.makedirs(out, exist_ok=True)
        t.begin_stage("measure", total=100, unit="items")
        frames(t, "measure", 100, 0.3, stall_at=30, stall_sec=75)
        with open(os.path.join(out, "measure.csv"), "w") as f:
            f.write("ok\n")
        t.end_stage("measure")
        t.add_result("측정 결과", os.path.join(out, "measure.csv"), "file")
        t.succeed()


def scenario_crash(a) -> None:
    out = os.path.join(a.out, a.id)
    t = TaskReporter("영상 생성 (비정상 종료 데모)", task_id=a.id, project="3DMaker(데모)", workdir=out,
                     subject="sample7", log_path=os.path.join(out, "run.log"),
                     stages=[{"key": "gen", "name": "sample7 영상 생성"}, {"key": "verify", "name": "검증"}])
    os.makedirs(out, exist_ok=True)
    os.makedirs(os.path.join(out, "frames.partial"), exist_ok=True)
    t.add_result("프레임 폴더(미완성)", os.path.join(out, "frames.partial"), "dir")
    t.begin_stage("gen", total=150, unit="frames")
    frames(t, "gen", 150, 0.25, crash_at=60)


SCENARIOS = {
    "pipeline": scenario_pipeline, "dependent": scenario_dependent, "input_copy": scenario_input_copy,
    "input_wait": scenario_input_wait, "fail": scenario_fail, "stall": scenario_stall, "crash": scenario_crash,
}


def main() -> int:
    global SPEED
    ap = argparse.ArgumentParser()
    ap.add_argument("scenario", choices=sorted(SCENARIOS))
    ap.add_argument("--id", required=True)
    ap.add_argument("--after", help="dependent: 선행 작업 ID")
    ap.add_argument("--speed", type=float, default=1.0)
    ap.add_argument("--out", default=os.path.join(HERE, "..", "demo_out"))
    ap.add_argument("--start-delay", type=float, default=0)
    a = ap.parse_args()
    SPEED = a.speed
    a.out = os.path.abspath(a.out)
    if a.start_delay and a.scenario != "input_copy":
        sleep(a.start_delay)
    SCENARIOS[a.scenario](a)
    return 0


if __name__ == "__main__":
    sys.exit(main())
