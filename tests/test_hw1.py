"""HW1 acceptance tests against a running, freshly initialized mock stack.

Run: docker compose up --build -d
     python3 -m unittest discover -s tests -v
Tests switch prompt versions and write request logs; v2 is restored afterwards.
"""

import concurrent.futures
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
BASE = os.environ.get("SERVICE_URL", "http://localhost:8080")
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def api(path, data=None):
    request = urllib.request.Request(
        BASE + path,
        data=None if data is None else json.dumps(data).encode(),
        headers={"Content-Type": "application/json"},
    )
    try:
        with OPENER.open(request, timeout=60) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        with error:
            return error.code, None


def sql(query):
    return subprocess.check_output(
        ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "llmops",
         "-d", "llmops", "-At", "-v", "ON_ERROR_STOP=1", "-c", query],
        cwd=ROOT, text=True,
    ).strip()


class PromptRegistryTests(unittest.TestCase):
    @classmethod
    def tearDownClass(cls):
        api("/prompts/v2/activate", {})

    def activate(self, version):
        self.assertEqual(api(f"/prompts/{version}/activate", {})[0], 200)

    def logged_chat(self, version, message="How do I reset my password?"):
        status, answer = api("/chat", {"message": message})
        self.assertEqual(status, 200)
        row = sql(
            "SELECT prompt_version FROM requests WHERE request_id = "
            f"'{answer['request_id']}'::uuid"
        )
        self.assertEqual(row, version)
        return answer["content"]

    def test_01_fresh_seed_and_registry(self):
        status, prompts = api("/prompts")
        self.assertEqual(status, 200)
        self.assertIsInstance(prompts, list)
        self.assertEqual(
            [(p["name"], p["version"], p["active"]) for p in prompts],
            [("support", "v1", False), ("support", "v2", True)],
        )
        self.assertEqual(
            sql("SELECT version || '|' || body FROM prompts ORDER BY version"),
            "v1|You are an assistant.\nv2|You are a support assistant. Be concise and helpful.",
        )

    def test_02_unknown_version_does_not_change_active(self):
        self.activate("v2")
        before = api("/prompts")[1]
        for version in ("v99", "v99%27%20OR%20%271%27%3D%271"):
            self.assertEqual(api(f"/prompts/{version}/activate", {})[0], 404)
            self.assertEqual(api("/prompts")[1], before)

    def test_03_rollback_changes_chat_and_evals(self):
        for version, expected_exit, expected_count in (("v1", 1, "6/10"), ("v2", 0, "10/10")):
            with self.subTest(version=version):
                self.activate(version)
                content = self.logged_chat(version)
                if version == "v1":
                    self.assertIn("не знаю", content.lower())
                else:
                    self.assertNotIn("не знаю", content.lower())
                result = subprocess.run(
                    [sys.executable, "evals/run.py", "--dataset", "evals/golden.jsonl", "--threshold", "9"],
                    cwd=ROOT, capture_output=True, text=True,
                )
                print(result.stdout, end="")
                self.assertEqual(result.returncode, expected_exit, result.stdout + result.stderr)
                self.assertIn(f"eval: {expected_count} passed, threshold 9", result.stdout)

    def test_04_no_active_prompt_uses_visible_fallback(self):
        try:
            sql("UPDATE prompts SET active=false")
            content = self.logged_chat("none")
            self.assertIn("не знаю", content.lower())
        finally:
            self.activate("v2")

    def test_05_concurrent_activation_keeps_one_active(self):
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            results = list(pool.map(
                lambda version: api(f"/prompts/{version}/activate", {})[0],
                ["v1", "v2"] * 8,
            ))
        self.assertEqual(results, [200] * 16)
        self.assertEqual(sql("SELECT count(*) FROM prompts WHERE active"), "1")
        self.activate("v2")

    def test_06_upstream_failure_still_logs_version(self):
        self.activate("v2")
        self.logged_chat("v2", "__fail_503")
        self.assertEqual(sql("SELECT status FROM requests ORDER BY created_at DESC LIMIT 1"), "503")


if __name__ == "__main__":
    unittest.main(verbosity=2)
