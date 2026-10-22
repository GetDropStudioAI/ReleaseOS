"""Every GitHub Actions workflow must be valid YAML with at least one job. Run: python3 tests/tools/test_workflows.py
An invalid workflow never starts, so CI cannot report it: from 3 Oct to 22 Oct 2026 every run "failed" with no jobs because a step
name held an unquoted ": ". CI cannot catch this about itself, so it is a local check (CLAUDE.md, Commands): run it after editing a workflow.
Needs PyYAML."""
import sys, unittest
from pathlib import Path

import yaml

WORKFLOWS = Path(__file__).resolve().parents[2] / ".github" / "workflows"


class Workflows(unittest.TestCase):
    def test_every_workflow_parses_and_has_jobs(self):
        files = sorted(WORKFLOWS.glob("*.yml")) + sorted(WORKFLOWS.glob("*.yaml"))
        self.assertTrue(files, f"no workflows under {WORKFLOWS}")
        for f in files:
            with self.subTest(workflow=f.name):
                doc = yaml.safe_load(f.read_text(encoding="utf-8"))
                self.assertIsInstance(doc, dict)
                self.assertTrue(doc.get("jobs"), f"{f.name} has no jobs")
                for name, job in doc["jobs"].items():
                    for i, step in enumerate(job.get("steps", [])):
                        self.assertIsInstance(step, dict, f"{f.name} job {name} step {i} is not a mapping")


if __name__ == "__main__":
    sys.exit(unittest.main())
