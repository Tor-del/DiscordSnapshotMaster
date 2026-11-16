using System.IO;
using System.Text.Json;

namespace DiscordSnapshot.Gui;

internal static class RecentProjectList
{
    private const int Limit = 10;
    private static string ComparisonKey(string path) =>
        path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ? path : path.ToUpperInvariant();
    private static readonly string ListPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord Snapshot", "recent-projects.json");

    public static void Clear()
    {
        try { File.Delete(ListPath); }
        catch (DirectoryNotFoundException) { }
    }

    public static string[] Read()
    {
        try
        {
            using var file = File.OpenRead(ListPath);
            return JsonSerializer.Deserialize<string[]>(file)?
                .Where(path => !string.IsNullOrWhiteSpace(path)).DistinctBy(ComparisonKey).Take(Limit).ToArray() ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static void Remember(string path)
    {
        try
        {
            var key = ComparisonKey(path);
            Write(Read().Where(previous => ComparisonKey(previous) != key).Prepend(path).Take(Limit).ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Recent projects are optional and the selected project is already open.
        }
    }

    public static void Remove(string path)
    {
        var key = ComparisonKey(path);
        var paths = Read();
        var remaining = paths.Where(previous => ComparisonKey(previous) != key).ToArray();
        if (remaining.Length != paths.Length) Write(remaining);
    }

    private static void Write(string[] paths)
    {
        var temporary = ListPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ListPath)!);
            using (var file = File.Create(temporary)) JsonSerializer.Serialize(file, paths);
            File.Move(temporary, ListPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
        }
    }
}
