"""Acceptance tests for the Topic 6 mock tool runtime."""

import unittest
from test_hw3_cache import chat


class ToolTests(unittest.TestCase):
    def test_lookup_order_returns_demo_status_and_records_tool(self):
        for _ in range(2):
            response, row = chat('Де моє замовлення #123?')
            self.assertEqual(response['tool'], 'lookup_order')
            self.assertIn('оплачено', response['content'].lower())
            self.assertIn('доставку призначено', response['content'].lower())
            self.assertGreater(row['prompt_tokens'], 0)
            self.assertEqual(row['tool'], 'lookup_order')

    def test_ticket_request_is_not_executed(self):
        response, row = chat('поверніть гроші терміново')
        self.assertEqual(response['tool'], 'create_ticket')
        self.assertIn('не створено', response['content'].lower())
        self.assertEqual(row['tool'], 'create_ticket')


if __name__ == '__main__':
    unittest.main()
