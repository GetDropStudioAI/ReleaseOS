#!/usr/bin/env python3
"""Start, reset and stop the Release Management app for local use (Windows, macOS, Linux).

    python start.py                 start backend + frontend, open the browser, keep running (Ctrl+C stops both)
    python start.py --no-browser    same, without opening a browser tab
    python start.py status          ask the running instance what it is doing
    python start.py reset           pull the latest code from origin/Team, restart both, the browser tab reloads itself
    python start.py stop            stop backend + frontend and exit the running instance

The same actions are available in the app toolbar (Reset, Exit) while it runs under this script.
Requires Python 3.9+, the .NET 10 SDK and Node 22 on PATH.
Backend: http://localhost:5080 (dotnet run, Development). Frontend: http://localhost:5173 (Vite, proxies /api, /hub, /auth).
A small control channel listens on 127.0.0.1:5099 only; every state-changing call needs a per-run token.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent
API_DIR = ROOT / "src" / "ReleaseMgmt.Api"
WEB_DIR = ROOT / "src" / "ReleaseMgmt.Web"
LOG_DIR = ROOT / "logs"
STATE_FILE = ROOT / ".start-control.json"   # port + token of the running instance (gitignored)
BRANCH = "Team"
API_PORT, WEB_PORT, CONTROL_PORT = 5080, 5173, 5099
API_URL, WEB_URL = f"http://localhost:{API_PORT}", f"http://localhost:{WEB_PORT}"
IS_WINDOWS = os.name == "nt"


# ----------------------------------------------------------------------------- git

def git(*args: str, cwd: Path = ROOT, check: bool = True) -> str:
    r = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
    if check and r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {(r.stderr or r.stdout).strip()}")
    return r.stdout.strip()


def pull_latest(root: Path = ROOT, branch: str = BRANCH, remote: str = "origin") -> dict:
    """Fast-forward `branch` to remote/branch. Refuses (raises) rather than touching uncommitted work or making a merge."""
    if git("status", "--porcelain", cwd=root):
        raise RuntimeError("You have uncommitted changes. Commit or stash them, then reset again.")
    current = git("rev-parse", "--abbrev-ref", "HEAD", cwd=root)
    before = git("rev-parse", "HEAD", cwd=root)
    git("fetch", remote, branch, cwd=root)
    if current != branch:
        git("checkout", branch, cwd=root)
    git("merge", "--ff-only", f"{remote}/{branch}", cwd=root)
    after = git("rev-parse", "HEAD", cwd=root)
    changed = git("diff", "--name-only", before, after, cwd=root).splitlines() if before != after else []
    return {"before": before[:7], "after": after[:7], "changed": changed, "switchedFrom": current if current != branch else None}


# ----------------------------------------------------------------------------- processes

def free(port: int) -> bool:
    with socket.socket() as s:
        s.settimeout(0.5)
        return s.connect_ex(("127.0.0.1", port)) != 0


def http_ok(url: str, timeout: float = 2.0) -> bool:
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return 200 <= r.status < 400
    except (urllib.error.URLError, OSError, ValueError):
        return False


def wait_for(url: str, seconds: int, what: str) -> None:
    deadline = time.time() + seconds
    while time.time() < deadline:
        if http_ok(url):
            return
        time.sleep(0.5)
    raise RuntimeError(f"{what} did not become ready within {seconds}s (see logs/)")


class Child:
    def __init__(self, name: str, cmd: list[str], cwd: Path, env: dict[str, str]):
        self.name, self.cmd, self.cwd, self.env = name, cmd, cwd, env
        self.proc: subprocess.Popen | None = None

    def start(self) -> None:
        LOG_DIR.mkdir(exist_ok=True)
        kw: dict = {"stdout": subprocess.PIPE, "stderr": subprocess.STDOUT, "text": True, "bufsize": 1, "cwd": self.cwd, "env": {**os.environ, **self.env}}
        if IS_WINDOWS:
            kw["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
        else:
            kw["start_new_session"] = True   # own process group so the whole tree can be stopped
        self.proc = subprocess.Popen(self.cmd, **kw)
        threading.Thread(target=self._pump, daemon=True).start()

    def _pump(self) -> None:
        assert self.proc and self.proc.stdout
        with open(LOG_DIR / f"{self.name}.log", "a", encoding="utf-8") as log:
            log.write(f"\n=== started {time.strftime('%Y-%m-%d %H:%M:%S')} ===\n")
            for line in self.proc.stdout:
                log.write(line)
                log.flush()
                print(f"[{self.name}] {line.rstrip()}", flush=True)

    def alive(self) -> bool:
        return self.proc is not None and self.proc.poll() is None

    def stop(self) -> None:
        p = self.proc
        if p is None or p.poll() is not None:
            return
        try:
            if IS_WINDOWS:
                subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"], capture_output=True)
            else:
                os.killpg(os.getpgid(p.pid), signal.SIGTERM)
                try:
                    p.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(os.getpgid(p.pid), signal.SIGKILL)
            p.wait(timeout=10)
        except (ProcessLookupError, PermissionError, subprocess.TimeoutExpired):
            pass


def which(name: str) -> str:
    found = shutil.which(name)
    if not found:
        raise RuntimeError(f"'{name}' was not found on PATH. Install it (see README) and try again.")
    return found


# ----------------------------------------------------------------------------- supervisor

class Supervisor:
    def __init__(self, open_browser: bool):
        self.open_browser = open_browser
        self.token = secrets.token_urlsafe(24)
        self.state, self.message = "starting", "Starting"
        self.detail: dict = {}
        self.children: list[Child] = []
        self.lock = threading.Lock()
        self.server: ThreadingHTTPServer | None = None
        self.stop_event = threading.Event()

    def set(self, state: str, message: str, **detail) -> None:
        self.state, self.message = state, message
        if detail:
            self.detail = {**self.detail, **detail}
        print(f"[start] {state}: {message}", flush=True)

    def revision(self) -> str:
        try:
            return git("rev-parse", "--short", "HEAD")
        except RuntimeError:
            return "unknown"

    # -- lifecycle
    def launch(self) -> None:
        deadline = time.time() + 20   # after a stop, the OS may take a moment to release the ports
        while (not free(API_PORT) or not free(WEB_PORT)) and time.time() < deadline:
            time.sleep(0.5)
        if not free(API_PORT) or not free(WEB_PORT):
            raise RuntimeError(f"Port {API_PORT} or {WEB_PORT} is already in use. Is the app already running? Try `python start.py stop`.")
        dotnet, npm = which("dotnet"), which("npm")
        if not (WEB_DIR / "node_modules").exists():
            self.set("starting", "Installing frontend packages (first run)")
            subprocess.run([npm, "ci"], cwd=WEB_DIR, check=True)
        api = Child("api", [dotnet, "run", "--no-launch-profile"], API_DIR,
                    {"ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": API_URL, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
        web = Child("web", [npm, "run", "dev", "--", "--port", str(WEB_PORT), "--strictPort"], WEB_DIR,
                    {"VITE_CONTROL_TOKEN": self.token})
        self.children = [api, web]
        self.set("starting", "Starting backend")
        api.start()
        wait_for(f"{API_URL}/healthz", 180, "The backend")
        self.set("starting", "Starting frontend")
        web.start()
        wait_for(WEB_URL, 60, "The frontend")
        self.set("ready", "Running", revision=self.revision(), api=API_URL, web=WEB_URL)

    def halt(self) -> None:
        for c in reversed(self.children):
            c.stop()
        self.children = []

    def reset(self) -> None:
        """Pull origin/Team, restart both processes. The page reloads itself once state is 'ready' again."""
        if not self.lock.acquire(blocking=False):
            return
        try:
            self.set("resetting", "Pulling the latest code from origin/Team")
            script_before = hashlib.sha256((ROOT / "start.py").read_bytes()).hexdigest()   # must be taken before the pull rewrites the file
            try:
                result = pull_latest()
            except RuntimeError as e:
                self.set("error", str(e))   # nothing was stopped: the app keeps running on the old code
                return
            self.detail = {**self.detail, "pull": result}
            self.set("resetting", f"Pulled {result['before']} → {result['after']}: restarting")
            self.halt()
            deps = {"src/ReleaseMgmt.Web/package.json", "src/ReleaseMgmt.Web/package-lock.json"} & set(result["changed"])
            if deps:
                self.set("resetting", "Frontend dependencies changed: npm ci")
                subprocess.run([which("npm"), "ci"], cwd=WEB_DIR, check=True)
            if hashlib.sha256((ROOT / "start.py").read_bytes()).hexdigest() != script_before:
                self.set("resetting", "start.py itself changed: relaunching")
                self.close_control()
                os.execv(sys.executable, [sys.executable, str(ROOT / "start.py"), "--no-browser"])
            self.launch()
        except Exception as e:  # noqa: BLE001 - surfaced to the UI and console, never swallowed
            self.set("error", f"Reset failed: {e}")
        finally:
            self.lock.release()

    def exit(self) -> None:
        self.set("stopping", "Stopping")
        self.halt()
        self.stop_event.set()

    def close_control(self) -> None:
        if self.server:
            self.server.shutdown()
            self.server.server_close()
        STATE_FILE.unlink(missing_ok=True)

    # -- control channel
    def serve_control(self) -> None:
        sup = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *a) -> None:  # quiet
                pass

            def _send(self, code: int, body: dict) -> None:
                data = json.dumps(body).encode()
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self) -> None:
                if self.path.rstrip("/") == "/control/status":
                    self._send(200, {"state": sup.state, "message": sup.message, **sup.detail, "revision": sup.revision(),
                                     "running": bool(sup.children) and all(c.alive() for c in sup.children)})
                else:
                    self._send(404, {"message": "not found"})

            def do_POST(self) -> None:
                if not secrets.compare_digest(self.headers.get("X-Control-Token", ""), sup.token):
                    return self._send(403, {"message": "bad control token"})
                path = self.path.rstrip("/")
                if path == "/control/reset":
                    if sup.state in ("resetting", "starting", "stopping"):
                        return self._send(409, {"message": f"busy: {sup.state}"})
                    threading.Thread(target=sup.reset, daemon=True).start()
                    return self._send(202, {"message": "reset started"})
                if path == "/control/exit":
                    threading.Thread(target=sup.exit, daemon=True).start()
                    return self._send(202, {"message": "stopping"})
                self._send(404, {"message": "not found"})

        self.server = ThreadingHTTPServer(("127.0.0.1", CONTROL_PORT), Handler)
        STATE_FILE.write_text(json.dumps({"port": CONTROL_PORT, "token": self.token, "pid": os.getpid()}))
        threading.Thread(target=self.server.serve_forever, daemon=True).start()


# ----------------------------------------------------------------------------- CLI

def talk(method: str, path: str) -> dict:
    if not STATE_FILE.exists():
        raise RuntimeError("No running instance found (start it with `python start.py`).")
    info = json.loads(STATE_FILE.read_text())
    req = urllib.request.Request(f"http://127.0.0.1:{info['port']}{path}", method=method, data=b"{}" if method == "POST" else None,
                                 headers={"X-Control-Token": info["token"], "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return json.loads(r.read())
    except (urllib.error.URLError, OSError) as e:
        raise RuntimeError(f"The running instance did not answer ({e}). If it crashed, delete {STATE_FILE.name}.") from e


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", nargs="?", default="run", choices=["run", "status", "reset", "stop"])
    ap.add_argument("--no-browser", action="store_true", help="do not open a browser tab")
    args = ap.parse_args(argv)

    if args.command != "run":
        try:
            if args.command == "status":
                print(json.dumps(talk("GET", "/control/status"), indent=2))
            elif args.command == "reset":
                print(talk("POST", "/control/reset")["message"], "- watch the other terminal or the browser tab")
            else:
                print(talk("POST", "/control/exit")["message"])
        except RuntimeError as e:
            print(f"error: {e}", file=sys.stderr)
            return 1
        return 0

    sup = Supervisor(open_browser=not args.no_browser)
    signal.signal(signal.SIGINT, lambda *_: sup.exit())
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, lambda *_: sup.exit())
    try:
        sup.serve_control()
    except OSError as e:
        print(f"error: control port {CONTROL_PORT} is busy: {e}. Is the app already running? Try `python start.py stop`.", file=sys.stderr)
        return 1
    try:
        sup.launch()
        print(f"\n  Release Management is running\n  App:      {WEB_URL}\n  API:      {API_URL}\n  Stop:     Ctrl+C, `python start.py stop`, or Exit in the app\n  Reset:    `python start.py reset` or Reset in the app (pulls origin/{BRANCH})\n", flush=True)
        if sup.open_browser:
            webbrowser.open(WEB_URL)
    except Exception as e:  # noqa: BLE001
        sup.set("error", str(e))
        print(f"error: {e}", file=sys.stderr)
        sup.halt()
        sup.close_control()
        return 1
    while not sup.stop_event.is_set():
        time.sleep(0.5)
        if sup.state in ("ready", "error") and not sup.lock.locked() and sup.children and not all(c.alive() for c in sup.children):
            dead = [c.name for c in sup.children if not c.alive()]
            sup.set("error", f"{', '.join(dead)} stopped unexpectedly (see logs/{dead[0]}.log). Use Reset to restart.")
    sup.close_control()
    print("[start] stopped", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
