"""분야에 무관한 stdout 진행 로그 예제. 실제 작업 부분만 교체해서 사용하세요.

python python/taskstate.py wrap --name "파일 처리" --stage prepare:준비 --stage convert:변환 --stage verify:검증 -- python python/examples/example_generic_progress.py

미리 선언한 단계는 대기로 보이고, 로그의 stage가 바뀌면 이전 단계가 완료됩니다.
TASK_PROGRESS_JSON은 Python 외 프로그램에서도 같은 한 줄을 출력하면 됩니다.
"""
import json
import time


def report(stage, done, total, unit, subject):
    print("TASK_PROGRESS_JSON " + json.dumps(dict(
        stage=stage, done=done, total=total, unit=unit, subject=subject,
    ), ensure_ascii=False), flush=True)


def main():
    for stage, total, unit in [("prepare", 5, "items"), ("convert", 30, "files"), ("verify", 10, "checks")]:
        report(stage, 0, total, unit, "example-batch")
        for done in range(1, total + 1):
            time.sleep(0.2)  # 실제 준비 / 변환 / 검증 코드로 교체
            report(stage, done, total, unit, "example-batch")


if __name__ == "__main__":
    main()
