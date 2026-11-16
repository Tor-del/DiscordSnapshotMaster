using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DiscordSnapshot.Gui;

internal sealed record SshAddress(string Host, string User, int Port, string Path)
{
    public override string ToString() => $"ssh://{User}@{Host}:{Port}{Path}";
    public string HistoryKey => new UriBuilder("ssh", Host, Port, Path.Replace("%", "%25")) { UserName = User }.Uri.AbsoluteUri;
    public string ProjectFolderName => Path[(Path.LastIndexOf('/') + 1)..];

    public static SshAddress? Create(string host, string user, int port, string path)
    {
        if (!Regex.IsMatch(host, @"\A[A-Za-z0-9][A-Za-z0-9.-]{0,252}\z") ||
            !Regex.IsMatch(user, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}\z") ||
            port is < 1 or > 65535 || path.Length is < 2 or > 1024 || path[0] != '/' ||
            path.Any(character => char.IsControl(character) || "\\\"*?[]#".Contains(character)) ||
            path.Split('/', StringSplitOptions.RemoveEmptyEntries) is not { Length: > 0 } parts ||
            parts.Any(part => part is "." or "..")) return null;
        return new SshAddress(host.ToLowerInvariant(), user, port, "/" + string.Join('/', parts));
    }

    public static SshAddress? Parse(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "ssh" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? Create(uri.Host, Uri.UnescapeDataString(uri.UserInfo), uri.Port, Uri.UnescapeDataString(uri.AbsolutePath))
            : null;
}

internal sealed class SshProject(SshAddress address, string directory)
{
    private const string Database = "snapshot.sqlite";
    private const string Manifest = "project.json";
    private const string AuthFile = ArchiveAuthorization.FileName;
    private static readonly string Cache = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord Snapshot", "SSH"));
    private static readonly string[] AuthorizationFiles = [AuthFile, AuthFile + ".disabled"];
    private static readonly string[] ProjectFiles = [Manifest, Database, "compose.yml", .. AuthorizationFiles];
    private static readonly Regex MediaName = new(@"\A[a-f0-9]{64}(\.[a-z0-9]{1,8})?\z");
    private readonly Dictionary<string, string> lastSaved = new(StringComparer.Ordinal);
    private readonly string transferId = Guid.NewGuid().ToString("N");
    private bool mediaDirectoryReady;
    public string DirectoryPath => directory;
    public string RemotePath => address.ToString();
    public string HistoryKey => address.HistoryKey;
    public string ProjectFolderName => address.ProjectFolderName;

    public static async Task<SshProject> DownloadAsync(SshAddress address, bool createIfMissing, CancellationToken cancellation)
    {
        var project = new SshProject(address, Path.Combine(Cache, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(project.DirectoryPath);
        try
        {
            var folders = address.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parents = Enumerable.Range(1, folders.Length)
                .Select(count => "-@mkdir " + Quote("/" + string.Join('/', folders.Take(count))));
            if (createIfMissing) await project.BatchAsync([.. parents, "@cd " + Quote(address.Path)], cancellation);
            var root = await project.ListAsync("", cancellation);
            CheckDatabaseState(root);
            var files = ProjectFiles.Where(root.Contains).ToArray();
            var commands = new List<string>();
            foreach (var name in files)
            {
                var local = project.LocalFile(name);
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                commands.Add(project.Get(name, local));
            }
            if (commands.Count > 0) await project.BatchAsync(commands, cancellation);
            foreach (var name in files) project.lastSaved[name] = Hash(project.LocalFile(name));
            return project;
        }
        catch
        {
            project.RemoveWorkingCopy();
            throw;
        }
    }

    public async Task UploadMediaAsync(string relativePath, CancellationToken cancellation)
    {
        if (!relativePath.StartsWith("media/", StringComparison.Ordinal) || !MediaName.IsMatch(relativePath[6..]))
            throw new InvalidDataException(Strings.Get("SshInvalidTransfer"));
        var temporary = TemporaryFile(relativePath);
        if (!mediaDirectoryReady)
        {
            await BatchAsync(["-@mkdir " + Quote(RemoteFile("media")), "@cd " + Quote(RemoteFile("media"))], cancellation);
            mediaDirectoryReady = true;
        }
        try
        {
            await RunAsync("scp", ["-s", "-q", "--", LocalFile(relativePath), address.User + "@" + address.Host + ":" + RemoteFile(temporary)],
                null, cancellation);
            await BatchAsync([Rename(temporary, relativePath)], cancellation);
        }
        catch (IOException)
        {
            await RemoveTemporaryAsync([temporary], cancellation);
            throw;
        }
    }

    public async Task DownloadMediaAsync(CancellationToken cancellation)
    {
        if (!(await ListAsync("", cancellation)).Contains("media")) return;
        var available = await ListAsync("media", cancellation);
        var files = available.Where(name => MediaName.IsMatch(name) && !File.Exists(LocalFile("media/" + name))).ToArray();
        if (files.Length == 0) return;
        var folder = Directory.CreateTempSubdirectory("discord-ssh-media-");
        try
        {
            await BatchAsync(files.Select(name => Get("media/" + name, Path.Combine(folder.FullName, name))), cancellation);
            Directory.CreateDirectory(LocalFile("media"));
            foreach (var name in files) File.Move(Path.Combine(folder.FullName, name), LocalFile("media/" + name));
        }
        finally { folder.Delete(recursive: true); }
    }

    public Task SaveAsync(CancellationToken cancellation) => SaveFilesAsync(ProjectFiles, cancellation);

    public Task SaveAuthorizationAsync(CancellationToken cancellation) => SaveFilesAsync(AuthorizationFiles, cancellation);

    public Task SaveProjectNameAsync(CancellationToken cancellation) => SaveFilesAsync([Manifest], cancellation);

    private async Task SaveFilesAsync(string[] files, CancellationToken cancellation)
    {
        var changes = files.Select(name => (Name: name, Hash: File.Exists(LocalFile(name)) ? Hash(LocalFile(name)) : null))
            .Where(file => file.Hash != lastSaved.GetValueOrDefault(file.Name))
            .OrderBy(file => file.Name == Database ? 3 : file.Name == AuthFile ? file.Hash is null ? 2 : 0 : 1)
            .ToArray();
        if (changes.Length == 0) return;

        var root = await ListAsync("", cancellation);
        if (changes.Any(file => file.Name == Database)) CheckDatabaseState(root);
        using var samples = new TemporarySamples();
        var checks = changes.Where(file => root.Contains(file.Name)).ToArray();
        if (checks.Length > 0)
            await BatchAsync(checks.Select((file, index) => Get(file.Name, samples.File(index))), cancellation);
        var current = checks.Select((file, index) => (file.Name, Hash: Hash(samples.File(index))))
            .ToDictionary(file => file.Name, file => file.Hash, StringComparer.Ordinal);
        var writes = changes.Where(file => current.GetValueOrDefault(file.Name) != file.Hash).ToArray();
        if (writes.Any(file => current.GetValueOrDefault(file.Name) != lastSaved.GetValueOrDefault(file.Name)))
            throw new IOException(Strings.Get("SshRemoteChanged"));

        var uploads = writes.Where(file => file.Hash is not null).Select(file => file.Name).ToArray();
        try
        {
            if (uploads.Length > 0)
            {
                var commands = new List<string>();
                var empty = samples.File(-1);
                File.WriteAllBytes(empty, []);
                foreach (var name in uploads)
                {
                    var target = Quote(RemoteFile(TemporaryFile(name)));
                    if (name == AuthFile || name == AuthFile + ".disabled")
                    {
                        commands.Add("@put " + Quote(LocalArgument(empty)) + " " + target);
                        commands.Add("@chmod 600 " + target);
                    }
                    commands.Add("@put " + Quote(LocalArgument(LocalFile(name))) + " " + target);
                }
                await BatchAsync(commands, cancellation);
            }
            if (writes.Length > 0) await BatchAsync(writes.Select(file => file.Hash is null
                ? "@rm " + Quote(RemoteFile(file.Name)) : Rename(TemporaryFile(file.Name), file.Name)), cancellation);
        }
        catch (IOException)
        {
            await RemoveTemporaryAsync(uploads.Select(TemporaryFile), cancellation);
            throw;
        }
        foreach (var file in changes)
            if (file.Hash is null) lastSaved.Remove(file.Name);
            else lastSaved[file.Name] = file.Hash;
    }

    private async Task<HashSet<string>> ListAsync(string folder, CancellationToken cancellation)
    {
        var output = await BatchAsync(["@cd " + Quote(RemoteFile(folder)), "@ls -1a"], cancellation);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }

    private async Task RemoveTemporaryAsync(IEnumerable<string> names, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested) return;
        try { await BatchAsync(names.Select(name => "-@rm " + Quote(RemoteFile(name))), cancellation); }
        catch (IOException) { }
    }

    private Task<string> BatchAsync(IEnumerable<string> commands, CancellationToken cancellation) =>
        RunAsync("sftp", ["-b", "-", "--", address.User + "@" + address.Host], string.Join('\n', commands) + "\n", cancellation);

    private async Task<string> RunAsync(string program, string[] arguments, string? input, CancellationToken cancellation)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "OpenSSH", program + ".exe");
        if (!File.Exists(executable)) throw new InvalidOperationException(Strings.Get("SshClientMissing"));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10",
                     "-o", "ForwardAgent=no", "-o", "PermitLocalCommand=no", "-o", "ServerAliveInterval=15",
                     "-o", "ServerAliveCountMax=2", "-P", address.Port.ToString() }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException(Strings.Get("SshInvalidTransfer"));
        using var registration = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var errors = process.StandardError.ReadToEndAsync(cancellation);
        IOException? writeError = null;
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellation);
            process.StandardInput.Close();
        }
        catch (IOException error) { writeError = error; }
        await process.WaitForExitAsync(cancellation);
        var errorText = (await errors).Trim();
        if (process.ExitCode != 0)
            throw new IOException(Strings.Get("SshTransferFailed", errorText.Length == 0 ? (await output).Trim() : errorText));
        if (writeError is not null) throw writeError;
        return await output;
    }

    private string Get(string name, string destination) => "@get " + Quote(RemoteFile(name)) + " " + Quote(LocalArgument(destination));
    private string Rename(string source, string destination) => "@rename " + Quote(RemoteFile(source)) + " " + Quote(RemoteFile(destination));
    private string LocalFile(string name) => Path.Combine(directory, name);
    private string RemoteFile(string name) => name.Length == 0 ? address.Path.TrimEnd('/') : address.Path.TrimEnd('/') + "/" + name;
    private string TemporaryFile(string name) => name + ".part-" + transferId;
    private static string LocalArgument(string path) => path.Replace('\\', '/');
    private static string Quote(string path) => "\"" + path + "\"";
    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static void CheckDatabaseState(HashSet<string> files)
    {
        if (files.Contains(Database + "-wal") || files.Contains(Database + "-journal"))
            throw new IOException(Strings.Get("SshDatabaseJournal"));
    }

    private sealed class TemporarySamples : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("discord-ssh-check-");
        public string File(int index) => Path.Combine(directory.FullName, index.ToString());
        public void Dispose() => directory.Delete(recursive: true);
    }

    public void RemoveWorkingCopy()
    {
        var path = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(path) == Cache && Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
