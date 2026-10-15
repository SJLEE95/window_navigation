"""TaskMonitor 작업 상태 helper (taskmonitor/1 계약).

표준 라이브러리만 사용합니다. 이 파일 하나를 기존 프로젝트에 복사하거나
PYTHONPATH에 이 폴더를 추가해서 쓰면 됩니다.

    from taskstate import TaskReporter

    with TaskReporter(name="3DMaker 영상 생성", project="3DMaker",
                      stages=[("sample2", "sample2 영상 생성·측정"),
                              ("verify", "결과 파일 검증")],
                      log_path="logs/run.log", cancel_supported=True) as t:
        t.begin_stage("sample2", total=599, unit="frames")
        for i in range(599):
            ...
            t.progress(i + 1)
            if t.cancel_requested():
                t.cancelled("사용자 요청")
                return
        t.end_stage()
        t.begin_stage("verify", validating=True)
        ...
        t.add_result("보고서", "out/report.html")
        t.succeed()

명령줄 사용법은 ``python taskstate.py --help`` 참고.
"""
from __future__ import annotations

import argparse
import contextlib
import datetime as _dt
import json
import os
import random
import re
import socket
import subprocess
import sys
import threading
import time
import traceback
from typing import Any, Callable, Iterable, Optional, Sequence

SCHEMA = "taskmonitor/1"
TERMINAL_STATES = ("succeeded", "failed", "cancelled")
TASK_STATES = ("pending", "waiting", "running", "validating") + TERMINAL_STATES
STAGE_STATES = ("pending", "running", "validating", "succeeded", "failed", "skipped", "cancelled")
INFER_RE = re.compile(r"INFER_PROGRESS_JSON\s+(\{.*\})\s*$")
_ID_RE = re.compile(r"[^A-Za-z0-9._-]+")


# ---------------------------------------------------------------- paths / time
def root_dir() -> str:
    env = os.environ.get("TASKMONITOR_DIR")
    if env:
        return os.path.abspath(env)
    base = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), ".local", "share")
    return os.path.join(base, "TaskMonitor")


def tasks_dir(root: Optional[str] = None) -> str:
    return os.path.join(root or root_dir(), "tasks")


def control_dir(root: Optional[str] = None) -> str:
    return os.path.join(root or root_dir(), "control")


def now_iso() -> str:
    return _dt.datetime.now().astimezone().isoformat(timespec="seconds")


def parse_iso(s: Optional[str]) -> Optional[_dt.datetime]:
    if not s:
        return None
    try:
        return _dt.datetime.fromisoformat(s)
    except ValueError:
        return None


def make_task_id(prefix: str) -> str:
    slug = _ID_RE.sub("-", prefix).strip("-").lower()[:32] or "task"
    stamp = _dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    return f"{slug}-{stamp}-{random.randrange(16**4):04x}"


def _check_id(task_id: str) -> str:
    if not task_id or _ID_RE.search(task_id):
        raise ValueError(f"작업 ID에는 영문/숫자/._- 만 쓸 수 있습니다: {task_id!r}")
    return task_id


# ---------------------------------------------------------------- file locking
class _FileLock:
    """같은 작업 파일을 여러 프로세스가 갱신할 때 쓰는 OS 파일 잠금."""

    def __init__(self, path: str, timeout: float = 10.0):
        self.path = path
        self.timeout = timeout
        self._fh = None

    def __enter__(self):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        self._fh = open(self.path, "a+b")
        deadline = time.monotonic() + self.timeout
        while True:
            try:
                if os.name == "nt":
                    import msvcrt
                    self._fh.seek(0)
                    msvcrt.locking(self._fh.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(self._fh.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                return self
            except OSError:
                if time.monotonic() > deadline:
                    self._fh.close()
                    raise TimeoutError(f"잠금 대기 시간 초과: {self.path}")
                time.sleep(0.02)

    def __exit__(self, *exc):
        try:
            if os.name == "nt":
                import msvcrt
                self._fh.seek(0)
                msvcrt.locking(self._fh.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._fh.fileno(), fcntl.LOCK_UN)
        finally:
            self._fh.close()
        return False


def atomic_write_json(path: str, data: Any) -> None:
    """임시 파일에 전부 쓰고 fsync 후 os.replace로 교체 (읽는 쪽은 반쯤 쓴 파일을 보지 않음)."""
    d = os.path.dirname(path)
    os.makedirs(d, exist_ok=True)
    tmp = f"{path}.{os.getpid()}.{random.randrange(16**6):06x}.tmp"
    payload = json.dumps(data, ensure_ascii=False, indent=1)
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write(payload)
        f.flush()
        os.fsync(f.fileno())
    # Windows: 다른 프로세스(모니터)가 읽는 순간에는 PermissionError가 날 수 있어 재시도
    for attempt in range(60):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            time.sleep(0.025 + 0.005 * attempt)
    try:
        os.remove(tmp)
    finally:
        raise PermissionError(f"상태 파일을 교체하지 못했습니다: {path}")


def read_json(path: str) -> Optional[dict]:
    for _ in range(20):
        try:
            with open(path, "r", encoding="utf-8") as f:
                return json.load(f)
        except FileNotFoundError:
            return None
        except (PermissionError, json.JSONDecodeError):
            time.sleep(0.02)
    return None


def read_task(task_id: str, root: Optional[str] = None) -> Optional[dict]:
    return read_json(os.path.join(tasks_dir(root), f"{task_id}.json"))


# ---------------------------------------------------------------- reporter
class DependencyFailed(RuntimeError):
    pass


class TaskReporter:
    """작업 하나의 상태 파일을 기록합니다.

    - 쓰기는 항상 '잠금 → 현재 파일 읽기 → 수정 → 원자적 교체' 순서라서,
      같은 작업을 래퍼와 본 스크립트가 동시에 갱신해도 서로 덮어쓰지 않습니다.
    - progress()는 기본 1초에 한 번만 파일에 씁니다(min_interval).
    - 백그라운드 heartbeat 스레드는 기본으로 켜지 않습니다. 본 작업이 멈추면
      갱신도 멈춰야 모니터가 "상태 갱신 없음"을 정확히 보여줄 수 있기 때문입니다.
    """

    def __init__(
        self,
        name: Optional[str] = None,
        *,
        task_id: Optional[str] = None,
        project: Optional[str] = None,
        workdir: Optional[str] = None,
        subject: Optional[str] = None,
        stages: Optional[Sequence] = None,
        log_path: Optional[str] = None,
        depends_on: Optional[Iterable[str]] = None,
        stale_after_s: int = 120,
        cancel_supported: bool = False,
        pid: Optional[int] = None,
        root: Optional[str] = None,
        min_interval: float = 1.0,
        attach: bool = False,
        state: str = "pending",
    ):
        self.root = os.path.abspath(root) if root else root_dir()
        if task_id is None:
            task_id = os.environ.get("TASKMONITOR_TASK_ID") if attach else None
        if task_id is None:
            if attach:
                raise ValueError("attach=True 인데 task_id나 TASKMONITOR_TASK_ID가 없습니다")
            task_id = make_task_id(project or name or "task")
        self.id = _check_id(task_id)
        self.path = os.path.join(tasks_dir(self.root), f"{self.id}.json")
        self._lock_path = self.path + ".lock"
        self.min_interval = min_interval
        self._last_write = 0.0
        self._pending_progress: dict = {}
        self._mutex = threading.RLock()
        self._closed = False
        self._log_fh = None

        if attach:
            if read_task(self.id, self.root) is None:
                raise FileNotFoundError(f"연결할 작업 파일이 없습니다: {self.path}")
            return

        ts = now_iso()
        doc = {
            "schema": SCHEMA,
            "id": self.id,
            "name": name or self.id,
            "project": project,
            "workdir": os.path.abspath(workdir) if workdir else os.getcwd(),
            "subject": subject,
            "state": state,
            "message": None,
            "pid": pid if pid is not None else os.getpid(),
            "host": socket.gethostname(),
            "created_at": ts,
            "started_at": None,
            "updated_at": ts,
            "finished_at": None,
            "stale_after_s": int(stale_after_s),
            "current_stage": None,
            "stages": [self._stage_dict(s) for s in (stages or [])],
            "depends_on": list(depends_on or []),
            "waiting_for": None,
            "log_path": os.path.abspath(log_path) if log_path else None,
            "results": [],
            "error": None,
            "cancel": {"supported": bool(cancel_supported), "method": "file" if cancel_supported else None},
            "completion": None,
            "seq": 0,
        }
        with _FileLock(self._lock_path):
            atomic_write_json(self.path, doc)
        self._last_write = time.monotonic()

    # -- construction helpers
    @classmethod
    def attach(cls, task_id: Optional[str] = None, root: Optional[str] = None, **kw) -> "TaskReporter":
        """이미 등록된 작업(예: 래퍼가 만든 작업)에 연결해서 함께 갱신합니다."""
        return cls(task_id=task_id, root=root, attach=True, **kw)

    @staticmethod
    def _stage_dict(s) -> dict:
        if isinstance(s, dict):
            d = dict(s)
        elif isinstance(s, (tuple, list)):
            d = {"key": s[0], "name": s[1] if len(s) > 1 else s[0]}
            if len(s) > 2 and s[2] is not None:
                d["expected_seconds"] = s[2]
        else:
            d = {"key": str(s), "name": str(s)}
        d.setdefault("name", d["key"])
        d.setdefault("state", "pending")
        return d

    # -- core write
    def _mutate(self, fn: Callable[[dict], None], force: bool = True) -> None:
        with self._mutex:
            with _FileLock(self._lock_path):
                doc = read_json(self.path)
                if doc is None:
                    raise FileNotFoundError(f"작업 파일이 없습니다: {self.path}")
                if self._pending_progress:
                    self._apply_progress(doc, self._pending_progress)
                    self._pending_progress = {}
                fn(doc)
                doc["updated_at"] = now_iso()
                doc["seq"] = int(doc.get("seq") or 0) + 1
                atomic_write_json(self.path, doc)
            self._last_write = time.monotonic()

    def snapshot(self) -> dict:
        return read_json(self.path) or {}

    def _current_stage_key(self) -> Optional[str]:
        if not hasattr(self, "_cur_stage_cache"):
            self._cur_stage_cache = self.snapshot().get("current_stage")
        return self._cur_stage_cache

    @staticmethod
    def _find_stage(doc: dict, key: Optional[str]) -> Optional[dict]:
        key = key or doc.get("current_stage")
        for st in doc.get("stages") or []:
            if st.get("key") == key:
                return st
        return None

    @staticmethod
    def _ensure_started(doc: dict) -> None:
        if not doc.get("started_at"):
            doc["started_at"] = now_iso()

    # -- general
    def update(self, **fields) -> None:
        """임의 최상위 필드 갱신 (message, subject 등)."""
        def fn(doc):
            doc.update(fields)
        self._mutate(fn)

    def message(self, text: str) -> None:
        self.update(message=text)

    def set_subject(self, subject: str) -> None:
        self.update(subject=subject)

    def heartbeat(self, message: Optional[str] = None) -> None:
        """처리량 변화는 없지만 작업이 살아서 일하고 있음을 알릴 때 호출."""
        def fn(doc):
            if message is not None:
                doc["message"] = message
        self._mutate(fn)

    def start(self, message: Optional[str] = None) -> None:
        def fn(doc):
            self._ensure_started(doc)
            doc["state"] = "running"
            doc["waiting_for"] = None
            if message:
                doc["message"] = message
        self._mutate(fn)

    # -- stages
    def add_stage(self, key: str, name: Optional[str] = None, **extra) -> None:
        def fn(doc):
            if self._find_stage(doc, key) is None:
                doc.setdefault("stages", []).append(self._stage_dict({"key": key, "name": name or key, **extra}))
        self._mutate(fn)

    def begin_stage(self, key: str, *, name: Optional[str] = None, total: Optional[float] = None,
                    unit: Optional[str] = None, expected_seconds: Optional[float] = None,
                    validating: bool = False, subject: Optional[str] = None,
                    message: Optional[str] = None) -> None:
        def fn(doc):
            st = self._find_stage(doc, key)
            if st is None:
                st = self._stage_dict({"key": key, "name": name or key})
                doc.setdefault("stages", []).append(st)
            if name:
                st["name"] = name
            ts = now_iso()
            st["state"] = "validating" if validating else "running"
            st["started_at"] = ts
            st["finished_at"] = None
            st["done"] = 0 if total is not None else st.get("done")
            if total is not None:
                st["total"] = total
            if unit is not None:
                st["unit"] = unit
            if expected_seconds is not None:
                st["expected_seconds"] = expected_seconds
            self._ensure_started(doc)
            doc["current_stage"] = key
            doc["state"] = "validating" if validating else "running"
            doc["waiting_for"] = None
            if subject is not None:
                doc["subject"] = subject
            doc["message"] = message if message is not None else st.get("name")
        self._mutate(fn)
        self._cur_stage_cache = key

    @staticmethod
    def _apply_progress(doc: dict, p: dict) -> None:
        st = TaskReporter._find_stage(doc, p.get("stage"))
        if st is None:
            return
        old_done = st.get("done")
        for k in ("done", "total", "unit", "rate", "rate_unit", "eta_seconds"):
            if p.get(k) is not None:
                st[k] = p[k]
        if p.get("done") is not None and p["done"] != old_done:
            st["progress_at"] = p.get("ts") or now_iso()
        if p.get("message") is not None:
            doc["message"] = p["message"]
        if p.get("subject") is not None:
            doc["subject"] = p["subject"]

    def progress(self, done: float, total: Optional[float] = None, *, stage: Optional[str] = None,
                 unit: Optional[str] = None, rate: Optional[float] = None, rate_unit: Optional[str] = None,
                 eta_seconds: Optional[float] = None, message: Optional[str] = None,
                 subject: Optional[str] = None, force: bool = False) -> None:
        """현재 단계(또는 stage)의 처리량 갱신. min_interval보다 자주 부르면 모아서 씁니다."""
        with self._mutex:
            p = self._pending_progress
            if p.get("stage") not in (None, stage):
                # 다른 단계의 대기 중인 갱신은 먼저 기록
                self._mutate(lambda d: None)
                p = self._pending_progress
            p.update({k: v for k, v in dict(stage=stage, done=done, total=total, unit=unit, rate=rate,
                                             rate_unit=rate_unit, eta_seconds=eta_seconds,
                                             message=message, subject=subject).items() if v is not None})
            p["ts"] = now_iso()
            if force or time.monotonic() - self._last_write >= self.min_interval:
                self._mutate(lambda d: None)

    def flush(self) -> None:
        with self._mutex:
            if self._pending_progress:
                self._mutate(lambda d: None)

    def end_stage(self, key: Optional[str] = None, state: str = "succeeded") -> None:
        if state not in STAGE_STATES:
            raise ValueError(state)

        def fn(doc):
            st = self._find_stage(doc, key)
            if st is None:
                return
            st["state"] = state
            st["finished_at"] = now_iso()
            st.pop("eta_seconds", None)
            if state == "succeeded" and st.get("total") is not None:
                st["done"] = st["total"]
        self._mutate(fn)

    def skip_stage(self, key: str, reason: Optional[str] = None) -> None:
        def fn(doc):
            st = self._find_stage(doc, key)
            if st is not None:
                st["state"] = "skipped"
                if reason:
                    st["note"] = reason
        self._mutate(fn)

    # -- waiting
    def wait_for(self, tasks: Iterable[str] = (), inputs: Iterable[str] = (), reason: Optional[str] = None) -> None:
        def fn(doc):
            doc["state"] = "waiting"
            doc["waiting_for"] = {"tasks": list(tasks), "inputs": [os.path.abspath(i) for i in inputs],
                                  "reason": reason}
            doc["message"] = reason or "선행 작업·입력 대기"
        self._mutate(fn)

    def wait_for_tasks(self, task_ids: Sequence[str], poll: float = 5.0, timeout: Optional[float] = None,
                       reason: Optional[str] = None) -> None:
        """선행 작업이 모두 succeeded가 될 때까지 대기. 하나라도 failed/cancelled면 DependencyFailed."""
        ids = list(task_ids)
        self.update(depends_on=sorted(set((self.snapshot().get("depends_on") or []) + ids)))
        self.wait_for(tasks=ids, reason=reason or "선행 작업 완료 대기: " + ", ".join(ids))
        t0 = time.monotonic()
        while True:
            pending = []
            for tid in ids:
                d = read_task(tid, self.root)
                st = (d or {}).get("state")
                if st in ("failed", "cancelled"):
                    raise DependencyFailed(f"선행 작업 {tid} 상태: {st}")
                if st != "succeeded":
                    pending.append(tid)
            if not pending:
                return
            if timeout is not None and time.monotonic() - t0 > timeout:
                raise TimeoutError("선행 작업 대기 시간 초과: " + ", ".join(pending))
            if self.cancel_requested():
                raise KeyboardInterrupt("중단 요청")
            self.heartbeat()
            time.sleep(poll)

    def wait_for_inputs(self, paths: Sequence[str], poll: float = 5.0, timeout: Optional[float] = None,
                        reason: Optional[str] = None) -> None:
        """입력 파일·폴더가 생길 때까지 대기 (.partial/.tmp 는 완성본으로 보지 않음)."""
        paths = [os.path.abspath(p) for p in paths]
        self.wait_for(inputs=paths, reason=reason or "입력 대기: " + ", ".join(os.path.basename(p) for p in paths))
        t0 = time.monotonic()
        while not all(is_complete_path(p) for p in paths):
            if timeout is not None and time.monotonic() - t0 > timeout:
                raise TimeoutError("입력 대기 시간 초과")
            if self.cancel_requested():
                raise KeyboardInterrupt("중단 요청")
            self.heartbeat()
            time.sleep(poll)

    # -- results / logs
    def add_result(self, label: str, path: str, kind: Optional[str] = None) -> None:
        path = os.path.abspath(path)
        kind = kind or ("dir" if os.path.isdir(path) else "file")

        def fn(doc):
            res = [r for r in (doc.get("results") or []) if r.get("path") != path]
            res.append({"label": label, "path": path, "kind": kind})
            doc["results"] = res
        self._mutate(fn)

    def set_log_path(self, path: str) -> None:
        self.update(log_path=os.path.abspath(path))

    def log(self, text: str) -> None:
        """log_path 에 한 줄 추가 (log_path가 있을 때)."""
        lp = self.snapshot().get("log_path")
        if not lp:
            return
        os.makedirs(os.path.dirname(lp) or ".", exist_ok=True)
        with open(lp, "a", encoding="utf-8") as f:
            f.write(f"{_dt.datetime.now():%Y-%m-%d %H:%M:%S} {text}\n")

    # -- cancel
    def cancel_requested(self) -> bool:
        return os.path.exists(os.path.join(control_dir(self.root), f"{self.id}.cancel"))

    # -- terminal
    def _finish(self, state: str, fn_extra: Optional[Callable[[dict], None]] = None) -> None:
        def fn(doc):
            ts = now_iso()
            doc["state"] = state
            doc["finished_at"] = ts
            doc["waiting_for"] = None
            for st in doc.get("stages") or []:
                if st.get("state") in ("running", "validating"):
                    st["state"] = {"succeeded": "succeeded", "failed": "failed", "cancelled": "cancelled"}[state]
                    st["finished_at"] = ts
                    st.pop("eta_seconds", None)
            if fn_extra:
                fn_extra(doc)
        self._mutate(fn)
        self._closed = True

    def succeed(self, message: Optional[str] = "완료", basis: str = "reported", exit_code: Optional[int] = None) -> None:
        def extra(doc):
            doc["message"] = message
            doc["completion"] = {"basis": basis, "exit_code": exit_code}
            doc["error"] = None
            for st in doc.get("stages") or []:
                if st.get("state") == "succeeded" and st.get("total") is not None:
                    st["done"] = st["total"]
        self._finish("succeeded", extra)

    def fail(self, message: str, *, exc: Optional[BaseException] = None, error_type: Optional[str] = None,
             detail: Optional[str] = None, exit_code: Optional[int] = None) -> None:
        if exc is not None and detail is None:
            detail = "".join(traceback.format_exception(type(exc), exc, exc.__traceback__))[-4000:]

        def extra(doc):
            doc["message"] = message
            doc["error"] = {"type": error_type or (type(exc).__name__ if exc else "Error"),
                            "message": message, "detail": detail, "stage": doc.get("current_stage")}
            doc["completion"] = {"basis": "exit_code" if exit_code is not None else "reported", "exit_code": exit_code}
        self._finish("failed", extra)

    def cancelled(self, message: str = "중단 요청으로 중단됨") -> None:
        def extra(doc):
            doc["message"] = message
        self._finish("cancelled", extra)
        with contextlib.suppress(OSError):
            os.remove(os.path.join(control_dir(self.root), f"{self.id}.cancel"))

    # -- INFER_PROGRESS_JSON 연결
    def feed_line(self, line: str, stage: Optional[str] = None, unit: str = "frames") -> bool:
        """로그 한 줄을 받아 INFER_PROGRESS_JSON 이면 진행률로 반영. 반영했으면 True.

        JSON에 "stage" 키가 있고 지금 단계와 다르면 지금 단계를 끝내고 그 단계를 시작합니다.
        "sample" 키가 있으면 처리 중인 입력(subject)으로 표시합니다.
        """
        info = parse_infer_line(line)
        if info is None:
            return False
        st_key = info.get("stage") or stage
        if st_key and st_key != self._current_stage_key():
            if self._current_stage_key():
                self.end_stage(self._current_stage_key())
            self.begin_stage(st_key, unit=unit)
        sample = info.get("sample")
        self.progress(done=info.get("frame"), total=info.get("total"), stage=st_key, unit=unit,
                      rate=info.get("fps"), rate_unit=f"{unit}/s" if info.get("fps") is not None else None,
                      eta_seconds=info.get("eta_seconds"), subject=sample)
        return True

    # -- context manager
    def __enter__(self) -> "TaskReporter":
        return self

    def __exit__(self, et, ev, tb):
        if self._closed:
            return False
        if et is None:
            # 명시적인 완료 기록 없이 블록을 벗어남 → 완료로 추정하지 않음
            self.fail("완료 기록 없이 작업 블록이 끝났습니다", error_type="IncompleteExit")
        elif issubclass(et, KeyboardInterrupt):
            self.cancelled(str(ev) or "중단됨")
        else:
            self.fail(f"{et.__name__}: {ev}", exc=ev)
        return False


def is_complete_path(p: str) -> bool:
    low = p.lower().rstrip("\\/")
    if low.endswith((".partial", ".tmp", ".part", ".incomplete")):
        return False
    return os.path.exists(p)


def parse_infer_line(line: str) -> Optional[dict]:
    m = INFER_RE.search(line.strip())
    if not m:
        return None
    try:
        info = json.loads(m.group(1))
    except json.JSONDecodeError:
        return None
    return info if isinstance(info, dict) else None


def request_cancel(task_id: str, root: Optional[str] = None) -> str:
    d = control_dir(root)
    os.makedirs(d, exist_ok=True)
    p = os.path.join(d, f"{_check_id(task_id)}.cancel")
    with open(p, "w", encoding="utf-8") as f:
        f.write(now_iso())
    return p


# ---------------------------------------------------------------- CLI
def _parse_stage_args(items: Optional[list]) -> list:
    out = []
    for it in items or []:
        key, _, rest = it.partition(":")
        name, _, exp = rest.partition("@")
        d = {"key": key, "name": name or key}
        if exp:
            d["expected_seconds"] = float(exp)
        out.append(d)
    return out


def _cmd_register(a) -> int:
    t = TaskReporter(a.name, task_id=a.id, project=a.project, workdir=a.workdir, subject=a.subject,
                     stages=_parse_stage_args(a.stage), log_path=a.log, pid=a.pid,
                     stale_after_s=a.stale_after, state="running")
    t.start(a.message)
    if a.stage:
        t.begin_stage(_parse_stage_args(a.stage)[0]["key"])
    for r in a.result or []:
        label, _, path = r.partition("=")
        t.add_result(label if path else os.path.basename(label), path or label)
    print(t.id)
    return 0


def _cmd_set(a) -> int:
    t = TaskReporter.attach(a.id)
    if a.stage and a.begin:
        t.begin_stage(a.stage, total=a.total, unit=a.unit, validating=(a.state == "validating"))
    if a.done is not None:
        t.progress(a.done, a.total, stage=a.stage, unit=a.unit, force=True)
    if a.result:
        for r in a.result:
            label, _, path = r.partition("=")
            t.add_result(label if path else os.path.basename(label), path or label)
    if a.state == "succeeded":
        t.succeed(a.message or "완료")
    elif a.state == "failed":
        t.fail(a.message or "실패")
    elif a.state == "cancelled":
        t.cancelled(a.message or "중단됨")
    elif a.state:
        t.update(state=a.state, **({"message": a.message} if a.message else {}))
    elif a.message:
        t.message(a.message)
    return 0


def _tail_lines(path: str, n: int = 15) -> str:
    try:
        with open(path, "rb") as f:
            f.seek(0, 2)
            size = f.tell()
            f.seek(max(0, size - 8192))
            return "\n".join(f.read().decode("utf-8", "replace").splitlines()[-n:])
    except OSError:
        return ""


def _cmd_wrap(a) -> int:
    """명령을 실행하고 stdout/stderr 를 로그로 저장, INFER_PROGRESS_JSON 줄을 진행률로 반영."""
    cmd = a.command[1:] if a.command and a.command[0] == "--" else a.command
    if not cmd:
        print("실행할 명령이 없습니다: taskstate.py wrap --name X -- python script.py", file=sys.stderr)
        return 2
    stages = _parse_stage_args(a.stage) or [{"key": "run", "name": "실행"}]
    log = a.log or os.path.join(root_dir(), "logs", "{id}.log")
    t = TaskReporter(a.name, task_id=a.id, project=a.project, workdir=a.workdir, subject=a.subject,
                     stages=stages, stale_after_s=a.stale_after, cancel_supported=a.cancel_supported)
    log = log.replace("{id}", t.id)
    t.set_log_path(log)
    for r in a.result or []:
        label, _, path = r.partition("=")
        t.add_result(label if path else os.path.basename(label), path or label)
    first = stages[0]["key"]
    t.begin_stage(first, unit=a.unit)
    env = dict(os.environ, TASKMONITOR_TASK_ID=t.id, TASKMONITOR_DIR=t.root, PYTHONUNBUFFERED="1")
    os.makedirs(os.path.dirname(os.path.abspath(log)) or ".", exist_ok=True)
    with open(log, "a", encoding="utf-8") as lf:
        lf.write(f"# taskstate wrap {now_iso()}: {subprocess.list2cmdline(cmd)}\n")
        lf.flush()
        try:
            proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env,
                                    cwd=a.workdir or None, text=True, encoding="utf-8", errors="replace", bufsize=1)
        except OSError as e:
            t.fail(f"명령 실행 실패: {e}", exc=e)
            return 1
        t.update(child_pid=proc.pid)
        for line in proc.stdout:
            lf.write(line)
            lf.flush()
            if not a.quiet:
                sys.stdout.write(line)
            try:
                t.feed_line(line, unit=a.unit)
            except Exception as e:  # 진행률 반영 실패가 작업을 멈추지 않도록
                lf.write(f"# taskstate: 진행률 반영 실패 {e}\n")
        rc = proc.wait()
    snap = t.snapshot()
    if snap.get("state") in TERMINAL_STATES:
        return rc  # 자식 프로세스가 attach 해서 직접 완료/실패를 기록한 경우 그대로 둠
    if rc == 0:
        t.flush()
        cur = t.snapshot().get("current_stage") or first
        t.end_stage(cur)
        for st in t.snapshot().get("stages") or []:
            if st.get("state") == "pending":  # 실행 기록이 없는 단계는 완료로 표시하지 않음
                t.skip_stage(st["key"], "실행 기록 없음 (종료 코드 0으로 끝남)")
        t.succeed("종료 코드 0 (래퍼가 확인)", basis="exit_code", exit_code=0)
    else:
        t.fail(f"종료 코드 {rc}", error_type="ExitCode", detail=_tail_lines(log), exit_code=rc)
    return rc


def _pid_alive(pid: int) -> bool:
    if os.name == "nt":
        import ctypes
        h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            return False
        code = ctypes.c_ulong()
        ok = ctypes.windll.kernel32.GetExitCodeProcess(h, ctypes.byref(code))
        ctypes.windll.kernel32.CloseHandle(h)
        return bool(ok) and code.value == 259  # STILL_ACTIVE
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def _cmd_follow(a) -> int:
    """이미 실행 중인 다른 프로그램의 로그를 따라 읽어 진행률 반영 (완료 여부는 추정하지 않음)."""
    if a.id and read_task(a.id):
        t = TaskReporter.attach(a.id)
    else:
        t = TaskReporter(a.name or os.path.basename(a.log), task_id=a.id, project=a.project,
                         stages=_parse_stage_args(a.stage) or [{"key": "run", "name": "실행"}],
                         log_path=a.log, pid=a.pid, stale_after_s=a.stale_after, state="running")
        t.begin_stage((_parse_stage_args(a.stage) or [{"key": "run"}])[0]["key"], unit=a.unit)
        print(t.id)
    pos = 0 if a.from_start else (os.path.getsize(a.log) if os.path.exists(a.log) else 0)
    buf = ""
    while True:
        try:
            with open(a.log, "r", encoding="utf-8", errors="replace") as f:
                f.seek(pos)
                chunk = f.read()
                pos = f.tell()
        except FileNotFoundError:
            chunk = ""
        if chunk:
            buf += chunk
            *lines, buf = buf.split("\n")
            for line in lines:
                t.feed_line(line, unit=a.unit)
        t.flush()
        if t.snapshot().get("state") in TERMINAL_STATES:
            return 0
        if a.pid and not _pid_alive(a.pid):
            t.update(process_exited_at=now_iso())
            return 0  # 완료/실패를 추정하지 않음. 모니터가 "종료 기록 없음"으로 표시
        time.sleep(a.poll)


def main(argv: Optional[list] = None) -> int:
    ap = argparse.ArgumentParser(prog="taskstate", description="TaskMonitor 작업 상태 기록 도구")
    sub = ap.add_subparsers(dest="cmd", required=True)

    def common(p, name_required=False):
        p.add_argument("--id")
        p.add_argument("--name", required=name_required)
        p.add_argument("--project")
        p.add_argument("--workdir")
        p.add_argument("--subject")
        p.add_argument("--stage", action="append", help="key:이름@예상초  (여러 번 지정 가능)")
        p.add_argument("--result", action="append", help="라벨=경로")
        p.add_argument("--stale-after", type=int, default=120)

    p = sub.add_parser("register", help="이미 실행한 프로세스를 작업으로 등록")
    common(p, True)
    p.add_argument("--pid", type=int)
    p.add_argument("--log")
    p.add_argument("--message")
    p.set_defaults(fn=_cmd_register)

    p = sub.add_parser("set", help="등록된 작업 상태 갱신 (배치 파일 등에서)")
    p.add_argument("--id", required=True)
    p.add_argument("--state", choices=TASK_STATES)
    p.add_argument("--stage")
    p.add_argument("--begin", action="store_true", help="--stage 단계를 시작")
    p.add_argument("--done", type=float)
    p.add_argument("--total", type=float)
    p.add_argument("--unit")
    p.add_argument("--message")
    p.add_argument("--result", action="append")
    p.set_defaults(fn=_cmd_set)

    p = sub.add_parser("wrap", help="명령을 실행하며 INFER_PROGRESS_JSON 을 진행률로 반영")
    common(p, True)
    p.add_argument("--log", help="로그 파일 경로 ({id} 사용 가능)")
    p.add_argument("--unit", default="frames")
    p.add_argument("--cancel-supported", action="store_true",
                   help="자식 스크립트가 TaskReporter.attach() 로 cancel_requested() 를 확인할 때만 지정")
    p.add_argument("--quiet", action="store_true")
    p.add_argument("command", nargs=argparse.REMAINDER)
    p.set_defaults(fn=_cmd_wrap)

    p = sub.add_parser("follow-log", help="실행 중인 다른 프로그램의 로그를 따라 읽기")
    common(p)
    p.add_argument("--log", required=True)
    p.add_argument("--pid", type=int)
    p.add_argument("--unit", default="frames")
    p.add_argument("--poll", type=float, default=2.0)
    p.add_argument("--from-start", action="store_true")
    p.set_defaults(fn=_cmd_follow)

    p = sub.add_parser("cancel", help="중단 요청 파일 생성 (작업이 지원할 때만 의미 있음)")
    p.add_argument("--id", required=True)
    p.set_defaults(fn=lambda a: print(request_cancel(a.id)) or 0)

    p = sub.add_parser("show", help="작업 상태 JSON 출력")
    p.add_argument("--id", required=True)
    p.set_defaults(fn=lambda a: print(json.dumps(read_task(a.id), ensure_ascii=False, indent=1)) or 0)

    a = ap.parse_args(argv)
    return a.fn(a)


if __name__ == "__main__":
    sys.exit(main())
