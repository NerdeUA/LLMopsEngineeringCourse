"""Acceptance tests for Topic 8 masking and human approval."""

import concurrent.futures
import unittest
import uuid
from test_hw1 import api
from test_hw3_cache import chat


class SafetyTests(unittest.TestCase):
    def test_approval_is_atomic_and_result_remains_visible(self):
        response, _ = chat('Хочу повернути гроші, терміново')
        self.assertIn('не створено', response['content'].lower())
        approval_id = response['approval_id']
        status, data = api('/approvals')
        self.assertEqual(status, 200)
        item = next(a for a in data['pending'] if a['id'] == approval_id)
        self.assertEqual(item['action'], 'create_ticket')
        self.assertIsNone(item.get('result'))
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
            results = list(executor.map(lambda _: api(f'/approvals/{approval_id}/approve', {}), range(8)))
        self.assertEqual(sorted(status for status, _ in results), [200] + [404] * 7)
        created = next(data for status, data in results if status == 200)
        self.assertRegex(created['result'], r'(?i)тікет #T-[a-f0-9]+')
        _, data = api('/approvals')
        self.assertFalse(any(a['id'] == approval_id for a in data['pending']))
        self.assertEqual(next(a for a in data['completed'] if a['id'] == approval_id)['result'], created['result'])

    def test_different_emails_share_masked_faq_cache_key(self):
        spaces = ' ' * (uuid.uuid4().int % 10000 + 1)
        _, first = chat('мій email a@b.com, скиньте пароль' + spaces)
        _, second = chat('мій email c@d.net, скиньте пароль' + spaces)
        self.assertGreater(first['prompt_tokens'], 0)
        self.assertEqual(second['prompt_tokens'], 0)
        self.assertEqual(second['cost_usd'], 0)

    def test_unknown_approval_is_not_found(self):
        self.assertEqual(api('/approvals/' + uuid.uuid4().hex + '/approve', {})[0], 404)


if __name__ == '__main__':
    unittest.main()
