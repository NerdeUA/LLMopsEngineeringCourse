# Starter repository

Те, що студент клонує на першому уроці й дороблює до capstone.

**➡ Повний гайд для студента (вимоги, запуск, кожна папка, troubleshooting): [GETTING_STARTED.md](GETTING_STARTED.md).**
**➡ Домашні завдання — окремий файл на кожен тиждень: [homework/](homework/README.md).**

## Що дається готовим, що добудовує студент

| Компонент | Дається готовим | Студент добудовує |
|---|---|---|
| UI (Angular) | готові в'юхи: Chat + Console/Observability | не змінює |
| App service (.NET) | skeleton | control plane: routing, fallback, cost, policies |
| Observability API (.NET) | skeleton | агрегати з Postgres (traces, p95, cost, error-taxonomy) у Console-в'юху |
| Gateway (LiteLLM) | базовий конфіг | моделі/провайдери, порядок fallback |
| Mock provider | готовий | використовує для тестів і failure-сценаріїв |
| Postgres | базова схема | розширює під logs / cost / prompt records |
| Eval runner (Python) | skeleton | dataset, graders, пороги |
| CI (GitHub Actions) | шаблон `.github/workflows/eval-gate.yml` | eval gate |
| Redis / дашборди | опційний шаблон | advanced extension |

## Запуск (мінімальний стек)

```sh
docker compose up --build
```

Піднімаються: UI (`:4200`), сервіс (`:8080`), LiteLLM (`:4000`), Postgres, mock provider.
Для mock реальні ключі не потрібні. Redis і дашборди — опційні: `docker compose --profile advanced up`.
Для оцінки якості (model-based evals): `cp gateway/.env.example gateway/.env` і заповнити ключі.

## Операційні рішення

- Для майбутнього зовнішнього `lookup_order` обрав би timeout 2 секунди, щоб
  повільна залежність не блокувала відповідь; після таймауту сервіс має
  повідомити про недоступність статусу. Поточний mock працює локально, таймаут
  у ньому ще не реалізовано.
- Для дії зі зміною стану повтор із тим самим `request_id` має повертати вже
  записаний результат, а не створювати другий тікет. Новий хід розмови або
  повторне натискання створює інший `request_id`, тому додатково потрібен ключ
  самої дії, наприклад `hash(conversation_id, tool_name, args)`; цю дедуплікацію
  та сховище результатів ще не реалізовано.
- Навчальна черга з Теми 8 захищає повторне підтвердження тієї самої заявки за
  `approval_id` у межах одного процесу та повертає 404 для наступних спроб. Це
  не дедуплікація окремих заявок і не гарантія для зовнішньої системи.
