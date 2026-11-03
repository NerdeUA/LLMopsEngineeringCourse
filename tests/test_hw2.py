"""HW2 acceptance tests; run against the Docker mock stack with MODEL=mock."""

from decimal import Decimal, ROUND_HALF_UP
import json
import unittest
import uuid

from test_hw1 import api, sql


class RoutingCostTests(unittest.TestCase):
    def logged_chat(self, message):
        status, response = api("/chat", {"message": message})
        self.assertEqual(status, 200)
        row = sql(
            "SELECT row_to_json(r) FROM requests r WHERE request_id = "
            f"'{response['request_id']}'::uuid"
        )
        self.assertTrue(row, "Chat request must have a persisted log row")
        return json.loads(row, parse_float=Decimal)

    def test_01_ordinary_questions_use_mini_in_both_languages(self):
        for message in ("How do I reset my password?", "Як скинути пароль?", "Привіт"):
            with self.subTest(message=message):
                self.assertEqual(self.logged_chat(message)["model"], "mock-mini")

    def test_02_escalations_use_strong_in_both_languages(self):
        for message in (
            "поверніть гроші терміново", "Хочу оформити повернення", "Маю скаргу",
            "ЦЕ ТЕРМІНОВО", "I need a refund", "I want to return my order",
            "I have a complaint", "I am complaining about this", "URGENT: help me log in",
        ):
            with self.subTest(message=message):
                self.assertEqual(self.logged_chat(message)["model"], "mock-strong")

    def test_03_cost_uses_model_input_and_output_rates(self):
        rows = []
        for message, input_rate, output_rate in (
            ("How do I reset my password?", Decimal("0.001"), Decimal("0.002")),
            ("URGENT: How do I reset my password?", Decimal("0.016"), Decimal("0.032")),
        ):
            row = self.logged_chat(message)
            self.assertEqual(row["status"], "200")
            self.assertGreater(row["prompt_tokens"], 0)
            self.assertGreater(row["completion_tokens"], 0)
            self.assertIsNotNone(row["cost_usd"])
            expected = (
                (row["prompt_tokens"] * input_rate + row["completion_tokens"] * output_rate)
                / 1000
            ).quantize(Decimal("0.000001"), rounding=ROUND_HALF_UP)
            self.assertEqual(row["cost_usd"], expected)
            self.assertGreater(row["cost_usd"], 0)
            rows.append(row)
        unit_costs = [r["cost_usd"] / (r["prompt_tokens"] + r["completion_tokens"]) for r in rows]
        self.assertGreater(unit_costs[1] / unit_costs[0], 10)

    def test_04_gateway_failure_without_usage_has_zero_cost(self):
        row = self.logged_chat("__fail_503")
        self.assertEqual(row["status"], "503")
        self.assertEqual(row["model"], "mock-mini")
        self.assertEqual(row["prompt_tokens"], 0)
        self.assertEqual(row["completion_tokens"], 0)
        self.assertEqual(row["cost_usd"], Decimal("0"))

    def test_05_cost_endpoint_matches_database_today(self):
        self.logged_chat("Привіт")
        status, cost = api("/cost")
        self.assertEqual(status, 200)
        self.assertIn("today_usd", cost)
        self.assertIn("budget_usd", cost)
        self.assertIsInstance(cost["today_usd"], (int, float))
        self.assertIsInstance(cost["budget_usd"], (int, float))
        self.assertGreater(cost["today_usd"], 0)
        self.assertEqual(cost["budget_usd"], 10)
        expected = Decimal(sql(
            "SELECT round(COALESCE(SUM(cost_usd), 0), 4) FROM requests "
            "WHERE created_at::date = CURRENT_DATE"
        ))
        self.assertEqual(Decimal(str(cost["today_usd"])), expected)

    def test_06_daily_total_excludes_other_dates_and_handles_null(self):
        fixture_ids = [str(uuid.uuid4()) for _ in range(4)]
        ids_sql = ",".join(f"'{request_id}'::uuid" for request_id in fixture_ids)
        try:
            sql(
                "INSERT INTO requests (request_id, model, created_at, cost_usd) VALUES "
                f"('{fixture_ids[0]}', 'hw2-test', CURRENT_DATE - INTERVAL '1 day', 876.543210),"
                f"('{fixture_ids[1]}', 'hw2-test', CURRENT_DATE + INTERVAL '1 day', 987.654321),"
                f"('{fixture_ids[2]}', 'hw2-test', now(), NULL),"
                f"('{fixture_ids[3]}', 'hw2-test', now(), 1.234567)"
            )
            status, cost = api("/cost")
            self.assertEqual(status, 200)
            self.assertIn("today_usd", cost)
            expected = Decimal(sql(
                "SELECT round(COALESCE(SUM(cost_usd), 0), 4) FROM requests "
                "WHERE created_at::date = CURRENT_DATE"
            ))
            self.assertEqual(Decimal(str(cost["today_usd"])), expected)
        finally:
            sql(f"DELETE FROM requests WHERE request_id IN ({ids_sql})")


if __name__ == "__main__":
    unittest.main(verbosity=2)
