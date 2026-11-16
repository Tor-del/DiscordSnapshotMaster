using System.IO;

namespace DiscordSnapshot.Gui;

internal static class ArchiveWebUi
{
    private const string FolderName = "web-ui";
    private const string MarkerName = ".discord-snapshot-web-ui";
    private const string LegacyMarker = "Discord Snapshot Web UI\n";
    private const string PreviousMarker = "Discord Snapshot Web UI v3\n";
    private const string Marker = "Discord Snapshot Web UI v4\n";
    private const string ResourcePrefix = "ArchiveWebUi/";
    private static readonly string[] Files = typeof(ArchiveWebUi).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        .Select(name => name[ResourcePrefix.Length..]).ToArray();

    public static bool IsInstalled(string archive)
    {
        var folder = Path.Combine(archive, FolderName);
        var marker = Path.Combine(folder, MarkerName);
        try
        {
            return Directory.Exists(folder) && !IsLink(folder) && File.Exists(marker) && !IsLink(marker)
                && new FileInfo(marker).Length <= Marker.Length && File.ReadAllText(marker) is Marker or PreviousMarker or LegacyMarker;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public static void SetInstalled(string archive, bool install)
    {
        var folder = Path.Combine(archive, FolderName);
        if (install)
        {
            var installed = IsInstalled(archive);
            if (installed && File.ReadAllText(Path.Combine(folder, MarkerName)) == Marker) return;
            if (!installed && Path.Exists(folder))
                throw new IOException(Strings.Get("ViewerFolderOccupied"));

            var staging = Path.Combine(archive, $".{FolderName}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var name in Files)
                {
                    using var resource = typeof(ArchiveWebUi).Assembly.GetManifestResourceStream(ResourcePrefix + name)
                        ?? throw new InvalidOperationException(Strings.Get("ViewerResourceMissing", name));
                    using var output = File.Open(Path.Combine(staging, name), FileMode.CreateNew);
                    resource.CopyTo(output);
                }
                File.WriteAllText(Path.Combine(staging, MarkerName), Marker);
                if (installed)
                {
                    foreach (var name in Files.OrderBy(name => name == "server.py"))
                        File.Move(Path.Combine(staging, name), Path.Combine(folder, name), overwrite: true);
                    File.Move(Path.Combine(staging, MarkerName), Path.Combine(folder, MarkerName), overwrite: true);
                }
                else Directory.Move(staging, folder);
            }
            finally
            {
                if (Directory.Exists(staging)) RemoveFiles(staging);
            }
        }
        else if (IsInstalled(archive))
        {
            RemoveFiles(folder);
        }
    }

    private static void RemoveFiles(string folder)
    {
        foreach (var name in Files) File.Delete(Path.Combine(folder, name));
        File.Delete(Path.Combine(folder, MarkerName));
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
}
