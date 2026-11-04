"""Topic 10 checks for meaningful evals and unusable model output."""

import json
import unittest
from pathlib import Path
from test_hw3_cache import chat


class EvalChecks(unittest.TestCase):
    def test_garbage_is_replaced_without_claiming_provider_outage(self):
        response, row = chat('__garbage')
        self.assertIn('не зміг сформулювати', response['content'].lower())
        self.assertNotIn('тимчасово недоступний', response['content'].lower())
        self.assertEqual(row['status'], '200')

    def test_dataset_keeps_baseline_and_adds_diverse_cases(self):
        path = Path(__file__).resolve().parents[1] / 'evals/golden.jsonl'
        cases = [json.loads(line) for line in path.read_text(encoding='utf-8').splitlines() if line.strip()]
        ids = {case['id'] for case in cases}
        self.assertTrue({'faq-1', 'faq-2', 'order-1', 'refund-1', 'safety-1', 'caps-1'} <= ids)
        new = [case for case in cases if case['id'] not in {'faq-1', 'faq-2', 'order-1', 'refund-1', 'safety-1', 'caps-1'}]
        self.assertGreaterEqual(len(new), 4)
        self.assertGreaterEqual(sum(bool(c.get('forbid') or c.get('expect_refusal')) for c in new), 2)


if __name__ == '__main__':
    unittest.main()
