"""Topic 9 acceptance checks against the Docker stack."""

import json
import subprocess
import time
import unittest
import uuid
from decimal import Decimal
from test_hw1 import OPENER, ROOT, api, sql
from test_hw3_cache import chat


class ObservabilityTests(unittest.TestCase):
    def test_empty_database_has_zero_metrics(self):
        name = 'obs_test_' + uuid.uuid4().hex[:12]
        subprocess.run(['docker', 'compose', 'exec', '-T', 'postgres', 'createdb', '-U', 'llmops', name],
                       cwd=ROOT, capture_output=True, check=True)
        self.addCleanup(lambda: subprocess.run(['docker', 'compose', 'exec', '-T', 'postgres',
                        'dropdb', '-U', 'llmops', name], cwd=ROOT, capture_output=True, check=True))
        subprocess.run(['docker', 'compose', 'exec', '-T', 'postgres', 'psql', '-U', 'llmops', '-d', name,
                        '-f', '/docker-entrypoint-initdb.d/schema.sql'], cwd=ROOT, capture_output=True, check=True)
        container = 'hw5-empty-' + uuid.uuid4().hex[:10]
        subprocess.run(['docker', 'compose', 'run', '--rm', '-d', '--no-deps', '--name', container,
                        '-p', '127.0.0.1::8080', '-e',
                        f'DB_CONN=Host=postgres;Database={name};Username=llmops;Password=llmops', 'service'],
                       cwd=ROOT, capture_output=True, check=True)
        self.addCleanup(lambda: subprocess.run(['docker', 'stop', container], capture_output=True, check=True))
        mapping = subprocess.check_output(['docker', 'port', container, '8080/tcp'], text=True).strip()
        base = 'http://' + mapping.splitlines()[0]
        deadline = time.monotonic() + 20
        while True:
            try:
                with OPENER.open(base + '/observability', timeout=2) as response:
                    metrics = json.load(response)
                break
            except OSError:
                if time.monotonic() > deadline:
                    raise
                time.sleep(.2)
        self.assertEqual(metrics, {'requests': 0, 'p95_ms': 0, 'error_rate_pct': 0,
                                   'cache_hit_pct': 0, 'fallback_events': 0})

    def test_counts_percentile_and_errors_match_database(self):
        chat('Звичайний запит для метрик')
        chat('__fail_503')
        status, metrics = api('/observability')
        self.assertEqual(status, 200)
        row = json.loads(sql("SELECT row_to_json(r) FROM (SELECT count(*) AS requests, "
            "COALESCE(round((percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms))::numeric),0) AS p95_ms, "
            "COALESCE(round(100.0 * count(*) FILTER (WHERE status <> '200') / NULLIF(count(*),0),1),0) AS error_rate_pct "
            "FROM requests WHERE created_at::date=CURRENT_DATE) r"), parse_float=Decimal)
        self.assertEqual(metrics['requests'], row['requests'])
        self.assertLessEqual(abs(metrics['p95_ms'] - row['p95_ms']), 1)
        self.assertEqual(Decimal(str(metrics['error_rate_pct'])), row['error_rate_pct'])
        self.assertGreater(metrics['error_rate_pct'], 0)
        self.assertGreater(metrics['fallback_events'], 0)

    def test_cache_ratio_and_provider_degradation_and_recovery(self):
        question = 'Як змінити пароль?' + ' ' * (uuid.uuid4().int % 10000 + 1)
        chat(question)
        chat(question)
        self.assertGreater(api('/observability')[1]['cache_hit_pct'], 0)
        for _ in range(3):
            chat('__fail_503')
        providers = api('/providers')[1]['providers']
        self.assertEqual(providers[0]['status'], 'degraded')
        chat(question)  # A cache hit does not prove that the provider recovered.
        self.assertEqual(api('/providers')[1]['providers'][0]['status'], 'degraded')
        chat('Тест відновлення провайдера після інциденту')
        self.assertEqual(api('/providers')[1]['providers'][0]['status'], 'ok')


if __name__ == '__main__':
    unittest.main()
