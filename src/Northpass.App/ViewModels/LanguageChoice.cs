namespace Northpass.ViewModels;

// Native labels stay recognizable when changing away from an unfamiliar language.
public sealed record LanguageChoice(string Code, string Label)
{
    public static IReadOnlyList<LanguageChoice> All { get; } = Array.AsReadOnly(new[]
    {
        new LanguageChoice("ru", "Русский"), new LanguageChoice("en", "English"), new LanguageChoice("az", "Azərbaycanca")
    });
}
