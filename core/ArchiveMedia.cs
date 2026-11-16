using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordSnapshot;

internal sealed record MediaAsset(string Id, string Kind, string Url, string Path, long? ExpectedBytes);

internal sealed partial class ArchiveMedia(HttpClient http)
{
    private const string Cdn = "https://cdn.discordapp.com";
    private const string Media = "https://media.discordapp.net";
    private readonly RequestPacer pace = new();

    [GeneratedRegex(@"<(a?):[^:<>]+:(\d+)>")]
    private static partial Regex CustomEmoji();

    [GeneratedRegex(@"\A\.[a-z0-9]{1,8}\z")]
    private static partial Regex SafeExtension();

    [GeneratedRegex(@"\Amedia/[a-f0-9]{64}(\.[a-z0-9]{1,8})?\z")]
    private static partial Regex SafeRelativePath();

    private static MediaAsset Asset(string kind, string url, string key, long? size = null)
    {
        var suffix = Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? System.IO.Path.GetExtension(parsed.AbsolutePath).ToLowerInvariant() : "";
        var extension = SafeExtension().IsMatch(suffix) ? suffix : "";
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{key}")));
        return new MediaAsset(id, kind, url, $"media/{id}{extension}", size);
    }

    private static bool IsDiscordCdn(Uri? url) => url is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 }
        && url.Host is "cdn.discordapp.com" or "media.discordapp.net";

    public static string LocalPath(string directory, string relative)
    {
        if (!SafeRelativePath().IsMatch(relative)) throw new InvalidDataException(Strings.Get("InvalidArchivePath"));
        return System.IO.Path.Combine(directory, relative);
    }

    public static MediaAsset Avatar(JsonElement user, JsonElement member = default, string? guildId = null)
    {
        var userId = user.Id();
        var memberHash = member.Text("avatar");
        var hash = memberHash ?? user.Text("avatar");
        string url;
        if (!string.IsNullOrEmpty(hash))
        {
            var path = memberHash is null ? $"/avatars/{userId}/{hash}" : $"/guilds/{guildId}/users/{userId}/avatars/{hash}";
            url = $"{Cdn}{path}.{ImageExtension(hash)}?size=256";
        }
        else
        {
            var index = uint.TryParse(user.Text("discriminator"), out var discriminator) && discriminator != 0
                ? discriminator % 5 : (Snowflake.Parse(userId) >> 22) % 6;
            url = $"{Cdn}/embed/avatars/{index}.png";
        }
        return Asset("avatar", url, url);
    }

    private static string ImageExtension(string hash) => hash.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "webp";

    public static MediaAsset? GuildIcon(JsonElement guild)
    {
        if (guild.Text("icon") is not { Length: > 0 } hash) return null;
        var url = $"{Cdn}/icons/{guild.Id()}/{hash}.{ImageExtension(hash)}?size=256";
        return Asset("guild_icon", url, url);
    }

    public static Dictionary<string, MediaAsset> ForMessage(JsonElement message)
    {
        var assets = new Dictionary<string, MediaAsset>();
        void Add(MediaAsset asset) => assets[asset.Id] = asset;
        void AddCdn(string kind, string? value)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var url) && IsDiscordCdn(url))
                Add(Asset(kind, url.AbsoluteUri, url.GetLeftPart(UriPartial.Path)));
        }
        void Visit(JsonElement data)
        {
            foreach (var file in data.Array("attachments"))
            {
                var size = file.Get("size") is { ValueKind: JsonValueKind.Number } field ? field.GetInt64() : (long?)null;
                Add(Asset("attachment", file.Text("url") ?? "", file.Id(), size));
            }
            foreach (Match match in CustomEmoji().Matches(data.Text("content") ?? ""))
            {
                var extension = match.Groups[1].Length > 0 ? "gif" : "webp";
                AddCdn("emoji", $"{Cdn}/emojis/{match.Groups[2].Value}.{extension}");
            }
            foreach (var reaction in data.Array("reactions"))
            {
                var emoji = reaction.Get("emoji");
                if (emoji.Text("id") is { } id)
                    AddCdn("emoji", $"{Cdn}/emojis/{id}.{(emoji.Flag("animated") ? "gif" : "webp")}");
            }
            var stickers = data.Get("sticker_items").ValueKind == JsonValueKind.Array ? "sticker_items" : "stickers";
            foreach (var sticker in data.Array(stickers))
            {
                var format = sticker.Get("format_type").GetInt32();
                var extension = format switch { 1 or 2 => "png", 3 => "json", 4 => "gif", _ => null };
                if (extension is not null)
                    AddCdn("sticker", $"{(format == 4 ? Media : Cdn)}/stickers/{sticker.Id()}.{extension}");
            }
            foreach (var embed in data.Array("embeds"))
            {
                foreach (var property in new[] { "image", "thumbnail", "video", "author", "footer" })
                {
                    var part = embed.Get(property);
                    AddCdn("embed", part.Text("url"));
                    AddCdn("embed", part.Text("icon_url"));
                }
            }
            foreach (var snapshot in data.Array("message_snapshots")) Visit(snapshot.Get("message"));
        }
        Visit(message);
        return assets;
    }

    public async Task<(long Bytes, string Sha256)> DownloadAsync(MediaAsset asset, string directory, int interval, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var url) || !IsDiscordCdn(url))
            throw new InvalidDataException(Strings.Get("OutsideDiscordCdn"));
        var destination = LocalPath(directory, asset.Path);
        var temporary = destination + ".part";
        for (var attempt = 0; ; attempt++)
        {
            await pace.WaitAsync(interval, cancellation).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status == 429) pace.Block(DiscordHttp.RetryAfter(response) ?? 2);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt >= 2 || (status < 500 && status != 429)) throw new DiscordRequestException(status, asset.Path, true);
                }
                else
                {
                    var result = await SaveAsync(response, temporary, asset.ExpectedBytes, timeout.Token).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, overwrite: true);
                    return result;
                }
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidDataException)
            {
                cancellation.ThrowIfCancellationRequested();
                if (attempt >= 2)
                {
                    if (error is InvalidDataException) throw;
                    throw new InvalidOperationException(Strings.Get("CdnDownloadFailed"));
                }
            }
            finally { File.Delete(temporary); }
            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellation).ConfigureAwait(false);
        }
    }

    private static async Task<(long, string)> SaveAsync(HttpResponseMessage response, string temporary, long? expected, CancellationToken cancellation)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long size = 0;
        while (true)
        {
            int count;
            try { count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false); }
            catch (IOException error) { throw new HttpRequestException(Strings.Get("CdnReadFailed"), error); }
            if (count == 0) break;
            size += count;
            if (size > expected) throw new InvalidDataException(Strings.Get("UnexpectedFileSize"));
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
        }
        if (expected is not null && size != expected) throw new InvalidDataException(Strings.Get("UnexpectedFileSize"));
        output.Flush(flushToDisk: true);
        return (size, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
