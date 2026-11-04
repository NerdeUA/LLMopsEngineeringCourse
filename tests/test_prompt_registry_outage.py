"""Distinguish an empty prompt registry from an unavailable database (Topic 2)."""

import json
import subprocess
import time
import unittest
import urllib.error
import urllib.request
import uuid

from test_hw1 import OPENER, ROOT


class RegistryOutageTests(unittest.TestCase):
    def test_unavailable_database_returns_503_and_logs_infrastructure_failure(self):
        name = "hw1-registry-outage-" + uuid.uuid4().hex[:10]
        subprocess.run(
            ["docker", "compose", "run", "--rm", "-d", "--no-deps", "--name", name,
             "-p", "127.0.0.1::8080", "-e",
             "DB_CONN=Host=postgres;Port=1;Database=llmops;Username=llmops;Password=llmops;Timeout=1",
             "service"], cwd=ROOT, capture_output=True, text=True, check=True,
        )
        self.addCleanup(lambda: subprocess.run(
            ["docker", "stop", name], capture_output=True, text=True, check=True))
        mapping = subprocess.check_output(["docker", "port", name, "8080/tcp"], text=True).strip()
        base = "http://" + mapping.splitlines()[0]
        deadline = time.monotonic() + 20
        while True:
            try:
                with OPENER.open(base + "/health", timeout=2) as response:
                    self.assertEqual(response.status, 200)
                break
            except OSError:
                if time.monotonic() > deadline:
                    raise
                time.sleep(0.2)
        request = urllib.request.Request(
            base + "/chat", data=json.dumps({"message": "Як скинути пароль?"}).encode(),
            headers={"Content-Type": "application/json"},
        )
        try:
            with OPENER.open(request, timeout=10) as response:
                status, body = response.status, json.load(response)
        except urllib.error.HTTPError as error:
            with error:
                status, body = error.code, json.load(error)
        self.assertEqual(status, 503, body)
        self.assertEqual(body["title"], "Prompt registry unavailable")
        logs = subprocess.check_output(["docker", "logs", name], text=True, stderr=subprocess.STDOUT)
        self.assertIn("Prompt registry unavailable for request", logs)
