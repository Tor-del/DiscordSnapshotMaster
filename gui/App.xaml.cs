using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Markup;

namespace DiscordSnapshot.Gui;

public partial class App : Application
{
    // Keep preferences in the existing data folder so upgrades find the same projects.
    private static readonly string LanguagePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord Snapshot", "language.txt");
    private readonly ResourceDictionary localized = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        string? language = null;
        try { language = File.ReadAllText(LanguagePath).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Resources.MergedDictionaries.Add(localized);
        ApplyLanguage(language);
        base.OnStartup(e);
    }

    internal void SelectLanguage(string language)
    {
        ApplyLanguage(language);
        var temporary = LanguagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LanguagePath)!);
            File.WriteAllText(temporary, language);
            File.Move(temporary, LanguagePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void OpenInShell(Window owner, string target)
    {
        try { using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Win32Exception error)
        {
            MessageBox.Show(owner, error.Message, Strings.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyLanguage(string? language)
    {
        Strings.SetLanguage(language);
        foreach (var (key, value) in Strings.WindowResources()) localized[key] = value;
        localized["UiCulture"] = XmlLanguage.GetLanguage(Strings.Culture.Name);
    }
}
