// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — роутинг між mock-mini/mock-strong, грошей не треба.
// Явний MODEL обирає одну модель; її реальний тариф треба додати в ModelPolicy.

using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

// налаштування беремо з оточення (задаються в docker-compose.yml)
var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";
var dailyBudgetUsd = builder.Configuration.GetValue<decimal>("DAILY_BUDGET_USD", 5m);
if (dailyBudgetUsd < 0)
    throw new InvalidOperationException("DAILY_BUDGET_USD must be non-negative");
var responseCache = new ConcurrentDictionary<(string Model, string Prompt, string Message), string>();
long cacheHits = 0, cacheMisses = 0;
long fallbackEvents = 0;

// Additive migration for existing volumes; /chat still reports a registry outage normally.
try
{
    await using var migrationDb = new NpgsqlConnection(dbConn);
    await migrationDb.OpenAsync();
    await using var migration = new NpgsqlCommand("ALTER TABLE requests ADD COLUMN IF NOT EXISTS tool TEXT", migrationDb);
    await migration.ExecuteNonQueryAsync();
}
catch (NpgsqlException ex)
{
    app.Logger.LogWarning(ex, "Could not apply requests.tool migration; restart after database recovery");
}

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): тут перевірити вхід на PII / інʼєкції. поки нічого.
    // TODO(student, W4)

    // Звичайні питання -> mock-mini; ескалації UK/EN -> mock-strong.
    var model = ModelPolicy.SelectModel(body.Message, defaultModel);

    // Текст і версія читаються разом: лог описує саме використаний промпт.
    string systemPrompt, promptVersion;
    try
    {
        (systemPrompt, promptVersion) = await ReadActivePrompt(dbConn, app.Logger);
    }
    catch (NpgsqlException)
    {
        // Інфраструктурний збій не є порожнім реєстром: БД не зможе зберегти і лог.
        app.Logger.LogError("Prompt registry unavailable for request {RequestId}", requestId);
        return Results.Problem(statusCode: 503, title: "Prompt registry unavailable");
    }

    // Лише перевірені публічні FAQ. Персональні або невідомі запити оминають кеш.
    var cacheable = PublicFaqCache.IsEligible(body.Message);
    var cacheKey = (model, systemPrompt, body.Message);
    if (cacheable && responseCache.TryGetValue(cacheKey, out var cachedAnswer))
    {
        var hit = Interlocked.Increment(ref cacheHits);
        app.Logger.LogInformation("Response cache hit; hits={Hits}, misses={Misses}", hit, Interlocked.Read(ref cacheMisses));
        var cachedLatency = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        await LogRequest(dbConn, requestId, model, promptVersion, cachedLatency, 0, 0, 0m, 200, null);
        return Results.Json(new { request_id = requestId, content = cachedAnswer, tool = (string?)null, latency_ms = cachedLatency });
    }
    if (cacheable)
    {
        var miss = Interlocked.Increment(ref cacheMisses);
        app.Logger.LogInformation("Response cache miss; hits={Hits}, misses={Misses}", Interlocked.Read(ref cacheHits), miss);
    }

    var http = httpFactory.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(5);
    var selectedModel = model;
    var chain = defaultModel == "mock"
        ? new[] { model, model == "mock-strong" ? "mock-mini" : "mock-strong" }
        : new[] { model };
    GatewayResult result = new(false, "", null, false, 0, 0, false, 0);
    var priorUsageUnknown = false;
    for (var i = 0; i < chain.Length; i++)
    {
        if (i > 0)
        {
            var count = Interlocked.Increment(ref fallbackEvents);
            app.Logger.LogWarning("Fallback for request {RequestId}: {PreviousModel} -> {NextModel}; transitions={Count}",
                requestId, chain[i - 1], chain[i], count);
        }
        result = await CallGateway(http, gateway, chain[i], systemPrompt, body.Message);
        if (result.Ok)
        {
            model = chain[i];
            break;
        }
        if (!result.HasUsage) priorUsageUnknown = true;
        // Перемикаємо маршрут лише для тимчасових збоїв, а не помилок запиту.
        if (result.Status != 0 && result.Status != 429 && result.Status < 500) break;
    }
    if (!result.Ok) model = selectedModel;
    var answer = result.Ok ? result.Answer : "Сервіс тимчасово недоступний. Спробуйте пізніше.";
    string? toolCall = null;
    int promptTokens = result.PromptTokens, completionTokens = result.CompletionTokens, status = result.Status;
    var hasUsage = result.HasUsage;
    var hasToolCalls = result.HasToolCalls;
    if (result.Ok && hasToolCalls)
    {
        toolCall = result.Tool;
        if (toolCall == "lookup_order")
            answer += " (Навчальний приклад: статус: оплачено, доставку призначено.)";
        else if (toolCall == "create_ticket")
            answer = "Тікет не створено: ця дія потребує підтвердження людини.";
        else
            answer = "Запитаний інструмент недоступний.";
        app.Logger.LogInformation("Request {RequestId} requested tool {Tool}; execution={ToolResult}",
            requestId, toolCall, toolCall == "lookup_order" ? "mock_read" : "not_executed");
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
    if (cacheable && result.Ok && model == selectedModel && status == 200 && !hasToolCalls && !string.IsNullOrWhiteSpace(answer))
        responseCache.TryAdd(cacheKey, answer);

    var costUsd = hasUsage && !priorUsageUnknown ? ModelPolicy.CalculateCost(model, promptTokens, completionTokens) : null;
    if (!hasUsage || priorUsageUnknown)
        app.Logger.LogWarning("No usage returned for request {RequestId}; cost_usd remains unknown", requestId);
    else if (costUsd is null)
        app.Logger.LogWarning("No price configured for model {Model}; cost_usd remains unknown", model);

    // лог кожного запиту — з цього живе observability (W1) і cost (W2)
    await LogRequest(dbConn, requestId, model, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, status, toolCall);

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

static async Task<GatewayResult> CallGateway(HttpClient http, string gateway, string model, string systemPrompt, string userMessage)
{
    var payload = JsonSerializer.Serialize(new
    {
        model,
        messages = new object[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userMessage } }
    });
    try
    {
        using var response = await http.PostAsync($"{gateway}/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
            return new(false, "", null, false, 0, 0, false, status);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var pt = 0;
        var ct = 0;
        var hasUsage = doc.RootElement.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("prompt_tokens", out var promptUsage)
            && promptUsage.TryGetInt32(out pt)
            && usage.TryGetProperty("completion_tokens", out var completionUsage)
            && completionUsage.TryGetInt32(out ct);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var answer = message.GetProperty("content").GetString() ?? "";
        var hasTools = message.TryGetProperty("tool_calls", out var tools)
            && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0;
        var tool = hasTools ? tools[0].GetProperty("function").GetProperty("name").GetString() : null;
        return new(true, answer, tool, hasTools, pt, ct, hasUsage, status);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
    {
        return new(false, "", null, false, 0, 0, false, 0);
    }
}

static async Task<(string Body, string Version)> ReadActivePrompt(string conn, ILogger logger)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT body, version FROM prompts WHERE name = 'support' AND active", db);
    await using var rows = await cmd.ExecuteReaderAsync();
    if (await rows.ReadAsync())
        return (rows.GetString(0), rows.GetString(1));
    logger.LogWarning("No active support prompt; using fallback prompt (version none)");
    return ("You are an assistant.", "none");
}

// пише один рядок у requests. якщо лог впав — запит користувача все одно віддаємо.
static async Task LogRequest(string conn, Guid id, string model, string promptVersion, int latency,
    int promptTokens, int completionTokens, decimal? cost, int status, string? tool)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO requests (request_id, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status, tool) "
            + "VALUES (@id, @model, @version, @lat, @pt, @ct, @cost, @status, @tool)", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("version", promptVersion);
        cmd.Parameters.AddWithValue("lat", latency);
        cmd.Parameters.AddWithValue("pt", promptTokens);
        cmd.Parameters.AddWithValue("ct", completionTokens);
        cmd.Parameters.AddWithValue("cost", (object?)cost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", status.ToString());
        cmd.Parameters.AddWithValue("tool", NpgsqlTypes.NpgsqlDbType.Text, (object?)tool ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* не валимо запит через лог */ }
}

record ChatIn(string Message);
record GatewayResult(bool Ok, string Answer, string? Tool, bool HasToolCalls, int PromptTokens,
    int CompletionTokens, bool HasUsage, int Status);
