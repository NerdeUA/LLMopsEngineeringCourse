"""Topic 5 cache acceptance tests against the Docker mock stack."""

from decimal import Decimal
import json
import unittest
import uuid
from test_hw1 import api, sql


def chat(message):
    status, response = api('/chat', {'message': message})
    assert status == 200
    row = sql("SELECT row_to_json(r) FROM requests r WHERE request_id = "
              f"'{response['request_id']}'::uuid")
    return response, json.loads(row, parse_float=Decimal)


class CacheTests(unittest.TestCase):
    def test_public_faq_hit_is_logged_with_zero_usage(self):
        question = 'Як змінити пароль?' + ' ' * (uuid.uuid4().int % 10000 + 1)
        _, first = chat(question)
        _, second = chat(question)
        self.assertGreater(first['prompt_tokens'], 0)
        self.assertEqual(second['prompt_tokens'], 0)
        self.assertEqual(second['completion_tokens'], 0)
        self.assertEqual(second['cost_usd'], Decimal('0'))

    def test_prompt_change_causes_miss(self):
        question = 'Як відновити пароль?' + ' ' * (uuid.uuid4().int % 10000 + 1)
        api('/prompts/v2/activate', {})
        try:
            _, first = chat(question)
            _, cached = chat(question)
            self.assertEqual(cached['prompt_tokens'], 0)
            api('/prompts/v1/activate', {})
            response, changed = chat(question)
            self.assertGreater(changed['prompt_tokens'], 0)
            self.assertEqual(changed['prompt_version'], 'v1')
            self.assertEqual(response['content'], 'не знаю')
        finally:
            api('/prompts/v2/activate', {})

    def test_personal_order_question_never_cached(self):
        for _ in range(2):
            _, row = chat('Де моє замовлення #10482?')
            self.assertGreater(row['prompt_tokens'], 0)

    def test_failure_never_cached(self):
        for _ in range(2):
            _, row = chat('__fail_503')
            self.assertEqual(row['status'], '503')
            self.assertIsNone(row['cost_usd'])


if __name__ == '__main__':
    unittest.main()
