// Навчальні тарифи mock у USD за 1,000 токенів; це не ціни реальних провайдерів.
static class ModelPolicy
{
    private static readonly string[] EscalationMarkers =
        ["поверн", "скарг", "термінов", "refund", "return", "complain", "urgent"];

    private static readonly IReadOnlyDictionary<string, (decimal Input, decimal Output)> Prices =
        new Dictionary<string, (decimal Input, decimal Output)>(StringComparer.Ordinal)
        {
            ["mock"] = (0.001m, 0.002m),
            ["mock-mini"] = (0.001m, 0.002m),
            ["mock-strong"] = (0.016m, 0.032m)
        };

    public static string SelectModel(string? message, string configuredModel)
    {
        // Явний MODEL зберігає ручний вибір моделі зі стартера; mock вмикає роутинг.
        if (configuredModel != "mock") return configuredModel;

        // Запланований fallback chain (виконання — HW4):
        // mock-strong -> mock-mini -> заглушка; mock-mini -> mock-strong -> заглушка.
        // За 80% денного бюджету: записати попередження в лог; автоматичну реакцію не реалізовано.
        return EscalationMarkers.Any(marker =>
            (message ?? "").Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? "mock-strong" : "mock-mini";
    }

    public static decimal? CalculateCost(string model, int promptTokens, int completionTokens)
    {
        // Невідомий тариф — NULL, а не вигадана ціна чи безкоштовний виклик.
        if (!Prices.TryGetValue(model, out var price)) return null;
        var cost = (promptTokens * price.Input + completionTokens * price.Output) / 1000m;
        // Та сама точність, що в requests.cost_usd NUMERIC(10, 6).
        return Math.Round(cost, 6, MidpointRounding.AwayFromZero);
    }
}
