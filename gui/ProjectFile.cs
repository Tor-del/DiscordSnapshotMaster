using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordSnapshot.Gui;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectStatus>))]
internal enum ProjectStatus { New, Copying, Completed }

internal sealed record CopySettings(DiscordServer Server, ChannelChoice[] Channels, int ApiInterval, int CdnInterval, byte[]? Token, string OperationId)
{
    public static byte[] ProtectToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        try { return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string? ReadToken()
    {
        if (Token is null) return null;
        var bytes = UnprotectToken() ?? throw new InvalidOperationException(Strings.Get("SavedTokenUnavailable"));
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public bool CanReadToken()
    {
        if (UnprotectToken() is not { } bytes) return false;
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    private byte[]? UnprotectToken()
    {
        if (Token is null) return null;
        try
        {
            return ProtectedData.Unprotect(Token, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException) { return null; }
    }
}

internal sealed class ProjectFile(string directory, FileStream folderLock) : IDisposable
{
    private const string FileName = "project.json";
    private const string TemporaryName = FileName + ".tmp";
    private const string LockName = ".project.lock";
    private const int Version = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private Manifest manifest = new(Version, ProjectStatus.New);

    public string DirectoryPath { get; } = directory;
    private string ManifestPath => Path.Combine(DirectoryPath, FileName);
    public ProjectStatus Status => manifest.Status;
    public string? Name => string.IsNullOrWhiteSpace(manifest.Name) ? null : manifest.Name;
    public CopySettings? Settings => manifest.Settings;
    public bool HasUsableToken { get; private set; }
    public ArchiveProgress? Archive { get; private set; }
    public bool CanResume => Status == ProjectStatus.Copying;

    private sealed record Manifest(int Version, ProjectStatus Status, CopySettings? Settings = null, string? Name = null);

    public static ProjectFile? OpenOrCreate(string directory, Func<string?> chooseName) => Load(directory, chooseName);
    public static ProjectFile Open(string directory) => Load(directory, chooseName: null)!;

    public static string DefaultName(string directory)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        return string.IsNullOrEmpty(name) ? directory : name;
    }

    private static ProjectFile? Load(string directory, Func<string?>? chooseName)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var manifestPath = Path.Combine(directory, FileName);

        var lockPath = Path.Combine(directory, LockName);
        var folderLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1, FileOptions.DeleteOnClose);
        var project = new ProjectFile(directory, folderLock);
        try
        {
            var hasManifest = Path.Exists(manifestPath);
            if (!hasManifest && !Path.Exists(Path.Combine(directory, "snapshot.sqlite")))
            {
                if (chooseName is null) throw new InvalidDataException(Strings.Get("ProjectNotFound"));

                var media = Path.Combine(directory, "media");
                if (Path.Exists(Path.Combine(directory, TemporaryName)) || File.Exists(media) ||
                    Directory.Exists(media) && Directory.EnumerateFileSystemEntries(media).Any())
                    throw new InvalidOperationException(Strings.Get("ProjectFolderOccupied"));

                var name = chooseName();
                if (name is null)
                {
                    project.Dispose();
                    return null;
                }
                project.Save(project.manifest with { Name = name });
            }
            else
            {
                if (hasManifest)
                {
                    using var file = File.OpenRead(manifestPath);
                    project.manifest = JsonSerializer.Deserialize<Manifest>(file, JsonOptions)
                        ?? throw new InvalidDataException(Strings.Get("EmptyProjectFile"));
                    project.Validate();
                    project.HasUsableToken = project.Settings?.CanReadToken() == true;
                }

                project.Archive = SnapshotImporter.InspectProgress(directory);
                if (!hasManifest && project.Archive is null)
                    throw new InvalidDataException(Strings.Get("ProjectSnapshotMissing"));
                if (project.Status == ProjectStatus.Copying && project.Settings is null && project.Archive is null)
                    throw new InvalidDataException(Strings.Get("CopySettingsMissing"));
                if (project.Settings is { Channels.Length: 0 } && (project.Archive is null || !project.IsCurrentCopy(project.Archive)))
                    throw new InvalidDataException(Strings.Get("CopySettingsMissing"));
                if (project.Status == ProjectStatus.Completed && project.Archive is not { Incomplete: false })
                    throw new InvalidDataException(Strings.Get("CompletedProjectInvalid"));

                if (project.Archive is { Incomplete: false } archive && project.IsCurrentCopy(archive))
                {
                    if (hasManifest && project.Status != ProjectStatus.Completed) project.Complete();
                    else project.manifest = project.manifest with { Status = ProjectStatus.Completed, Settings = null };
                }
                else if (project.Archive is not null && !hasManifest)
                {
                    project.manifest = new(Version, ProjectStatus.Copying);
                }
                if (project.Archive is not null) SnapshotImporter.EnsureViewerCompose(directory);
            }
            return project;
        }
        catch
        {
            project.Dispose();
            throw;
        }
    }

    private void Validate()
    {
        if (manifest.Version != Version || !Enum.IsDefined(Status))
            throw new InvalidDataException(Strings.Get("UnsupportedProject"));
        if (Status == ProjectStatus.Copying)
        {
            if (Settings is null) return; // A CLI snapshot can be named before GUI copy settings have been created.
            if (Settings is not { Server.Id.Length: > 0, Channels: not null, ApiInterval: >= 30, CdnInterval: >= 0 } ||
                Settings.Token is { Length: 0 } ||
                Settings.Channels.Any(channel => string.IsNullOrEmpty(channel?.Id)) ||
                !Guid.TryParseExact(Settings.OperationId, "N", out _))
                throw new InvalidDataException(Strings.Get("CopySettingsMissing"));
        }
        else if (Settings is not null)
            throw new InvalidDataException(Strings.Get("InvalidProjectStatus"));
    }

    public void Rename(string name)
    {
        if (Name != name) Save(manifest with { Name = name });
    }

    public void SaveSettings(CopySettings settings) => Save(manifest with { Status = ProjectStatus.Copying, Settings = settings });
    public void SkipPlannedChannels(IReadOnlySet<string> channelIds)
    {
        if (Settings is not { } settings || Archive is { } archive && IsCurrentCopy(archive))
            throw new InvalidOperationException(Strings.Get("CopySelectionChanged"));

        var remaining = settings.Channels.Where(channel => !channelIds.Contains(channel.Id)).ToArray();
        if (remaining.Length > 0) SaveSettings(settings with { Channels = remaining });
        else Save(manifest with { Status = Archive is null ? ProjectStatus.New
            : Archive.Incomplete ? ProjectStatus.Copying : ProjectStatus.Completed, Settings = null });
    }

    public void Complete() => Save(manifest with { Status = ProjectStatus.Completed, Settings = null });
    public void RefreshArchive() => Archive = SnapshotImporter.InspectProgress(DirectoryPath);

    public bool IsCurrentCopy(ArchiveProgress archive) => Settings is null || Settings.OperationId == archive.OperationId;

    private void Save(Manifest next)
    {
        var temporary = Path.Combine(DirectoryPath, TemporaryName);
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, next, JsonOptions);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, ManifestPath, overwrite: true);
            manifest = next;
            HasUsableToken = next.Settings?.CanReadToken() == true;
        }
        finally { File.Delete(temporary); }
    }

    public void Dispose() => folderLock.Dispose();
}
