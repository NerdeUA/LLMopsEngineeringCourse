# Інцидент: недоступний провайдер

Це навчальний сценарій для mock-стенда. Команди надсилають тестові запити й
додають записи в журнал. Вони не моделюють збій незалежного провайдера: обидва
mock-маршрути працюють через один процес.

## 1. Симптом і вихідний стан

Запустіть `docker compose up --build -d`, дочекайтеся відповіді сервісу й
gateway. В іншому терміналі виконайте команди нижче. Для звичайних портів BASE
можна не задавати; для чистого клону з `demo-ports.yml` задайте
`BASE=http://localhost:8180` перед першою командою.

```sh
BASE=${BASE:-http://localhost:8080}
curl -fsS "$BASE/health"
curl -fsS -X POST "$BASE/chat" -H 'Content-Type: application/json' -d '{"message":"How do I reset my password?"}'
curl -fsS "$BASE/observability"
curl -fsS "$BASE/providers"
```

Нормальна відповідь пояснює відновлення пароля. Запишіть поточні `fallback_events`
та `error_rate_pct`; показники можуть відрізнятися на різних стендах.

## 2. Відтворення та підтвердження

```sh
for i in 1 2 3; do
  curl -fsS -X POST "$BASE/chat" -H 'Content-Type: application/json' -d '{"message":"__fail_503"}'
done
curl -fsS "$BASE/observability"
curl -fsS "$BASE/providers"
docker compose exec -T postgres psql -U llmops -d llmops -c "SELECT created_at, model, status, latency_ms FROM requests ORDER BY created_at DESC LIMIT 5;"
```

Користувач бачить ввічливу заглушку з HTTP 200; у журналі — `503`. Кількість
`fallback_events` збільшується на три, а провайдер після трьох невдалих ланцюгів
переходить у `degraded`. Порівняйте також `error_rate_pct` із вихідним значенням.
У разі іншої назви compose-проєкту виконуйте SQL з директорії відповідного клону
і з тими ж `-f docker-compose.yml -f demo-ports.yml`, які використано для запуску.

## 3. Дія

Переконайтеся, що спрацювали fallback і безпечна заглушка; для цього штучного
збою ручне перемикання не потрібне. Якщо проблема пов'язана зі зміною промпта,
використайте [критерії відкату](docs/release-policy.md). Зафіксуйте час, симптом,
показники та подальші дії для розбору інциденту.

## 4. Перевірка відновлення

```sh
for i in $(seq 1 30); do
  curl -fsS -X POST "$BASE/chat" -H 'Content-Type: application/json' -d "{\"message\":\"Recovery check $i - how do I log in?\"}"
done
curl -fsS "$BASE/observability"
curl -fsS "$BASE/providers"
```

Ці повідомлення не входять до переліку кешованих FAQ: відповідь надходить від
gateway, а не з кешу. Провайдер має повернутися в `ok`; без інших інцидентів
`fallback_events` не збільшується, а денний `error_rate_pct` знижується після
успішних запитів. Через округлення або великий попередній трафік зниження може
стати видимим не одразу. Лічильник fallback накопичувальний і не зменшується.

Кеш зберігає лише успішні відповіді основної моделі, тому заглушки в ньому немає.
Якщо правило кешування зміниться, перевірте його перед відновленням. Сам cache і
лічильники живуть у пам'яті процесу; перезапуск їх очистить, але також видалить
очікувані заявки на підтвердження — не перезапускайте сервіс без потреби.
