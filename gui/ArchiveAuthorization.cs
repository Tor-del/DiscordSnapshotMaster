using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordSnapshot.Gui;

internal sealed record ArchiveUser(string Id, bool Admin);

internal sealed record ArchiveAuthorization(string ClientId, string ClientSecret, string RedirectUri, ArchiveUser[] Users, string[] Guilds,
    string? SessionVersion = null)
{
    public const string FileName = ".discord-auth.json";
    public const string DefaultRedirect = "http://127.0.0.1:8080/auth/callback";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };
    public static ArchiveAuthorization Empty { get; } = new("", "", DefaultRedirect, [], []);

    public static bool IsEnabled(string directory) => File.Exists(Path.Combine(directory, FileName));
    public static bool IsDiscordId(string value) => Regex.IsMatch(value, @"\A[1-9][0-9]{0,19}\z") && ulong.TryParse(value, out _);
    public static bool IsLoopback(Uri uri) => uri.Host is "127.0.0.1" or "localhost";
    public static bool IsValidRedirect(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || uri.Scheme == "http" && IsLoopback(uri)) &&
        uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 &&
        uri.AbsolutePath == "/auth/callback" && uri.Port > 0 &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        value.StartsWith(uri.GetLeftPart(UriPartial.Authority) + "/", StringComparison.OrdinalIgnoreCase);

    public static ArchiveAuthorization Load(string directory) => Read(Path.Combine(directory, FileName));

    private static ArchiveAuthorization Read(string path)
    {
        var settings = JsonSerializer.Deserialize<ArchiveAuthorization>(File.ReadAllText(path), JsonOptions);
        if (settings is null || settings.Users.Any(user => user is null) || settings.Guilds.Any(guild => guild is null))
            throw new InvalidDataException(Strings.Get("InvalidAuthorizationSettings"));
        return settings;
    }

    public static void SetEnabled(string directory, bool enabled)
    {
        var active = Path.Combine(directory, FileName);
        var disabled = active + ".disabled";
        if (enabled)
        {
            if (File.Exists(disabled))
            {
                ArchiveAuthorization? settings;
                try { settings = Read(disabled); }
                catch (Exception error) when (error is JsonException or InvalidDataException) { settings = null; }
                if (settings is null) File.Move(disabled, active, overwrite: true);
                else
                {
                    settings.Save(directory);
                    File.Delete(disabled);
                }
            }
            else if (!File.Exists(active)) Empty.Save(directory);
        }
        else if (File.Exists(active)) File.Move(active, disabled, overwrite: true);
    }

    public void Save(string directory)
    {
        var path = Path.Combine(directory, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var settings = this with { SessionVersion = Guid.NewGuid().ToString("N") };
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
