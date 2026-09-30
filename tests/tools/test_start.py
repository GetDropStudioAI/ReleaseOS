"""Tests for the git logic behind `start.py reset`. Run: python3 tests/tools/test_start.py
Uses throwaway local repos; needs only git and python."""
import subprocess, sys, tempfile, unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
import start  # noqa: E402


def run(*args, cwd):
    subprocess.run(args, cwd=cwd, check=True, capture_output=True, text=True)


class PullLatest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.origin = self.tmp / "origin.git"
        self.work = self.tmp / "work"
        self.other = self.tmp / "other"
        run("git", "init", "--bare", "-b", "main", str(self.origin), cwd=self.tmp)
        run("git", "clone", str(self.origin), str(self.work), cwd=self.tmp)
        for repo in (self.work,):
            run("git", "config", "user.email", "t@t", cwd=repo); run("git", "config", "user.name", "t", cwd=repo)
        (self.work / "a.txt").write_text("1")
        run("git", "add", "-A", cwd=self.work); run("git", "commit", "-m", "one", cwd=self.work)
        run("git", "push", "-u", "origin", "main", cwd=self.work)
        run("git", "clone", str(self.origin), str(self.other), cwd=self.tmp)
        run("git", "config", "user.email", "t@t", cwd=self.other); run("git", "config", "user.name", "t", cwd=self.other)

    def push_from_other(self, name="b.txt", content="2"):
        (self.other / name).write_text(content)
        run("git", "add", "-A", cwd=self.other); run("git", "commit", "-m", f"add {name}", cwd=self.other)
        run("git", "push", "origin", "main", cwd=self.other)

    def test_fast_forwards_and_reports_changed_files(self):
        self.push_from_other()
        r = start.pull_latest(self.work)
        self.assertEqual(r["changed"], ["b.txt"])
        self.assertTrue((self.work / "b.txt").exists())
        self.assertNotEqual(r["before"], r["after"])

    def test_already_up_to_date_changes_nothing(self):
        r = start.pull_latest(self.work)
        self.assertEqual(r["changed"], [])
        self.assertEqual(r["before"], r["after"])

    def test_refuses_with_uncommitted_changes_and_leaves_them_alone(self):
        self.push_from_other()
        (self.work / "a.txt").write_text("edited locally")
        with self.assertRaisesRegex(RuntimeError, "uncommitted"):
            start.pull_latest(self.work)
        self.assertEqual((self.work / "a.txt").read_text(), "edited locally")
        self.assertFalse((self.work / "b.txt").exists())

    def test_refuses_when_local_commits_diverge_instead_of_merging(self):
        self.push_from_other()
        (self.work / "c.txt").write_text("local")
        run("git", "add", "-A", cwd=self.work); run("git", "commit", "-m", "local", cwd=self.work)
        with self.assertRaisesRegex(RuntimeError, "ff-only|fast-forward|Not possible"):
            start.pull_latest(self.work)
        self.assertTrue((self.work / "c.txt").exists())   # local work untouched

    def test_switches_to_main_when_on_another_branch(self):
        run("git", "checkout", "-b", "scratch", cwd=self.work)
        self.push_from_other()
        r = start.pull_latest(self.work)
        self.assertEqual(r["switchedFrom"], "scratch")
        self.assertEqual(start.git("rev-parse", "--abbrev-ref", "HEAD", cwd=self.work), "main")


class Helpers(unittest.TestCase):
    def test_free_reports_a_bound_port_as_busy(self):
        import socket
        s = socket.socket(); s.bind(("127.0.0.1", 0)); s.listen(1)
        self.assertFalse(start.free(s.getsockname()[1]))
        s.close()


class FrontendDeps(unittest.TestCase):
    """A stale node_modules (installed before package-lock.json changed) is reinstalled on start, not left to fail in Vite."""
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.web = Path(self.tmp.name)
        (self.web / "package-lock.json").write_text('{"packages": {"node_modules/react": {}}}')
        self.calls = []

    def tearDown(self):
        self.tmp.cleanup()

    def fake_npm_ci(self, cmd, cwd, check):
        self.calls.append(cmd); (Path(cwd) / "node_modules").mkdir(exist_ok=True)

    def test_missing_node_modules_is_not_current(self):
        self.assertFalse(start.frontend_deps_current(self.web))

    def test_node_modules_without_marker_is_stale(self):
        (self.web / "node_modules").mkdir()
        self.assertFalse(start.frontend_deps_current(self.web))

    def test_install_marks_current_and_a_lock_change_makes_it_stale_again(self):
        start.install_frontend_deps("npm", self.web, run=self.fake_npm_ci)
        self.assertEqual(self.calls, [["npm", "ci"]])
        self.assertTrue(start.frontend_deps_current(self.web))
        (self.web / "package-lock.json").write_text('{"packages": {"node_modules/@microsoft/signalr": {}}}')
        self.assertFalse(start.frontend_deps_current(self.web))


if __name__ == "__main__":
    unittest.main(verbosity=2)
