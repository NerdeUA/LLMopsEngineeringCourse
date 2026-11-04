// Лише повідомлення, які явно визначено як публічні й незалежні від стану користувача.
static class PublicFaqCache
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Як скинути пароль?", "Як змінити пароль?", "Як відновити пароль?",
        "How do I reset my password?", "Що ти можеш?", "Привіт",
        "мій email [email], скиньте пароль"
    };

    public static bool IsEligible(string? message) => message is not null && Allowed.Contains(message.Trim());
}
