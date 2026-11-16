using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DiscordSnapshot.Gui;

public partial class ProjectPicker : Window
{
    private sealed record RecentProject(string Key, string Name, string Location);

    public ProjectPicker()
    {
        InitializeComponent();
        LanguagePicker.SelectedValue = Strings.Language;
        RefreshRecentProjects();
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguagePicker.SelectedValue is not string language || language == Strings.Language) return;
        ((App)Application.Current).SelectLanguage(language);
        ErrorText.Text = "";
    }

    private void RefreshRecentProjects()
    {
        var entries = RecentProjectList.Read();
        RecentProjects.ItemsSource = entries.Select(entry =>
        {
            var address = SshAddress.Parse(entry.Path);
            var defaultName = address?.ProjectFolderName ?? ProjectFile.DefaultName(entry.Path);
            return new RecentProject(entry.Path, string.IsNullOrWhiteSpace(entry.Name) ? defaultName : entry.Name,
                address?.ToString() ?? entry.Path);
        }).ToArray();
        EmptyText.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = entries.Length > 0;
    }

    private void ClearHistoryClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            RecentProjectList.Clear();
            RefreshRecentProjects();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = error.Message;
        }
    }

    private void RemoveRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RecentProject item }) return;
        ErrorText.Text = "";
        try
        {
            RecentProjectList.Remove(item.Key);
            RefreshRecentProjects();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = error.Message;
        }
    }

    private void PickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Strings.Get("PickProject"),
        };
        var initial = SnapshotImporter.DefaultArchiveRoot;
        if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog(this) == true) OpenProject(dialog.FolderName, createIfMissing: true);
    }

    private async void RecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RecentProject item }) return;
        if (SshAddress.Parse(item.Key) is { } address) await OpenSshAsync(address, createIfMissing: false);
        else OpenProject(item.Key, createIfMissing: false);
    }

    private async void PickSsh(object sender, RoutedEventArgs e)
    {
        var dialog = new SshConnection { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Address is not { } address) return;
        await OpenSshAsync(address, createIfMissing: true, dialog.NewProjectName);
    }

    private async Task OpenSshAsync(SshAddress address, bool createIfMissing, string? newProjectName = null)
    {
        IsEnabled = false;
        ErrorText.Text = Strings.Get("SshDownloading");
        SshProject? remote = null;
        ProjectFile? project = null;
        try
        {
            remote = await Task.Run(() => SshProject.DownloadAsync(address, createIfMissing, CancellationToken.None));
            project = createIfMissing
                ? ProjectFile.OpenOrCreate(remote.DirectoryPath, () => newProjectName ?? address.ProjectFolderName)!
                : ProjectFile.Open(remote.DirectoryPath);
            await Task.Run(() => remote.SaveAsync(CancellationToken.None));
            ShowProject(project, remote);
            RecentProjectList.Remember(address.HistoryKey, project.Name);
        }
        catch (Exception error)
        {
            project?.Dispose();
            ErrorText.Text = remote is null ? error.Message : Strings.Get("SshWorkingCopyRetained", error.Message, remote.DirectoryPath);
        }
        finally { IsEnabled = true; }
    }

    private void OpenProject(string path, bool createIfMissing)
    {
        ErrorText.Text = "";
        ProjectFile? project = null;
        try
        {
            project = createIfMissing ? ProjectFile.OpenOrCreate(path, () => AskProjectName(path)) : ProjectFile.Open(path);
            if (project is null) return;
            ShowProject(project);
        }
        catch (Exception error)
        {
            project?.Dispose();
            ErrorText.Text = error.Message;
            return;
        }
        RecentProjectList.Remember(project.DirectoryPath, project.Name);
    }

    private string? AskProjectName(string path)
    {
        var dialog = new ProjectNameDialog(ProjectFile.DefaultName(path), creating: true) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.ProjectName : null;
    }

    private void ShowProject(ProjectFile project, SshProject? remote = null)
    {
        ErrorText.Text = "";
        var window = new MainWindow(project, this, remote);
        window.Closed += (_, _) =>
        {
            if (!window.ReturnToPicker || Dispatcher.HasShutdownStarted) return;
            RefreshRecentProjects();
            Show();
            Activate();
        };
        window.Show();
        Application.Current.MainWindow = window;
        Hide();
    }
}
