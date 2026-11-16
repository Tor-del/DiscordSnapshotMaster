using System.Globalization;
using System.Text.Json;

namespace DiscordSnapshot;

public sealed record ImportResult(string Directory, bool Incomplete);
public sealed record ArchiveCheck(bool Incomplete, int DamagedFiles)
{
    public int SkippedFiles { get; init; }
}
public sealed record ArchiveProgress(DiscordServer Server, ChannelChoice[] Channels, bool Incomplete, string OperationId,
    IReadOnlyDictionary<string, DateTimeOffset> CopiedAt, IReadOnlySet<string> PendingChannelIds)
{
    public int FailedFiles { get; init; }
    public int SkippedFiles { get; init; }
    public bool HasChannelErrors { get; init; }
}
public sealed record CopyIssue(string Id, string Title, string? Context, string Error, bool IsFile);
public sealed class ChannelSelectionUnavailableException(IReadOnlySet<string> channelIds)
    : InvalidOperationException(Strings.Get("ChannelSelectionUnavailable"))
{
    public IReadOnlySet<string> ChannelIds { get; } = channelIds;
}

public sealed class SnapshotImporter(HttpClient? cdnHttp = null, Func<string, CancellationToken, Task>? publishMedia = null)
{
    private readonly ArchiveMedia media = new(cdnHttp ?? DiscordHttp.Shared);

    public static string DefaultArchiveRoot { get; } = FindArchiveRoot();

    private static string FindArchiveRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "core", "DiscordSnapshot.Core.csproj")))
                return Path.Combine(folder.FullName, "archives");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord Snapshot", "archives");
    }

    public Task<ImportResult> StartAsync(DiscordClient api, string guildId, IReadOnlyCollection<string> channelIds,
        string root, int cdnInterval, Action<string> log, Action<string> onArchive, CancellationToken cancellation)
    {
        root = Path.GetFullPath(root);
        return StartAsync(api, guildId, channelIds, now =>
        {
            var date = now.ToString("yyyy-MM-dd'T'HH-mm-ss.fffffff'Z'", CultureInfo.InvariantCulture);
            return Path.Combine(root, $"{guildId}-{date}");
        }, cdnInterval, log, onArchive, cancellation, Guid.NewGuid().ToString("N"));
    }

    public Task<ImportResult> StartInDirectoryAsync(DiscordClient api, string guildId, IReadOnlyCollection<string> channelIds,
        string directory, int cdnInterval, Action<string> log, CancellationToken cancellation, Action<string>? onArchive = null,
        string? operationId = null) => StartAsync(api, guildId, channelIds, _ => directory, cdnInterval, log, onArchive ?? (_ => { }),
            cancellation, operationId ?? Guid.NewGuid().ToString("N"));

    public static ArchiveProgress? InspectProgress(string directory) => ArchiveStore.InspectProgress(directory);

    public static void ResolveChannelCopies(string directory, string operationId, IReadOnlyCollection<string> channelIds, bool markComplete)
    {
        using var archive = new ArchiveStore(directory);
        archive.ResolveChannelCopies(operationId, channelIds, markComplete);
    }

    public static int SkipFailedFiles(string directory, string operationId)
    {
        using var archive = new ArchiveStore(directory);
        return archive.SkipFailedFiles(operationId);
    }

    public static CopyIssue[] InspectCopyIssues(string directory)
    {
        using var archive = new ArchiveStore(directory, readOnly: true);
        return archive.ReadCopyIssues();
    }

    public static void SkipCopyIssues(string directory, string operationId, IReadOnlyCollection<CopyIssue> issues)
    {
        using var archive = new ArchiveStore(directory);
        archive.SkipCopyIssues(operationId, issues);
    }

    public static void EnsureViewerCompose(string directory)
    {
        const string fileName = "compose.yml";
        const string previous = """
            services:
              viewer:
                image: docker.io/tordel/discord-snapshot-web:latest
                pull_policy: always
                ports:
                  - "127.0.0.1:8080:8080"
                volumes:
                  - type: bind
                    source: .
                    target: /archive
                    read_only: true
            """;
        var path = Path.Combine(directory, fileName);
        var upgrade = File.Exists(path);
        if (upgrade && (new FileInfo(path).Length > previous.Length + 32 ||
            File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd() != previous.ReplaceLineEndings("\n"))) return;

        var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var resource = typeof(SnapshotImporter).Assembly.GetManifestResourceStream(fileName)!)
            using (var output = File.Open(temporary, FileMode.CreateNew))
                resource.CopyTo(output);
            try { File.Move(temporary, path, overwrite: upgrade); }
            catch (IOException) when (!upgrade && File.Exists(path)) { }
        }
        finally { File.Delete(temporary); }
    }

    private async Task<ImportResult> StartAsync(DiscordClient api, string guildId, IReadOnlyCollection<string> channelIds,
        Func<DateTimeOffset, string> getDirectory, int cdnInterval, Action<string> log, Action<string> onArchive,
        CancellationToken cancellation, string operationId)
    {
        var selection = await api.LoadGuildAsync(guildId, cancellation).ConfigureAwait(false);
        var channels = SelectChannels(selection, channelIds);
        cancellation.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var directory = Directory.CreateDirectory(getDirectory(now)).FullName;
        EnsureViewerCompose(directory);
        using var archive = new ArchiveStore(directory, create: true);
        archive.Initialize(selection, channels, now, operationId);
        onArchive(directory);
        await ImportAsync(archive, api, selection.Permissions, cdnInterval, log, cancellation).ConfigureAwait(false);
        return new ImportResult(directory, archive.WriteReport(log, cancellation));
    }

    public async Task<ImportResult> CopyChannelsAsync(DiscordClient api, string directory, IReadOnlyCollection<string> channelIds,
        string operationId, int cdnInterval, Action<string> log, CancellationToken cancellation)
    {
        using var archive = new ArchiveStore(directory);
        var selection = await api.LoadGuildAsync(archive.Snapshot.GuildId, cancellation).ConfigureAwait(false);
        var channels = SelectChannels(selection, channelIds);
        cancellation.ThrowIfCancellationRequested();
        EnsureViewerCompose(archive.DirectoryPath);
        archive.BeginCopy(selection, channels, DateTimeOffset.UtcNow, operationId);
        await ImportAsync(archive, api, selection.Permissions, cdnInterval, log, cancellation).ConfigureAwait(false);
        return new ImportResult(archive.DirectoryPath, archive.WriteReport(log, cancellation));
    }

    private static DiscordChannel[] SelectChannels(GuildSelection selection, IReadOnlyCollection<string> channelIds)
    {
        var ids = channelIds.ToHashSet();
        if (ids.Count == 0) throw new InvalidOperationException(Strings.Get("SelectChannels"));
        var channels = selection.Readable.Where(channel => ids.Contains(channel.Id)).ToArray();
        if (channels.Length != ids.Count)
            throw new ChannelSelectionUnavailableException(ids.Except(channels.Select(channel => channel.Id)).ToHashSet());
        return channels;
    }

    public async Task<ImportResult> ResumeAsync(DiscordClient api, string directory, int cdnInterval,
        Action<string> log, Action<string> onArchive, CancellationToken cancellation)
    {
        using var archive = new ArchiveStore(directory);
        var snapshot = archive.Snapshot;
        EnsureViewerCompose(archive.DirectoryPath);
        onArchive(archive.DirectoryPath);
        var selection = await api.LoadGuildAsync(snapshot.GuildId, cancellation).ConfigureAwait(false);
        await ImportAsync(archive, api, selection.Permissions, cdnInterval, log, cancellation).ConfigureAwait(false);
        return new ImportResult(archive.DirectoryPath, archive.WriteReport(log, cancellation));
    }

    public static async Task<ArchiveCheck> CheckAsync(string directory, Action<string> log, CancellationToken cancellation)
    {
        using var archive = new ArchiveStore(directory, readOnly: true);
        cancellation.ThrowIfCancellationRequested();
        archive.VerifyDatabase();
        var incomplete = archive.WriteReport(log, cancellation);
        var damaged = await archive.VerifyFilesAsync(log, cancellation).ConfigureAwait(false);
        return new ArchiveCheck(incomplete, damaged) { SkippedFiles = archive.SkippedFiles };
    }

    private async Task ImportAsync(ArchiveStore archive, DiscordClient api, Dictionary<string, ulong> permissions,
        int cdnInterval, Action<string> log, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.Combine(archive.DirectoryPath, "media"));
        var (guildId, cutoff, _) = archive.Snapshot;
        var previous = "";
        while (true)
        {
            var assets = archive.PendingAssets(previous);
            if (assets.Length == 0) break;
            foreach (var entry in assets) await DownloadAssetAsync(archive, api, entry, cdnInterval, log, cancellation).ConfigureAwait(false);
            previous = assets[^1].Asset.Id;
        }
        await DiscoverThreadsAsync(archive, api, guildId, cutoff, permissions, log, cancellation).ConfigureAwait(false);
        foreach (var channel in archive.PendingHistory())
        {
            cancellation.ThrowIfCancellationRequested();
            var root = channel.Selected ? channel.Id : channel.ParentId ?? "";
            try
            {
                if (!DiscordClient.CanRead(permissions.GetValueOrDefault(root), channel.Type))
                    throw new InvalidOperationException(Strings.Get("BotCannotReadChannel"));
                await ImportHistoryAsync(archive, api, guildId, channel, cdnInterval, log, cancellation).ConfigureAwait(false);
                archive.HistoryCompleted(channel.Id);
            }
            catch (Exception error) when (!MustStop(error) && !cancellation.IsCancellationRequested)
            {
                archive.HistoryError(channel.Id, error.Message);
                log(Strings.Get("ChannelImportIncomplete", channel.Name, error.Message));
            }
        }
        cancellation.ThrowIfCancellationRequested();
        archive.FinishCopy(cutoff);
    }

    private static bool MustStop(Exception error) => error is OperationCanceledException or IOException or UnauthorizedAccessException
        or DiscordRequestException { Status: 401 or 429 };

    private static async Task DiscoverThreadsAsync(ArchiveStore archive, DiscordClient api, string guildId, string cutoff,
        Dictionary<string, ulong> permissions, Action<string> log, CancellationToken cancellation)
    {
        var roots = archive.PendingDiscovery();
        if (roots.Length == 0) return;
        JsonElement active;
        try { active = await api.GetAsync($"guilds/{guildId}/threads/active", cancellation).ConfigureAwait(false); }
        catch (Exception error) when (!MustStop(error) && !cancellation.IsCancellationRequested)
        {
            foreach (var root in roots) archive.DiscoveryError(root.Id, error.Message);
            log(Strings.Get("ActiveThreadsFailed", error.Message));
            return;
        }

        var threads = active.Array("threads").Select(DiscordChannel.FromJson).ToLookup(thread => thread.ParentId);
        var maxId = Snowflake.Parse(cutoff);
        foreach (var root in roots)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var perms = permissions.GetValueOrDefault(root.Id);
                if (!DiscordClient.CanRead(perms, root.Type)) throw new InvalidOperationException(Strings.Get("BotCannotReadParent"));
                foreach (var thread in threads[root.Id])
                    if (Snowflake.Parse(thread.Id) < maxId) archive.SaveChannel(thread, cutoff);

                await SaveArchived("public").ConfigureAwait(false);
                string? note = null;
                if (root.Type == 0)
                {
                    var allPrivate = (perms & DiscordClient.ManageThreads) != 0;
                    await SaveArchived(allPrivate ? "private" : "joined").ConfigureAwait(false);
                    if (!allPrivate) note = Strings.Get("PrivateThreadsLimited");
                }
                archive.DiscoveryCompleted(root.Id, note);

                async Task SaveArchived(string kind)
                {
                    await foreach (var thread in api.ArchivedThreads(root.Id, kind, cancellation).ConfigureAwait(false))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (thread.ParentId == root.Id && Snowflake.Parse(thread.Id) < maxId) archive.SaveChannel(thread, cutoff);
                    }
                }
            }
            catch (Exception error) when (!MustStop(error) && !cancellation.IsCancellationRequested)
            {
                archive.DiscoveryError(root.Id, error.Message);
                log(Strings.Get("ThreadImportIncomplete", root.Name, error.Message));
            }
        }
    }

    private async Task ImportHistoryAsync(ArchiveStore archive, DiscordClient api, string guildId, StoredChannel channel,
        int cdnInterval, Action<string> log, CancellationToken cancellation)
    {
        var before = Snowflake.Parse(channel.Cursor);
        var floor = channel.CopiedThrough is { } copiedThrough ? Snowflake.Parse(copiedThrough) : 0;
        while (true)
        {
            var page = await api.GetAsync($"channels/{Snowflake.Parse(channel.Id)}/messages?before={before}&limit=100", cancellation).ConfigureAwait(false);
            if (page.GetArrayLength() == 0) return;
            var ids = page.EnumerateArray().Select(message => Snowflake.Parse(message.Id())).ToArray();
            if (ids.Any(id => id >= before) || ids.Distinct().Count() != ids.Length)
                throw new InvalidDataException(Strings.Get("UnexpectedMessagePage"));
            var next = ids.Min();
            var messages = page.EnumerateArray().Where(message => Snowflake.Parse(message.Id()) >= floor).ToArray();
            if (messages.Length == 0) return;
            var profiles = await CaptureProfilesAsync(archive, api, guildId, messages, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var assets = archive.SavePage(guildId, channel, messages, next.ToString(CultureInfo.InvariantCulture), profiles);
            before = next;
            log(Strings.Get("MessagePageSaved", channel.Name, messages.Length, before));
            foreach (var id in assets)
            {
                var entry = archive.Asset(id);
                if (entry.Error is null) await DownloadAssetAsync(archive, api, entry, cdnInterval, log, cancellation).ConfigureAwait(false);
            }
            if (next <= floor) return;
        }
    }

    private static async Task<List<CapturedProfile>> CaptureProfilesAsync(ArchiveStore archive, DiscordClient api, string guildId,
        JsonElement[] page, CancellationToken cancellation)
    {
        var profiles = new List<CapturedProfile>();
        var seen = new HashSet<string>();
        foreach (var message in page)
        {
            var author = message.Get("author");
            if (author.Text("id") is not { } id || message.Text("webhook_id") is not null || !seen.Add(id) || archive.HasProfile(id)) continue;
            JsonElement member = default;
            try { member = await api.GetAsync($"guilds/{Snowflake.Parse(guildId)}/members/{Snowflake.Parse(id)}", cancellation).ConfigureAwait(false); }
            catch (DiscordRequestException error) when (error.Status == 404) { }
            var user = member.Get("user");
            profiles.Add(new CapturedProfile(user.Text("id") == id ? user : author, member, DateTimeOffset.UtcNow));
        }
        return profiles;
    }

    private async Task DownloadAssetAsync(ArchiveStore archive, DiscordClient api, StoredAsset entry, int interval,
        Action<string> log, CancellationToken cancellation)
    {
        if (entry.Hash is not null) return;
        var asset = entry.Asset;
        try
        {
            (long Bytes, string Sha256) result;
            try { result = await media.DownloadAsync(asset, archive.DirectoryPath, interval, cancellation).ConfigureAwait(false); }
            catch (DiscordRequestException error) when (error.Status is 403 or 404 && asset.Kind is "attachment" or "embed"
                && entry.ChannelId is not null && entry.MessageId is not null)
            {
                var message = await api.GetAsync($"channels/{Snowflake.Parse(entry.ChannelId)}/messages/{Snowflake.Parse(entry.MessageId)}", cancellation).ConfigureAwait(false);
                if (!ArchiveMedia.ForMessage(message).TryGetValue(asset.Id, out var refreshed) || refreshed.Url == asset.Url) throw;
                archive.RefreshAsset(refreshed);
                var download = asset with { Url = refreshed.Url };
                result = await media.DownloadAsync(download, archive.DirectoryPath, interval, cancellation).ConfigureAwait(false);
            }
            if (publishMedia is not null) await publishMedia(asset.Path, cancellation).ConfigureAwait(false);
            archive.AssetSaved(asset.Id, result.Bytes, result.Sha256);
        }
        catch (Exception error) when (!MustStop(error) && !cancellation.IsCancellationRequested)
        {
            archive.AssetError(asset.Id, error.Message);
            log(Strings.Get("AssetError", asset.Path, error.Message));
        }
    }
}
