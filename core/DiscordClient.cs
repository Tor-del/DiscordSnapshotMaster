using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DiscordSnapshot;

public sealed record DiscordServer(string Id, string Name)
{
    public string Label => $"{Name} ({Id})";
}

public sealed record DiscordConnection(string Username, IReadOnlyList<DiscordServer> Servers);
public sealed record ChannelChoice(string Id, string Title, string Kind)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string KindLabel => Kind switch
    {
        "text" or "текст" => Strings.Get("KindText"),
        "voice" or "чат голосового канала" => Strings.Get("KindVoiceChat"),
        "announcement" or "объявления" => Strings.Get("KindAnnouncement"),
        "stage" or "чат трибуны" => Strings.Get("KindStageChat"),
        "forum" or "форум" => Strings.Get("KindForum"),
        "media" or "медиа" => Strings.Get("KindMedia"),
        _ => Kind,
    };
}

public sealed class DiscordClient
{
    internal const ulong ManageThreads = 1UL << 34;
    private const ulong View = 1UL << 10;
    private const ulong History = 1UL << 16;
    private const ulong Connect = 1UL << 20;
    private const ulong Administrator = 1UL << 3;
    private static readonly Uri Api = new("https://discord.com/api/v10/");
    private readonly HttpClient http;
    private readonly AuthenticationHeaderValue authorization;
    private readonly RequestPacer pace = new();
    private string? botId;

    public DiscordClient(string token, HttpClient? http = null)
    {
        var credential = token.Trim();
        if (credential.Length == 0 || credential.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException(Strings.Get("InvalidTokenFormat"));
        authorization = new AuthenticationHeaderValue("Bot", credential);
        this.http = http ?? DiscordHttp.Shared;
    }

    public int IntervalMs { get; set; } = 2000;

    public async Task<DiscordConnection> ConnectAsync(CancellationToken cancellation)
    {
        var bot = await GetAsync("users/@me", cancellation).ConfigureAwait(false);
        if (!bot.Flag("bot")) throw new InvalidOperationException(Strings.Get("EnterBotToken"));
        var application = await GetAsync("applications/@me", cancellation).ConfigureAwait(false);
        if ((application.Get("flags").GetInt32() & ((1 << 18) | (1 << 19))) == 0)
            throw new InvalidOperationException(Strings.Get("MessageContentIntentRequired"));

        var servers = new Dictionary<string, DiscordServer>();
        ulong after = 0;
        while (true)
        {
            var page = await GetAsync($"users/@me/guilds?limit=200&after={after}", cancellation).ConfigureAwait(false);
            foreach (var guild in page.EnumerateArray())
                servers[guild.Id()] = new DiscordServer(guild.Id(), guild.Text("name") ?? "");
            if (page.GetArrayLength() < 200) break;
            var next = page.EnumerateArray().Max(guild => Snowflake.Parse(guild.Id()));
            if (next <= after) throw new InvalidDataException(Strings.Get("RepeatedServerPage"));
            after = next;
        }
        botId = bot.Id();
        return new DiscordConnection(bot.Text("username") ?? botId, servers.Values.ToArray());
    }

    public async Task<IReadOnlyList<ChannelChoice>> LoadChannelsAsync(string guildId, CancellationToken cancellation)
    {
        var selection = await LoadGuildAsync(guildId, cancellation).ConfigureAwait(false);
        var categories = selection.Channels.Where(channel => channel.Type == 4).ToDictionary(channel => channel.Id);
        return selection.Readable.Select(channel =>
        {
            var title = channel.Name;
            if (channel.ParentId is { } parent && categories.TryGetValue(parent, out var category)) title = $"{category.Name} / {title}";
            return new ChannelChoice(channel.Id, title, channel.Kind!);
        }).ToArray();
    }

    internal async Task<GuildSelection> LoadGuildAsync(string guildId, CancellationToken cancellation)
    {
        Snowflake.Parse(guildId);
        if (botId is null) throw new InvalidOperationException(Strings.Get("BotConnectionRequired"));
        var guild = await GetAsync($"guilds/{guildId}", cancellation).ConfigureAwait(false);
        var member = await GetAsync($"guilds/{guildId}/members/{botId}", cancellation).ConfigureAwait(false);
        var response = await GetAsync($"guilds/{guildId}/channels", cancellation).ConfigureAwait(false);
        var channels = response.EnumerateArray().Select(DiscordChannel.FromJson).ToArray();
        var roles = member.Array("roles").Select(role => role.GetString()).ToHashSet();
        ulong basePermissions = 0;
        foreach (var role in guild.Array("roles"))
            if (role.Id() == guildId || roles.Contains(role.Id())) basePermissions |= Snowflake.Parse(role.Text("permissions")!);

        var permissions = new Dictionary<string, ulong>();
        foreach (var channel in channels)
        {
            permissions[channel.Id] = guild.Text("owner_id") == botId || (basePermissions & Administrator) != 0
                ? ulong.MaxValue : ApplyOverwrites(channel, guildId, botId, roles, basePermissions);
        }
        return new GuildSelection(guild, channels, permissions);
    }

    private static ulong ApplyOverwrites(DiscordChannel channel, string guildId, string userId, HashSet<string?> roles, ulong permissions)
    {
        var overwrites = channel.Data.Array("permission_overwrites").ToArray();
        var everyone = overwrites.FirstOrDefault(entry => entry.Id() == guildId);
        permissions = ApplyOverwrite(permissions, everyone);
        ulong deny = 0, allow = 0;
        foreach (var entry in overwrites.Where(entry => entry.Get("type").GetInt32() == 0 && roles.Contains(entry.Id())))
        {
            deny |= Snowflake.Parse(entry.Text("deny")!);
            allow |= Snowflake.Parse(entry.Text("allow")!);
        }
        var own = overwrites.FirstOrDefault(entry => entry.Get("type").GetInt32() == 1 && entry.Id() == userId);
        return ApplyOverwrite((permissions & ~deny) | allow, own);
    }

    private static ulong ApplyOverwrite(ulong permissions, JsonElement entry) => entry.ValueKind == JsonValueKind.Undefined
        ? permissions : (permissions & ~Snowflake.Parse(entry.Text("deny")!)) | Snowflake.Parse(entry.Text("allow")!);

    internal static bool CanRead(ulong permissions, int channelType)
    {
        var required = View | History;
        if (channelType is 2 or 13) required |= Connect;
        return (permissions & required) == required;
    }

    internal async IAsyncEnumerable<DiscordChannel> ArchivedThreads(string channelId, string kind,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var route = kind == "joined" ? $"channels/{channelId}/users/@me/threads/archived/private" : $"channels/{channelId}/threads/archived/{kind}";
        string? before = null;
        while (true)
        {
            var query = before is null ? "limit=100" : $"limit=100&before={Uri.EscapeDataString(before)}";
            var page = await GetAsync($"{route}?{query}", cancellation).ConfigureAwait(false);
            JsonElement last = default;
            foreach (var thread in page.Array("threads"))
            {
                last = thread;
                yield return DiscordChannel.FromJson(thread);
            }
            if (!page.Flag("has_more")) yield break;
            var next = kind == "joined" ? last.Text("id") : last.Get("thread_metadata").Text("archive_timestamp");
            if (next is null || next == before) throw new InvalidDataException(Strings.Get("ThreadListReadFailed", channelId));
            before = next;
        }
    }

    internal async Task<JsonElement> GetAsync(string path, CancellationToken cancellation)
    {
        for (var attempt = 0; ; attempt++)
        {
            await pace.WaitAsync(IntervalMs, cancellation).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Api, path));
            request.Headers.Authorization = authorization;
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (DiscordHttp.Header(response, "X-RateLimit-Remaining") == "0" && DiscordHttp.HeaderSeconds(response, "X-RateLimit-Reset-After") is { } reset)
                    pace.Block(reset + 0.1);
                var status = (int)response.StatusCode;
                if (status == 429)
                {
                    var retry = DiscordHttp.RetryAfter(response);
                    pace.Block((retry ?? 2) + 0.1);
                    if (retry is null)
                    {
                        try
                        {
                            using var body = await DiscordHttp.ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
                            if (body.RootElement.Get("retry_after") is { ValueKind: JsonValueKind.Number } seconds)
                                pace.Block(seconds.GetDouble() + 0.1);
                        }
                        catch (JsonException) { }
                    }
                    if (attempt < 8) continue;
                }
                if (response.IsSuccessStatusCode)
                {
                    using var json = await DiscordHttp.ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
                    return json.RootElement.Clone();
                }
                if (status < 500 || attempt >= 4) throw new DiscordRequestException(status, path, false);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
            {
                cancellation.ThrowIfCancellationRequested();
                if (attempt >= 4) throw new InvalidOperationException(Strings.Get("DiscordUnavailable", path));
            }
            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellation).ConfigureAwait(false);
        }
    }
}

internal sealed record DiscordChannel(string Id, string? ParentId, string Name, int Type, JsonElement Data)
{
    public static DiscordChannel FromJson(JsonElement data) => new(data.Id(), data.Text("parent_id"), data.Text("name") ?? "", data.Get("type").GetInt32(), data);
    public bool Container => Type is 15 or 16;
    public bool HasThreads => Type is 0 or 5 or 15 or 16;
    public string? Kind => Type switch
    {
        0 => "text", 2 => "voice", 5 => "announcement", 13 => "stage", 15 => "forum", 16 => "media", _ => null,
    };
}

internal sealed record GuildSelection(JsonElement Guild, DiscordChannel[] Channels, Dictionary<string, ulong> Permissions)
{
    public IEnumerable<DiscordChannel> Readable => Channels.Where(channel => channel.Kind is not null && DiscordClient.CanRead(Permissions[channel.Id], channel.Type));
}

internal static class DiscordJson
{
    public static JsonElement Get(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    public static string? Text(this JsonElement value, string name) => value.Get(name) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;
    public static string Id(this JsonElement value) => value.Text("id") ?? throw new InvalidDataException(Strings.Get("DiscordIdMissing"));
    public static bool Flag(this JsonElement value, string name) => value.Get(name).ValueKind == JsonValueKind.True;
    public static IEnumerable<JsonElement> Array(this JsonElement value, string name) => value.Get(name) is { ValueKind: JsonValueKind.Array } field ? field.EnumerateArray() : [];
}

internal static class Snowflake
{
    public static ulong Parse(string value) => ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    public static string Cutoff(DateTimeOffset now) => ((now.ToUnixTimeMilliseconds() - 1420070400000L) << 22).ToString(CultureInfo.InvariantCulture);
}

internal sealed class DiscordRequestException(int status, string path, bool cdn) : Exception(status switch
{
    401 when !cdn => Strings.Get("DiscordTokenRejected"),
    429 => Strings.Get("RateLimitRepeated", cdn ? "CDN" : "Discord", path),
    _ => $"{(cdn ? "CDN" : "Discord")} HTTP {status} ({path})",
})
{
    public int Status { get; } = status;
}

internal static class DiscordHttp
{
    public static readonly HttpClient Shared = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static string? Header(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    public static double? HeaderSeconds(HttpResponseMessage response, string name) =>
        double.TryParse(Header(response, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : null;
    public static double? RetryAfter(HttpResponseMessage response) => HeaderSeconds(response, "Retry-After")
        ?? (response.Headers.RetryAfter?.Date is { } date ? Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds) : null);

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);
    }
}

internal sealed class RequestPacer
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double last = double.NegativeInfinity;
    private double blockedUntil;

    public void Block(double seconds)
    {
        if (double.IsFinite(seconds) && seconds >= 0) blockedUntil = Math.Max(blockedUntil, clock.Elapsed.TotalMilliseconds + seconds * 1000);
    }

    public async Task WaitAsync(int interval, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var deadline = Math.Max(last + interval, blockedUntil);
        while (deadline > clock.Elapsed.TotalMilliseconds)
        {
            var delay = Math.Clamp(Math.Ceiling(deadline - clock.Elapsed.TotalMilliseconds), 1, int.MaxValue);
            await Task.Delay((int)delay, cancellation).ConfigureAwait(false);
        }
        last = clock.Elapsed.TotalMilliseconds;
    }
}
