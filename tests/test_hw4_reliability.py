"""Topic 7 fallback and graceful degradation on the mock stack."""

import subprocess
import json
import threading
import time
import unittest
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from test_hw1 import OPENER, ROOT, sql
from test_hw3_cache import chat


class ReliabilityTests(unittest.TestCase):
    def test_503_and_429_have_honest_failure_log_and_no_invented_result(self):
        for marker, expected_status in [('__fail_503', '503'), ('__fail_429', '429')]:
            with self.subTest(marker=marker):
                response, row = chat(marker)
                text = response['content'].lower()
                self.assertIn('тимчасов', text)
                self.assertNotIn('тікет', text)
                self.assertFalse(any(char.isdigit() for char in text))
                self.assertEqual(row['status'], expected_status)
                self.assertIsNone(row['cost_usd'])
                logs = subprocess.check_output(
                    ['docker', 'compose', 'logs', '--no-color', 'service'],
                    cwd=ROOT, text=True)
                self.assertIn(f"Fallback for request {response['request_id']}", logs)

    def test_normal_request_succeeds_after_incident(self):
        chat('__fail_503')
        response, row = chat('Підкажіть, будь ласка, як увійти у свій профіль?')
        self.assertEqual(row['status'], '200')
        self.assertNotIn('тимчасово недоступний', response['content'].lower())

    def test_successful_fallback_records_the_actual_model(self):
        calls = []

        class Gateway(BaseHTTPRequestHandler):
            def do_POST(self):
                data = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                calls.append(data['model'])
                if data['model'] == 'mock-mini':
                    self.send_response(503)
                    self.end_headers()
                    return
                body = json.dumps({'choices': [{'message': {'content': 'Резервна відповідь'}}],
                                   'usage': {'prompt_tokens': 12, 'completion_tokens': 4}}).encode()
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):
                pass

        gateway = ThreadingHTTPServer(('0.0.0.0', 0), Gateway)
        thread = threading.Thread(target=gateway.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(gateway.server_close)
        self.addCleanup(gateway.shutdown)
        name = 'hw4-fallback-' + uuid.uuid4().hex[:10]
        subprocess.run(['docker', 'compose', 'run', '--rm', '-d', '--no-deps', '--name', name,
                        '-p', '127.0.0.1::8080', '-e',
                        f'GATEWAY_URL=http://host.docker.internal:{gateway.server_port}',
                        'service'], cwd=ROOT, capture_output=True, text=True, check=True)
        self.addCleanup(lambda: subprocess.run(['docker', 'stop', name], capture_output=True, check=True))
        mapping = subprocess.check_output(['docker', 'port', name, '8080/tcp'], text=True).strip()
        base = 'http://' + mapping.splitlines()[0]
        deadline = time.monotonic() + 20
        while True:
            try:
                with OPENER.open(base + '/health', timeout=2):
                    break
            except OSError:
                if time.monotonic() > deadline:
                    raise
                time.sleep(.2)
        request = urllib.request.Request(base + '/chat', data=json.dumps({'message': 'Тест резерву'}).encode(),
                                         headers={'Content-Type': 'application/json'})
        with OPENER.open(request, timeout=15) as response:
            body = json.load(response)
        self.assertEqual(body['content'], 'Резервна відповідь')
        self.assertEqual(calls, ['mock-mini', 'mock-strong'])
        row = json.loads(sql('SELECT row_to_json(r) FROM requests r WHERE request_id = '
                             f"'{body['request_id']}'::uuid"))
        self.assertEqual(row['model'], 'mock-strong')
        self.assertEqual(row['status'], '200')
        self.assertIsNone(row['cost_usd'])  # The failed attempt did not report usage.


if __name__ == '__main__':
    unittest.main()
