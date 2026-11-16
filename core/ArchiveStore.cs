using System.Security.Cryptography;
using System.Text.Json;

namespace DiscordSnapshot;

internal sealed record StoredChannel(string Id, string? ParentId, string Name, int Type, bool Selected, string Cursor, string? CopiedThrough);
internal sealed record StoredAsset(MediaAsset Asset, string? ChannelId, string? MessageId, string? Hash, string? Error);
internal sealed record CapturedProfile(JsonElement User, JsonElement Member, DateTimeOffset Time);

internal sealed class ArchiveStore : IDisposable
{
    private const int Version = 2;
    private const string DatabaseName = "snapshot.sqlite";
    private const string Schema = """
        CREATE TABLE snapshot (
          id INTEGER PRIMARY KEY CHECK (id = 1), guild_id TEXT NOT NULL, cutoff TEXT NOT NULL,
          started_at TEXT NOT NULL, data TEXT NOT NULL, icon_id TEXT REFERENCES assets(id), copy_id TEXT NOT NULL
        );
        CREATE TABLE assets (
          id TEXT PRIMARY KEY, kind TEXT NOT NULL, url TEXT NOT NULL, path TEXT NOT NULL,
          expected_bytes INTEGER, bytes INTEGER, sha256 TEXT, channel_id TEXT, message_id TEXT, error TEXT
        );
        CREATE TABLE channels (
          id TEXT PRIMARY KEY, parent_id TEXT, name TEXT NOT NULL, type INTEGER NOT NULL, data TEXT NOT NULL,
          selected INTEGER NOT NULL, cursor TEXT NOT NULL, history_done INTEGER NOT NULL DEFAULT 0,
          discovery_done INTEGER NOT NULL DEFAULT 1, history_error TEXT, discovery_error TEXT, note TEXT,
          pending_copy INTEGER NOT NULL, copied_through TEXT, copied_at TEXT
        );
        CREATE TABLE categories (id TEXT PRIMARY KEY, name TEXT NOT NULL, data TEXT NOT NULL);
        CREATE TABLE profiles (
          id TEXT PRIMARY KEY, captured_at TEXT NOT NULL, user_data TEXT NOT NULL, member_data TEXT,
          avatar_id TEXT NOT NULL REFERENCES assets(id), guild_avatar_id TEXT REFERENCES assets(id)
        );
        CREATE TABLE messages (
          id TEXT PRIMARY KEY, channel_id TEXT NOT NULL REFERENCES channels(id), author_id TEXT,
          timestamp TEXT NOT NULL, content TEXT, data TEXT NOT NULL
        );
        CREATE INDEX messages_channel_timestamp ON messages(channel_id, timestamp, id);
        CREATE TABLE message_assets (
          message_id TEXT NOT NULL REFERENCES messages(id), asset_id TEXT NOT NULL REFERENCES assets(id),
          PRIMARY KEY (message_id, asset_id)
        );
        """;
    private const string ChannelColumns = "SELECT id, parent_id, name, type, selected, cursor, copied_through FROM channels";
    private const string AssetColumns = "SELECT id, kind, url, path, expected_bytes, channel_id, message_id, sha256, error FROM assets";
    private const string HasHistoryGaps = "SELECT EXISTS(SELECT 1 FROM channels WHERE history_done = 0 OR discovery_done = 0)";
    private const string ChannelErrors = "(history_done = 0 AND history_error IS NOT NULL) OR (discovery_done = 0 AND discovery_error IS NOT NULL)";
    private const string HasPendingCopy = "SELECT EXISTS(SELECT 1 FROM channels WHERE pending_copy = 1)";
    private readonly Sqlite db;

    public ArchiveStore(string directory, bool create = false, bool readOnly = false)
    {
        DirectoryPath = Path.GetFullPath(directory);
        var database = Path.Combine(DirectoryPath, DatabaseName);
        if (!create && !File.Exists(database))
            throw new FileNotFoundException(Strings.Get("ArchiveDatabaseMissing", DatabaseName), database);
        db = new Sqlite(database, create, readOnly);
        try
        {
            if (!readOnly) db.Script("PRAGMA locking_mode = EXCLUSIVE; BEGIN EXCLUSIVE; COMMIT;");
            var version = ReadVersion(db);
            if (version != (create ? 0 : Version)) throw new InvalidDataException(Strings.Get("UnsupportedArchive"));
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public string DirectoryPath { get; }

    private static long ReadVersion(Sqlite database) => database.Query("PRAGMA user_version", row => row.Number(0)).Single();

    private static bool HasSkippedAssets(Sqlite database) => database.Query(
        "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'skipped_assets'", _ => true).Any();

    private static string PendingAssetFilter(Sqlite database) => HasSkippedAssets(database)
        ? "sha256 IS NULL AND id NOT IN (SELECT asset_id FROM skipped_assets)" : "sha256 IS NULL";

    private static bool HasGaps(Sqlite database) => database.Query(HasHistoryGaps, row => row.Flag(0)).Single() ||
        database.Query($"SELECT EXISTS(SELECT 1 FROM assets WHERE {PendingAssetFilter(database)})", row => row.Flag(0)).Single();

    private static int CountSkippedFiles(Sqlite database) => HasSkippedAssets(database)
        ? database.Query("SELECT count(*) FROM skipped_assets", row => (int)row.Number(0)).Single() : 0;

    public int SkippedFiles => CountSkippedFiles(db);

    public static ArchiveProgress? InspectProgress(string directory)
    {
        var path = Path.Combine(directory, DatabaseName);
        if (!File.Exists(path)) return null;

        // Open for writing so SQLite can recover an interrupted transaction.
        using var database = new Sqlite(path, create: false, readOnly: false);
        var version = ReadVersion(database);
        if (version == 0) return null;
        if (version != Version) throw new InvalidDataException(Strings.Get("UnsupportedArchive"));

        var (server, operationId) = database.Query("SELECT guild_id, data, copy_id FROM snapshot WHERE id = 1", row =>
        {
            var id = row.Text(0)!;
            using var guild = JsonDocument.Parse(row.Text(1)!);
            return (new DiscordServer(id, guild.RootElement.Text("name") ?? id), row.Text(2)!);
        }).SingleOrDefault();
        if (server is null) throw new InvalidDataException(Strings.Get("SnapshotDataMissing"));
        var roots = database.Query("""
            SELECT c.id, c.name, c.data, g.name, c.copied_at, c.pending_copy FROM channels c
            LEFT JOIN categories g ON g.id = c.parent_id WHERE c.selected = 1 ORDER BY c.name, c.id
            """, row =>
        {
            var title = row.Text(3) is { } category ? $"{category} / {row.Text(1)}" : row.Text(1)!;
            using var data = JsonDocument.Parse(row.Text(2)!);
            var choice = new ChannelChoice(row.Text(0)!, title, DiscordChannel.FromJson(data.RootElement).Kind ?? "");
            return (Choice: choice, CopiedAt: row.Text(4), Pending: row.Flag(5));
        }).ToArray();
        var dates = roots.Where(root => root.CopiedAt is not null).ToDictionary(root => root.Choice.Id,
            root => DateTimeOffset.Parse(root.CopiedAt!, System.Globalization.CultureInfo.InvariantCulture));
        var incomplete = database.Query(HasPendingCopy, row => row.Flag(0)).Single() || HasGaps(database);
        var failedFiles = database.Query($"SELECT count(*) FROM assets WHERE {PendingAssetFilter(database)} AND error IS NOT NULL",
            row => (int)row.Number(0)).Single();
        return new ArchiveProgress(server, roots.Select(root => root.Choice).ToArray(), incomplete, operationId, dates,
            roots.Where(root => root.Pending).Select(root => root.Choice.Id).ToHashSet())
        {
            FailedFiles = failedFiles,
            SkippedFiles = CountSkippedFiles(database),
            HasChannelErrors = database.Query($"SELECT EXISTS(SELECT 1 FROM channels WHERE {ChannelErrors})", row => row.Flag(0)).Single(),
        };
    }

    public (string GuildId, string Cutoff, string StartedAt) Snapshot => db.Query(
        "SELECT guild_id, cutoff, started_at FROM snapshot WHERE id = 1",
        row => (GuildId: row.Text(0)!, Cutoff: row.Text(1)!, StartedAt: row.Text(2)!)).SingleOrDefault()
        is { GuildId: not null } snapshot ? snapshot : throw new InvalidDataException(Strings.Get("SnapshotDataMissing"));

    public void Initialize(GuildSelection selection, DiscordChannel[] channels, DateTimeOffset now, string operationId)
    {
        var cutoff = Snowflake.Cutoff(now);
        db.Transaction(() =>
        {
            db.Script(Schema);
            db.Script($"PRAGMA user_version = {Version}");
            var icon = ArchiveMedia.GuildIcon(selection.Guild);
            if (icon is not null) SaveAsset(icon);
            db.Execute("INSERT INTO snapshot VALUES (1, ?, ?, ?, ?, ?, ?)", selection.Guild.Id(), cutoff, now.ToString("O"), selection.Guild.GetRawText(), icon?.Id, operationId);
            SaveSelection(selection, channels, cutoff);
        });
    }

    public void BeginCopy(GuildSelection selection, DiscordChannel[] channels, DateTimeOffset now, string operationId)
    {
        var cutoff = Snowflake.Cutoff(now);
        db.Transaction(() =>
        {
            if (db.Query(HasPendingCopy, row => row.Flag(0)).Single() || HasGaps(db))
                throw new InvalidOperationException(Strings.Get("FinishCopyBeforeAdding"));
            db.Execute("UPDATE snapshot SET cutoff = ?, copy_id = ? WHERE id = 1", cutoff, operationId);
            SaveSelection(selection, channels, cutoff);
        });
    }

    private void SaveSelection(GuildSelection selection, DiscordChannel[] channels, string cutoff)
    {
        foreach (var channel in channels) SaveChannel(channel, cutoff, selected: true);
        var parents = channels.Select(channel => channel.ParentId).ToHashSet();
        foreach (var category in selection.Channels.Where(channel => channel.Type == 4 && parents.Contains(channel.Id)))
            db.Execute("""
                INSERT INTO categories VALUES (?, ?, ?)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, data = excluded.data
                """, category.Id, category.Name, category.Data.GetRawText());
    }

    public void SaveChannel(DiscordChannel channel, string cutoff, bool selected = false) => db.Execute("""
        INSERT INTO channels (id, parent_id, name, type, data, selected, cursor, history_done, discovery_done, pending_copy)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 1)
        ON CONFLICT(id) DO UPDATE SET parent_id = excluded.parent_id, name = excluded.name, type = excluded.type,
          data = excluded.data, cursor = excluded.cursor, history_done = excluded.history_done,
          discovery_done = excluded.discovery_done, pending_copy = 1, history_error = NULL, discovery_error = NULL, note = NULL
        WHERE channels.pending_copy = 0
        """, channel.Id, channel.ParentId, channel.Name, channel.Type, channel.Data.GetRawText(), selected, cutoff,
        channel.Container, !selected || !channel.HasThreads);

    private static StoredChannel ReadChannel(Sqlite.Columns row) => new(row.Text(0)!, row.Text(1), row.Text(2)!, (int)row.Number(3), row.Flag(4), row.Text(5)!, row.Text(6));
    public StoredChannel[] PendingDiscovery() => db.Query($"{ChannelColumns} WHERE discovery_done = 0", ReadChannel).ToArray();
    public StoredChannel[] PendingHistory() => db.Query($"{ChannelColumns} WHERE history_done = 0 ORDER BY selected DESC, id", ReadChannel).ToArray();
    public void DiscoveryCompleted(string id, string? note) => db.Execute("UPDATE channels SET discovery_done = 1, discovery_error = NULL, note = ? WHERE id = ?", note, id);
    public void DiscoveryError(string id, string error) => db.Execute("UPDATE channels SET discovery_error = ? WHERE id = ?", error, id);
    public void HistoryCompleted(string id) => db.Execute("UPDATE channels SET history_done = 1, history_error = NULL WHERE id = ?", id);
    public void HistoryError(string id, string error) => db.Execute("UPDATE channels SET history_error = ? WHERE id = ?", error, id);
    public bool HasProfile(string id) => db.Query("SELECT 1 FROM profiles WHERE id = ?", row => row.Flag(0), id).Any();

    public void FinishCopy(string cutoff)
    {
        if (HasGaps(db)) return;
        db.Execute("UPDATE channels SET pending_copy = 0, copied_through = ?, copied_at = ? WHERE pending_copy = 1",
            cutoff, DateTimeOffset.UtcNow.ToString("O"));
    }

    public void ResolveChannelCopies(string operationId, IReadOnlyCollection<string> channelIds, bool markComplete)
    {
        db.Transaction(() =>
        {
            var currentOperation = db.Query("SELECT copy_id FROM snapshot WHERE id = 1", row => row.Text(0)).Single();
            var pending = db.Query("SELECT id FROM channels WHERE selected = 1 AND pending_copy = 1", row => row.Text(0)!).ToHashSet();
            if (currentOperation != operationId || channelIds.Any(id => !pending.Contains(id)))
                throw new InvalidOperationException(Strings.Get("CopySelectionChanged"));

            var cutoff = Snapshot.Cutoff;
            var now = DateTimeOffset.UtcNow.ToString("O");
            var note = Strings.Get(markComplete ? "ChannelManuallyCompleted" : "ChannelCopySkipped");
            foreach (var id in channelIds)
                db.Execute("""
                    UPDATE channels SET pending_copy = 0, history_done = 1, discovery_done = 1,
                        history_error = NULL, discovery_error = NULL, note = ?,
                        copied_through = CASE WHEN ? THEN ? ELSE copied_through END,
                        copied_at = CASE WHEN ? THEN ? ELSE copied_at END
                    WHERE pending_copy = 1 AND (id = ? OR (selected = 0 AND parent_id = ?))
                    """, note, markComplete, cutoff, markComplete, now, id, id);
            FinishCopy(cutoff);
        });
    }

    public int SkipFailedFiles(string operationId)
    {
        var skipped = 0;
        db.Transaction(() =>
        {
            CheckOperation(operationId);

            var filter = PendingAssetFilter(db) + " AND error IS NOT NULL";
            skipped = db.Query($"SELECT count(*) FROM assets WHERE {filter}", row => (int)row.Number(0)).Single();
            if (skipped == 0) return;

            EnsureSkippedAssets();
            db.Execute($"INSERT INTO skipped_assets SELECT id FROM assets WHERE {filter}");
            FinishCopy(Snapshot.Cutoff);
        });
        return skipped;
    }

    public CopyIssue[] ReadCopyIssues()
    {
        var channels = db.Query($"""
            SELECT c.id, c.name, p.name, c.history_error, c.discovery_error
            FROM channels c LEFT JOIN channels p ON c.selected = 0 AND c.parent_id = p.id
            WHERE c.id IN (SELECT id FROM channels WHERE {ChannelErrors}) ORDER BY c.name, c.id
            """, row => new CopyIssue(row.Text(0)!, row.Text(1)!, row.Text(2),
                string.Join(Environment.NewLine, new[] { row.Text(3), row.Text(4) }.OfType<string>()), IsFile: false));
        var files = db.Query($"""
            SELECT a.id, a.path, a.url, c.name, a.message_id, a.error FROM assets a
            LEFT JOIN channels c ON c.id = a.channel_id
            WHERE a.id IN (SELECT id FROM assets WHERE {PendingAssetFilter(db)} AND error IS NOT NULL) ORDER BY a.path
            """, row =>
        {
            var path = row.Text(1)!;
            var name = Uri.TryCreate(row.Text(2), UriKind.Absolute, out var url) ? Path.GetFileName(url.LocalPath) : null;
            var context = row.Text(4) is { } message ? path + Strings.Get("MessageLocation", message, row.Text(3) ?? "") : path;
            return new CopyIssue(row.Text(0)!, string.IsNullOrEmpty(name) ? path : name, context, row.Text(5)!, IsFile: true);
        });
        return channels.Concat(files).ToArray();
    }

    public void SkipCopyIssues(string operationId, IReadOnlyCollection<CopyIssue> issues)
    {
        db.Transaction(() =>
        {
            CheckOperation(operationId);
            var active = ReadCopyIssues().Select(issue => (issue.IsFile, issue.Id)).ToHashSet();
            if (issues.Any(issue => !active.Contains((issue.IsFile, issue.Id))))
                throw new InvalidOperationException(Strings.Get("CopySelectionChanged"));

            if (issues.Any(issue => issue.IsFile)) EnsureSkippedAssets();
            foreach (var issue in issues.DistinctBy(issue => (issue.IsFile, issue.Id)))
            {
                if (issue.IsFile) db.Execute("INSERT INTO skipped_assets VALUES (?)", issue.Id);
                else db.Execute("""
                    UPDATE channels SET history_done = 1, discovery_done = 1, pending_copy = 0, note = ? WHERE id = ?
                    """, Strings.Get("ChannelCopySkipped"), issue.Id);
            }
            FinishCopy(Snapshot.Cutoff);
        });
    }

    private void CheckOperation(string operationId)
    {
        var currentOperation = db.Query("SELECT copy_id FROM snapshot WHERE id = 1", row => row.Text(0)).Single();
        if (currentOperation != operationId) throw new InvalidOperationException(Strings.Get("CopySelectionChanged"));
    }

    // Optional in version 2 so existing archives can be opened without a schema migration.
    private void EnsureSkippedAssets() => db.Execute("""
        CREATE TABLE IF NOT EXISTS skipped_assets (
          asset_id TEXT NOT NULL PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE
        )
        """);

    private void SaveAsset(MediaAsset entry, string? channelId = null, string? messageId = null) => db.Execute("""
        INSERT INTO assets (id, kind, url, path, expected_bytes, channel_id, message_id) VALUES (?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT (id) DO UPDATE SET url = excluded.url, channel_id = excluded.channel_id, message_id = excluded.message_id
        WHERE assets.sha256 IS NULL
        """, entry.Id, entry.Kind, entry.Url, entry.Path, entry.ExpectedBytes, channelId, messageId);

    public IEnumerable<string> SavePage(string guildId, StoredChannel channel, JsonElement[] messages, string next, IEnumerable<CapturedProfile> profiles)
    {
        var assetIds = new HashSet<string>();
        void Queue(MediaAsset entry, string? messageId = null)
        {
            SaveAsset(entry, messageId is null ? null : channel.Id, messageId);
            assetIds.Add(entry.Id);
        }
        db.Transaction(() =>
        {
            foreach (var profile in profiles)
            {
                var global = ArchiveMedia.Avatar(profile.User);
                var local = profile.Member.Text("avatar") is { Length: > 0 } ? ArchiveMedia.Avatar(profile.User, profile.Member, guildId) : null;
                Queue(global);
                if (local is not null) Queue(local);
                var member = profile.Member.ValueKind == JsonValueKind.Object ? profile.Member.GetRawText() : null;
                db.Execute("INSERT INTO profiles VALUES (?, ?, ?, ?, ?, ?)", profile.User.Id(), profile.Time.ToString("O"), profile.User.GetRawText(), member, global.Id, local?.Id);
            }
            foreach (var message in messages)
            {
                var id = message.Id();
                // A skipped partial copy can revisit pages that were already saved.
                if (db.Query("SELECT 1 FROM messages WHERE id = ?", row => row.Flag(0), id).Any()) continue;
                var author = message.Get("author");
                db.Execute("INSERT INTO messages VALUES (?, ?, ?, ?, ?, ?)", id, channel.Id, author.Text("id"),
                    message.Text("timestamp"), message.Text("content"), message.GetRawText());
                var assets = ArchiveMedia.ForMessage(message);
                if (author.ValueKind == JsonValueKind.Object)
                {
                    var avatar = ArchiveMedia.Avatar(author);
                    assets[avatar.Id] = avatar;
                }
                foreach (var asset in assets.Values)
                {
                    Queue(asset, id);
                    db.Execute("INSERT INTO message_assets VALUES (?, ?)", id, asset.Id);
                }
            }
            db.Execute("UPDATE channels SET cursor = ?, history_error = NULL WHERE id = ?", next, channel.Id);
        });
        return assetIds;
    }

    private static StoredAsset ReadAsset(Sqlite.Columns row) => new(new MediaAsset(row.Text(0)!, row.Text(1)!, row.Text(2)!, row.Text(3)!, row.OptionalNumber(4)),
        row.Text(5), row.Text(6), row.Text(7), row.Text(8));
    public StoredAsset[] PendingAssets(string after) => db.Query(
        $"{AssetColumns} WHERE {PendingAssetFilter(db)} AND id > ? ORDER BY id LIMIT 100", ReadAsset, after).ToArray();
    public StoredAsset Asset(string id) => db.Query($"{AssetColumns} WHERE id = ?", ReadAsset, id).Single();
    public void RefreshAsset(MediaAsset asset) => db.Execute("UPDATE assets SET url = ? WHERE id = ?", asset.Url, asset.Id);
    public void AssetSaved(string id, long bytes, string hash) => db.Execute("UPDATE assets SET bytes = ?, sha256 = ?, error = NULL WHERE id = ?", bytes, hash, id);
    public void AssetError(string id, string error) => db.Execute("UPDATE assets SET error = ? WHERE id = ?", error, id);

    public bool WriteReport(Action<string> log, CancellationToken cancellation)
    {
        var snapshot = Snapshot;
        log(Strings.Get("SnapshotReportHeading", snapshot.StartedAt, snapshot.GuildId));
        var incomplete = false;
        foreach (var channel in db.Query("""
            SELECT c.id, c.name, c.history_done AND c.discovery_done, c.history_error, c.discovery_error, c.note,
              (SELECT count(*) FROM messages m WHERE m.channel_id = c.id) FROM channels c ORDER BY c.selected DESC, c.id
            """, row => (Id: row.Text(0), Name: row.Text(1), Done: row.Flag(2), History: row.Text(3), Discovery: row.Text(4), Note: row.Text(5), Count: row.Number(6))))
        {
            cancellation.ThrowIfCancellationRequested();
            incomplete |= !channel.Done;
            log(Strings.Get("ChannelReport", channel.Name, channel.Id, channel.Count, Strings.Get(channel.Done ? "ChannelComplete" : "ChannelIncomplete")));
            foreach (var detail in new[] { channel.History, channel.Discovery, channel.Note })
                if (detail is not null) log($"    {detail}");
        }
        var files = db.Query("SELECT count(*), count(sha256), coalesce(sum(bytes), 0) FROM assets",
            row => (Total: row.Number(0), Saved: row.Number(1), Bytes: row.Number(2))).Single();
        log(Strings.Get("FilesReport", files.Saved, files.Total, files.Bytes));
        foreach (var file in db.Query($"""
            SELECT path, kind, channel_id, message_id, error, NOT ({PendingAssetFilter(db)})
            FROM assets WHERE sha256 IS NULL ORDER BY id
            """, row => (Path: row.Text(0), Kind: row.Text(1), Channel: row.Text(2), Message: row.Text(3), Error: row.Text(4), Skipped: row.Flag(5))))
        {
            cancellation.ThrowIfCancellationRequested();
            var location = file.Message is null ? "" : Strings.Get("MessageLocation", file.Message, file.Channel);
            var detail = file.Skipped ? Strings.Get("FileSkipped", file.Error!) : file.Error ?? Strings.Get("PendingDownload");
            log($"  {file.Path} · {file.Kind}{location} · {detail}");
            incomplete |= !file.Skipped;
        }
        return incomplete || db.Query(HasPendingCopy, row => row.Flag(0)).Single();
    }

    public void VerifyDatabase()
    {
        if (db.Query("PRAGMA integrity_check", row => row.Text(0)).FirstOrDefault() != "ok" || db.Query("PRAGMA foreign_key_check", _ => true).Any())
            throw new InvalidDataException(Strings.Get("SqliteIntegrityFailure"));
    }

    public async Task<int> VerifyFilesAsync(Action<string> log, CancellationToken cancellation)
    {
        var damaged = 0;
        foreach (var entry in db.Query("SELECT path, bytes, sha256 FROM assets WHERE sha256 IS NOT NULL",
            row => (Path: row.Text(0)!, Bytes: row.Number(1), Hash: row.Text(2))))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var path = ArchiveMedia.LocalPath(DirectoryPath, entry.Path);
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                if (file.Length != entry.Bytes) throw new InvalidDataException(Strings.Get("WrongSize"));
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false));
                if (hash != entry.Hash) throw new InvalidDataException(Strings.Get("WrongChecksum"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                damaged++;
                log(Strings.Get("FileVerificationFailed", entry.Path, error.Message));
            }
        }
        return damaged;
    }

    public void Dispose() => db.Dispose();
}
