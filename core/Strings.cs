using System.Globalization;
using System.Resources;

namespace DiscordSnapshot;

public static class Strings
{
    public const string ProductName = "APEX DSM";

    private static readonly ResourceManager Messages = new("DiscordSnapshot.Core.Messages", typeof(Strings).Assembly);

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public static string Language => Culture.TwoLetterISOLanguageName;

    public static void SetLanguage(string? language) =>
        Culture = CultureInfo.GetCultureInfo(language == "ru" ? "ru-RU" : "en-US");

    public static string Get(string key) =>
        Messages.GetString(key, Culture) ?? throw new MissingManifestResourceException(key);

    public static string Get(string key, params object?[] values) =>
        string.Format(Culture, Get(key), values);

    public static IEnumerable<KeyValuePair<string, string>> WindowResources() =>
        Messages.GetResourceSet(CultureInfo.InvariantCulture, true, false)!
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .Where(key => key.StartsWith("Ui", StringComparison.Ordinal))
            .Select(key => KeyValuePair.Create(key, Get(key)));
}
