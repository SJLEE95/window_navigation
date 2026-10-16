"""공통 로그 및 래퍼 통합 검증: python -m unittest discover -s demo -p test_progress.py"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "python"))
from taskstate import TaskReporter, parse_progress_line


class ProgressTests(unittest.TestCase):
    def test_generic_and_legacy(self):
        info = parse_progress_line('TASK_PROGRESS_JSON {"done":12,"total":100,"unit":"files","rate":2,"subject":"batch","stage":"convert"}')
        self.assertEqual((info["done"], info["unit"], info["subject"], info["rate"]), (12, "files", "batch", 2))
        self.assertEqual(parse_progress_line('TASK_PROGRESS_JSON {"done":0}')["unit"], "items")
        old = parse_progress_line('INFER_PROGRESS_JSON {"frame":3,"total":10,"fps":0.5,"sample":"s"}')
        self.assertEqual((old["done"], old["unit"], old["rate"], old["subject"]), (3, "frames", 0.5, "s"))

    def test_invalid_lines(self):
        for payload in ['{}', '[]', '{bad}', '{"done":true}', '{"done":-1}', '{"done":"12"}',
                        '{"done":1,"total":0}', '{"done":11,"total":10}', '{"done":1,"rate":-1}',
                        '{"done":NaN}', '{"done":1e999}', '{"done":1,"eta_seconds":-2}', '{"done":1,"total":"10"}']:
            with self.subTest(payload=payload):
                self.assertIsNone(parse_progress_line("TASK_PROGRESS_JSON " + payload))
        self.assertIsNone(parse_progress_line('ordinary log: converted 12 files'))

    def test_stage_plan_and_units(self):
        with tempfile.TemporaryDirectory() as root:
            t = TaskReporter("범용 작업", root=root, stages=[("convert", "변환"), ("verify", "검증"), ("save", "저장")])
            t.begin_stage("convert")
            self.assertTrue(t.feed_line('TASK_PROGRESS_JSON {"stage":"convert","done":12,"total":20,"unit":"files","rate":2,"subject":"batch"}'))
            t.flush()
            d = t.snapshot()
            self.assertEqual([s["state"] for s in d["stages"]], ["running", "pending", "pending"])
            self.assertEqual((d["stages"][0]["done"], d["stages"][0]["rate_unit"], d["subject"]), (12, "files/s", "batch"))
            t.feed_line('TASK_PROGRESS_JSON {"stage":"verify","done":1,"total":3,"unit":"checks"}')
            t.flush()
            d = t.snapshot()
            self.assertEqual([s["state"] for s in d["stages"]], ["succeeded", "running", "pending"])
            self.assertEqual(d["stages"][1]["unit"], "checks")
            self.assertEqual(d["state"], "running")  # 진행 로그만으로 작업 전체 완료를 추정하지 않음

    def test_wrapper_exit_and_unknown_stage(self):
        for rc in (0, 2):
            with self.subTest(rc=rc), tempfile.TemporaryDirectory() as root:
                child = 'print(\'TASK_PROGRESS_JSON {"stage":"convert","done":2,"total":2,"unit":"files"}\', flush=True); raise SystemExit(' + str(rc) + ')'
                result = subprocess.run([sys.executable, str(REPO / "python/taskstate.py"), "wrap", "--name", "generic", "--id", "generic-test",
                                         "--stage", "convert:변환", "--stage", "verify:검증", "--quiet", "--", sys.executable, "-c", child],
                                        env=dict(os.environ, TASKMONITOR_DIR=root), capture_output=True, text=True, encoding="utf-8")
                self.assertEqual(result.returncode, rc, result.stderr)
                d = json.loads((Path(root) / "tasks/generic-test.json").read_text(encoding="utf-8"))
                self.assertEqual(d["state"], "succeeded" if rc == 0 else "failed")
                self.assertEqual(d["stages"][0]["done"], 2)
                if rc == 0:
                    self.assertEqual(d["stages"][1]["state"], "skipped")
                    self.assertEqual(d["completion"]["basis"], "exit_code")


if __name__ == "__main__":
    unittest.main()
