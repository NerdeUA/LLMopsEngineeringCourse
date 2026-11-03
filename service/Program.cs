// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — роутинг між mock-mini/mock-strong, грошей не треба.
// Явний MODEL обирає одну модель; її реальний тариф треба додати в ModelPolicy.

using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

// налаштування беремо з оточення (задаються в docker-compose.yml)
var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";
var dailyBudgetUsd = builder.Configuration.GetValue<decimal>("DAILY_BUDGET_USD", 10m);
if (dailyBudgetUsd < 0)
    throw new InvalidOperationException("DAILY_BUDGET_USD must be non-negative");

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): тут перевірити вхід на PII / інʼєкції. поки нічого.
    // TODO(student, W4)

    // Звичайні питання -> mock-mini; ескалації UK/EN -> mock-strong.
    var model = ModelPolicy.SelectModel(body.Message, defaultModel);

    // Текст і версія читаються разом: лог описує саме використаний промпт.
    var (systemPrompt, promptVersion) = await ReadActivePrompt(dbConn, app.Logger);

    // cache (W3): перед викликом глянути в Redis — раптом вже відповідали
    // TODO(student, W3)

    // fallback (W4): якщо тут 429/5xx — піти на іншого провайдера. поки один виклик.
    // TODO(student, W4)
    var payload = JsonSerializer.Serialize(new
    {
        model,
        messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = body.Message }
        }
    });

    var http = httpFactory.CreateClient();
    var answer = "";
    string? toolCall = null;
    int promptTokens = 0, completionTokens = 0, status = 0; // 0 = відповіді не було
    try
    {
        var response = await http.PostAsync(
            $"{gateway}/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        status = (int)response.StatusCode;
        var rawJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(rawJson);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        answer = message.GetProperty("content").GetString() ?? "";

        // tools + HITL (W3/W4): якщо модель попросила інструмент — виконати;
        // перед незворотною дією (створити тікет) спитати людину. поки лише читаємо назву.
        // TODO(student, W3/W4)
        if (message.TryGetProperty("tool_calls", out var tools)
            && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
        {
            toolCall = tools[0].GetProperty("function").GetProperty("name").GetString();
        }

        var usage = doc.RootElement.GetProperty("usage");
        promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
        completionTokens = usage.GetProperty("completion_tokens").GetInt32();
    }
    catch
    {
        // мережа/gateway недоступні або відповідь не розпарсилась (status лишиться 0/5xx).
        // TODO(student, W4): тут краще graceful degradation
        answer = "Сервіс тимчасово недоступний.";
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    var costUsd = ModelPolicy.CalculateCost(model, promptTokens, completionTokens);
    if (costUsd is null)
        app.Logger.LogWarning("No price configured for model {Model}; cost_usd remains unknown", model);

    // лог кожного запиту — з цього живе observability (W1) і cost (W2)
    await LogRequest(dbConn, requestId, model, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, status);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

// ці ендпоінти читає готова консоль. поверни потрібну форму — картки оживуть.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));                                    // ліфнес, не для консолі
app.MapGet("/observability", () => Results.Json(new { todo = "aggregate from requests table" }));  // W5: { p95_ms, requests, cache_hit_pct, error_rate_pct, fallback_events }
app.MapGet("/cost", async () =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();
    // Межі дня визначає Postgres, як і CURRENT_DATE у SQL перевірки домашки.
    await using var cmd = new NpgsqlCommand(
        "SELECT round(COALESCE(SUM(cost_usd), 0), 4) FROM requests "
        + "WHERE created_at >= CURRENT_DATE AND created_at < CURRENT_DATE + INTERVAL '1 day'", db);
    var todayUsd = (decimal)(await cmd.ExecuteScalarAsync())!;
    return Results.Json(new { today_usd = todayUsd, budget_usd = dailyBudgetUsd });
});
app.MapGet("/prompts", async () =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT name, version, active FROM prompts ORDER BY name, version", db);
    await using var rows = await cmd.ExecuteReaderAsync();
    var prompts = new List<object>();
    while (await rows.ReadAsync())
        prompts.Add(new { name = rows.GetString(0), version = rows.GetString(1), active = rows.GetBoolean(2) });
    return Results.Json(prompts);
});

app.MapPost("/prompts/{version}/activate", async (string version) =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();
    await using var tx = await db.BeginTransactionAsync();

    // Серіалізуємо перемикання; звичайний SELECT у чаті не блокується.
    await using var tableLock = new NpgsqlCommand(
        "LOCK TABLE prompts IN SHARE ROW EXCLUSIVE MODE", db, tx);
    await tableLock.ExecuteNonQueryAsync();

    await using var exists = new NpgsqlCommand(
        "SELECT EXISTS (SELECT 1 FROM prompts WHERE name = 'support' AND version = @version)", db, tx);
    exists.Parameters.AddWithValue("version", version);
    if (!(bool)(await exists.ExecuteScalarAsync())!)
        return Results.NotFound(new { error = "Prompt version not found" });

    await using var deactivate = new NpgsqlCommand(
        "UPDATE prompts SET active = false WHERE name = 'support' AND active", db, tx);
    await deactivate.ExecuteNonQueryAsync();
    await using var activate = new NpgsqlCommand(
        "UPDATE prompts SET active = true WHERE name = 'support' AND version = @version", db, tx);
    activate.Parameters.AddWithValue("version", version);
    await activate.ExecuteNonQueryAsync();
    await tx.CommitAsync();
    return Results.Ok(new { name = "support", version, active = true });
});
app.MapGet("/providers", () => Results.Json(new { todo = "provider health" }));                    // W5: { providers: [ { name, status } ] }
app.MapGet("/approvals", () => Results.Json(new { todo = "pending HITL approvals" }));              // W4: { pending: [ { id, action } ] }

app.Run("http://0.0.0.0:8080");

static async Task<(string Body, string Version)> ReadActivePrompt(string conn, ILogger logger)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT body, version FROM prompts WHERE name = 'support' AND active", db);
        await using var rows = await cmd.ExecuteReaderAsync();
        if (await rows.ReadAsync())
            return (rows.GetString(0), rows.GetString(1));
        logger.LogWarning("No active support prompt; using fallback prompt (version none)");
    }
    catch (NpgsqlException)
    {
        logger.LogWarning("Prompt registry unavailable; using fallback prompt (version none)");
    }
    return ("You are an assistant.", "none");
}

// пише один рядок у requests. якщо лог впав — запит користувача все одно віддаємо.
static async Task LogRequest(string conn, Guid id, string model, string promptVersion, int latency,
    int promptTokens, int completionTokens, decimal? cost, int status)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO requests (request_id, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status) "
            + "VALUES (@id, @model, @version, @lat, @pt, @ct, @cost, @status)", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("version", promptVersion);
        cmd.Parameters.AddWithValue("lat", latency);
        cmd.Parameters.AddWithValue("pt", promptTokens);
        cmd.Parameters.AddWithValue("ct", completionTokens);
        cmd.Parameters.AddWithValue("cost", (object?)cost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", status.ToString());
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* не валимо запит через лог */ }
}

record ChatIn(string Message);
