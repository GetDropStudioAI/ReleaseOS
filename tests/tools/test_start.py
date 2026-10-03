"""Tests for start.py: the git logic behind `reset`, the control channel, frontend packages and the local database upgrade. Run: python3 tests/tools/test_start.py
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


class ControlChannelHost(unittest.TestCase):
    """DNS rebinding (SEC-E2): the control channel refuses any Host that is not a loopback name, whatever the port."""
    def test_loopback_names_are_served(self):
        for h in ("127.0.0.1:5099", "localhost:5099", "127.0.0.1:6273", "LOCALHOST", "[::1]:5099", "127.0.0.1"):
            self.assertTrue(start.loopback_host(h), h)

    def test_foreign_and_missing_hosts_are_refused(self):
        for h in ("attacker.example:5099", "attacker.example", "127.0.0.1.attacker.example:5099", "localhost.attacker.example",
                  "[::2]:5099", "", None, "evil:127.0.0.1"):
            self.assertFalse(start.loopback_host(h), h)


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


class BackendEnvironment(unittest.TestCase):
    """SEC-B1 (docs/security/scan-authn.md): Development maps the anonymous dev sign-in, so start.py must not turn an environment the operator chose into
    Development. Nothing is started: Child, the port probe and the readiness wait are replaced, and the environment handed to the backend is captured."""

    def backend_env(self, **environ):
        import os
        from unittest import mock
        captured = {}

        class FakeChild:
            def __init__(self, name, cmd, cwd, env):
                captured[name] = env

            def start(self): pass
            def alive(self): return True
            def stop(self): pass

        web = Path(tempfile.mkdtemp()); (web / "node_modules").mkdir()
        with mock.patch.dict(os.environ, environ), mock.patch.multiple(start, Child=FakeChild, free=lambda port: True, which=lambda name: name, frontend_deps_current=lambda *a: True,
                                                                       wait_for=lambda *a, **k: None, WEB_DIR=web):
            for k in ("ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT"):
                if k not in environ:
                    os.environ.pop(k, None)   # restored by patch.dict
            sup = start.Supervisor(open_browser=False)
            sup.revision = lambda: "test"
            sup.launch()
        return captured["api"]

    def test_a_developer_laptop_still_gets_development(self):
        self.assertEqual(self.backend_env()["ASPNETCORE_ENVIRONMENT"], "Development")

    def test_an_explicit_aspnetcore_environment_is_not_overridden(self):
        self.assertEqual(self.backend_env(ASPNETCORE_ENVIRONMENT="Production")["ASPNETCORE_ENVIRONMENT"], "Production")

    def test_an_explicit_dotnet_environment_is_not_overridden(self):
        self.assertEqual(self.backend_env(DOTNET_ENVIRONMENT="Production")["ASPNETCORE_ENVIRONMENT"], "Production")

    def test_the_backend_is_always_bound_to_loopback(self):
        self.assertEqual(self.backend_env(ASPNETCORE_URLS="http://0.0.0.0:6080")["ASPNETCORE_URLS"], start.API_URL)
        self.assertTrue(start.API_URL.startswith("http://127.0.0.1:"))


class DatabaseUpgrade(unittest.TestCase):
    """Q-SEC-B8m: a pull that changed the schema left local databases unstartable ("the backend did not become ready"). start.py now finds the
    db/upgrades chain from the migrations a database records, backs the file up and applies it before the backend opens it."""
    ROOT = Path(__file__).resolve().parents[2]
    FIRST = ("20260929110130_Schema", "20260929110136_Triggers")

    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        self.db = self.dir / "releasemgmt.db"
        self.said = []

    def make(self, history, schema=None):
        import sqlite3
        c = sqlite3.connect(self.db)
        c.executescript((schema or self.ROOT / "db/upgrades/baseline/schema-20260929.sql").read_text())
        c.execute("CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL)")
        c.executemany("INSERT INTO __EFMigrationsHistory VALUES (?, '10.0.12')", [(m,) for m in history])
        c.execute("INSERT INTO Users (Id, Email, DisplayName, Role) VALUES ('u1', 'rte@x.com', 'Rte', 'RTE')")
        c.commit(); c.close()

    def upgrade(self):
        return start.upgrade_database(self.db, say=self.said.append)

    def query(self, sql):
        import sqlite3
        c = sqlite3.connect(self.db)
        try: return c.execute(sql).fetchall()
        finally: c.close()

    def test_a_first_release_database_is_backed_up_and_brought_to_this_build(self):
        self.make(self.FIRST)
        backup = self.upgrade()
        self.assertTrue(backup and backup.exists() and backup.name.startswith("releasemgmt.pre-upgrade-"))
        self.assertEqual([r[0] for r in self.query("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY 1")], start.build_migrations())
        self.assertEqual(self.query("SELECT Id, Email FROM Users"), [("u1", "rte@x.com")])                                   # rows kept
        self.assertEqual(self.query("SELECT count(*) FROM sqlite_master WHERE name IN ('SessionRevocations','TrainMilestones')"), [(2,)])
        import sqlite3
        b = sqlite3.connect(backup)
        self.assertEqual(sorted(r[0] for r in b.execute("SELECT MigrationId FROM __EFMigrationsHistory")), list(self.FIRST))   # the backup is the old one
        b.close()
        self.assertTrue(any("001_" in m for m in self.said) and any("003_" in m for m in self.said))

    def test_a_partly_upgraded_database_runs_only_the_scripts_it_still_needs(self):
        self.make(self.FIRST)
        import sqlite3
        c = sqlite3.connect(self.db, isolation_level=None)
        c.executescript((self.ROOT / "db/upgrades/001_idp_identity_and_session_revocations.sql").read_text()); c.close()
        self.upgrade()
        self.assertFalse(any("001_" in m for m in self.said))
        self.assertTrue(any("002_" in m for m in self.said) and any("003_" in m for m in self.said))
        self.assertEqual([r[0] for r in self.query("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY 1")], start.build_migrations())

    def test_a_current_missing_or_empty_database_is_left_alone(self):
        self.assertIsNone(self.upgrade())                       # no file yet: the backend creates it
        import sqlite3
        sqlite3.connect(self.db).close()
        self.assertIsNone(self.upgrade())                       # empty file: EF creates everything
        self.db.unlink()
        self.make(start.build_migrations(), schema=self.ROOT / "db/schema.sql")
        self.assertIsNone(self.upgrade())                       # already this build
        self.assertEqual(list(self.dir.glob("*.pre-upgrade-*")), [])

    def test_an_unknown_history_stops_the_start_and_changes_nothing(self):
        self.make(("20250101000000_Schema", "20250101000001_Triggers"))
        before = self.db.read_bytes()
        with self.assertRaises(RuntimeError) as e:
            self.upgrade()
        self.assertIn("cannot upgrade", str(e.exception))
        self.assertEqual(self.db.read_bytes(), before)
        self.assertEqual(list(self.dir.glob("*.pre-upgrade-*")), [])

    def test_launch_upgrades_before_the_backend_starts(self):
        import os
        from unittest import mock
        order = []

        class FakeChild:
            def __init__(self, name, cmd, cwd, env): self.name = name
            def start(self): order.append(self.name)
            def alive(self): return True
            def stop(self): pass

        with mock.patch.multiple(start, Child=FakeChild, free=lambda port: True, which=lambda name: name, frontend_deps_current=lambda *a: True,
                                 wait_for=lambda *a, **k: None, upgrade_database=lambda **k: order.append("upgrade")):
            start.Supervisor(open_browser=False).launch()
        self.assertEqual(order, ["upgrade", "api", "web"])

    def test_the_database_path_follows_Db__Path_like_the_backend(self):
        import os
        from unittest import mock
        with mock.patch.dict(os.environ, {"Db__Path": ""}):
            self.assertEqual(start.database_path(), start.API_DIR / "data" / "releasemgmt.db")
        with mock.patch.dict(os.environ, {"Db__Path": str(self.db)}):
            self.assertEqual(start.database_path(), self.db)


if __name__ == "__main__":
    unittest.main(verbosity=2)
