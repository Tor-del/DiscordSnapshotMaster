using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Automation;
using System.Windows.Input;

namespace DiscordSnapshot.Gui;

public partial class MainWindow : Window
{
    private readonly SnapshotImporter importer;
    private readonly ProjectFile project;
    private readonly ProjectPicker picker;
    private readonly SshProject? remote;
    private ChannelChoice[] availableChannels = [];
    private ChannelRow[] channelRows = [];
    private ICollectionView? channelView;
    private DiscordClient? discord;
    private byte[]? connectedToken;
    private CancellationTokenSource? operation;
    private ViewerSession? viewer;
    private bool copying;
    private bool archiveReady;
    private bool closeWhenReady;
    private bool remoteCloseReady;
    private bool remoteClosing;
    private bool keepWorkingCopy;
    private int journalLines;
    private bool Busy => operation is not null;
    private bool CanCheckArchive => !Busy && archiveReady && File.Exists(Path.Combine(project.DirectoryPath, "snapshot.sqlite"));
    internal bool ReturnToPicker { get; private set; }

    internal MainWindow(ProjectFile project, ProjectPicker picker, SshProject? remote = null)
    {
        this.project = project;
        this.picker = picker;
        this.remote = remote;
        importer = new SnapshotImporter(publishMedia: remote is null ? null : remote.UploadMediaAsync);
        archiveReady = project.Archive is not null;
        InitializeComponent();
        Journal.Document.FontFamily = FontFamily;
        Journal.Document.FontSize = Journal.FontSize;
        var settings = project.Settings;
        ApiInterval.Text = (settings?.ApiInterval ?? 2000).ToString();
        CdnInterval.Text = (settings?.CdnInterval ?? 1000).ToString();
        if ((settings?.Server ?? project.Archive?.Server) is { } server)
        {
            Servers.ItemsSource = new[] { server };
            Servers.SelectedIndex = 0;
        }
        SetChannelRows();
        ProjectNameText.Text = project.Name ?? remote?.ProjectFolderName ?? ProjectFile.DefaultName(project.DirectoryPath);
        ProjectPathText.Text = remote?.RemotePath ?? project.DirectoryPath;
        ProjectPathText.ToolTip = remote is null ? project.DirectoryPath : Strings.Get("SshWorkingCopy", project.DirectoryPath);
        SshSaveButton.Visibility = remote is null ? Visibility.Collapsed : Visibility.Visible;
        OpenProjectFolderButton.Visibility = remote is null ? Visibility.Visible : Visibility.Collapsed;
        WebUiCheckBox.Visibility = remote is null ? Visibility.Visible : Visibility.Collapsed;
        WebUiCheckBox.IsChecked = ArchiveWebUi.IsInstalled(project.DirectoryPath);
        if (WebUiCheckBox.IsChecked == true) ArchiveWebUi.SetInstalled(project.DirectoryPath, true);
        AuthorizationCheckBox.IsChecked = ArchiveAuthorization.IsEnabled(project.DirectoryPath);
        StatusText.Text = project.Status switch
        {
            ProjectStatus.Completed => Strings.Get("ConnectToAddChannels"),
            ProjectStatus.Copying when settings?.Token is null => Strings.Get("EnterTokenToResume"),
            ProjectStatus.Copying => Strings.Get("ClickResume"),
            _ => Strings.Get("EnterTokenToLoadServers"),
        };
        AppendLog(StatusText.Text);
        RefreshControls();
    }

    private static string Clean(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private sealed class ChannelRow(ChannelChoice channel, bool available, bool pending, DateTimeOffset? copiedAt) : INotifyPropertyChanged
    {
        private bool isChecked = pending;
        public ChannelChoice Channel { get; } = channel;
        public bool CanSelect { get; } = available && !pending;
        public bool IsArchived => copiedAt.HasValue;
        public string Detail => copiedAt is { } date ? Strings.Get("ChannelCopiedAt", date.ToLocalTime()) : Channel.KindLabel;
        public bool IsChecked
        {
            get => isChecked;
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void SetChannelRows()
    {
        var pending = (project.Settings?.Channels ?? (project.CanResume ? project.Archive?.Channels : null)) ?? [];
        var pendingIds = pending.Select(channel => channel.Id).ToHashSet();
        var archived = project.Archive?.Channels ?? [];
        var availableIds = availableChannels.Select(channel => channel.Id).ToHashSet();
        var choices = project.CanResume ? pending.Concat(archived) : availableChannels.Concat(archived);
        channelRows = choices.DistinctBy(channel => channel.Id)
            .Select(channel => new ChannelRow(channel with { Title = Clean(channel.Title) },
                availableIds.Contains(channel.Id), pendingIds.Contains(channel.Id),
                project.Archive?.CopiedAt.GetValueOrDefault(channel.Id) is { } date && date != default ? date : null))
            .ToArray();
        channelView = new ListCollectionView(channelRows) { Filter = MatchesSearch };
        Channels.ItemsSource = channelView;
    }

    private bool MatchesSearch(object item)
    {
        var channel = ((ChannelRow)item).Channel;
        var query = ChannelSearch.Text.Trim();
        return channel.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || channel.Id.Contains(query, StringComparison.Ordinal);
    }

    private void ChannelSearchChanged(object sender, TextChangedEventArgs e)
    {
        channelView?.Refresh();
        RefreshControls();
    }

    private void ChannelBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // Keep a partially visible checkbox under the pointer until its click is released.
        if (Mouse.LeftButton == MouseButtonState.Pressed) e.Handled = true;
    }

    private void RefreshControls()
    {
        if (Activity is null) return;
        var configuring = !Busy && !project.CanResume;
        var connected = discord is not null;
        var selected = channelRows.Count(row => row.CanSelect && row.IsChecked);
        ApiInterval.IsEnabled = CdnInterval.IsEnabled = SlowButton.IsEnabled = !Busy;
        TokenBox.IsEnabled = !Busy && !connected;
        TokenInputRow.Visibility = TokenLabel.Visibility =
            connected ? Visibility.Collapsed : Visibility.Visible;
        using var password = TokenBox.SecurePassword;
        ConnectButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        ConnectButton.IsEnabled = configuring && !connected && password.Length > 0;
        DisconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        DisconnectButton.IsEnabled = !Busy && connected;
        Servers.IsEnabled = configuring && project.Status == ProjectStatus.New && connected;
        ChannelSearch.IsEnabled = channelRows.Length > 0;
        Channels.IsEnabled = !Busy && !project.CanResume;
        var selectAllLabel = Strings.Get(project.Status == ProjectStatus.Completed ? "UiSelectNewChannels" : "UiSelectAll");
        SelectAllButton.ToolTip = selectAllLabel;
        AutomationProperties.SetName(SelectAllButton, selectAllLabel);
        SelectAllButton.IsEnabled = configuring && Channels.Items.OfType<ChannelRow>().Any(row => row.CanSelect && !row.IsArchived && !row.IsChecked);
        ClearButton.IsEnabled = configuring && selected > 0;
        CopyButton.Content = copying ? Strings.Get("Pause") : project.CanResume ? Strings.Get("Resume")
            : Strings.Get(project.Status == ProjectStatus.Completed ? "UiSyncChannels" : "UiStart");
        CopyButton.IsEnabled = copying ? operation is { IsCancellationRequested: false }
            : project.CanResume ? !Busy && (project.Settings?.Token is not null || password.Length > 0)
            : configuring && discord is not null && Servers.SelectedItem is DiscordServer && selected > 0;
        CheckButton.IsEnabled = CanCheckArchive;
        SshSaveButton.IsEnabled = !Busy;
        RenameProjectButton.IsEnabled = !Busy;
        WebUiCheckBox.IsEnabled = !Busy;
        AuthorizationCheckBox.IsEnabled = AuthorizationSettingsButton.IsEnabled = !Busy;
        AuthorizationSettingsButton.Visibility = AuthorizationCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewArchiveButton.Visibility = project.Status == ProjectStatus.Completed && (remote is not null || WebUiCheckBox.IsChecked == true)
            ? Visibility.Visible : Visibility.Collapsed;
        ViewArchiveButton.IsEnabled = !Busy;
        Activity.IsIndeterminate = Busy;
        ProjectStatusText.Text = copying ? Strings.Get("Copying") : project.Status switch
        {
            ProjectStatus.New => Strings.Get("NewProject"),
            ProjectStatus.Copying => Strings.Get("CopyIncomplete"),
            ProjectStatus.Completed => Strings.Get("CopyComplete"),
            _ => "",
        };
        var selectable = channelRows.Count(row => row.CanSelect);
        SelectionText.Text = channelRows.Length > 0 && !Channels.HasItems ? Strings.Get("NoSearchMatches")
            : configuring && selectable > 0 ? Strings.Get("SelectedChannelCount", selected, selectable)
            : channelRows.Length > 0 ? Strings.Get("ProjectChannelCount", channelRows.Length)
            : Servers.SelectedItem is null ? Strings.Get("UiSelectServer") : Strings.Get("NoChannelsLoaded");
    }

    private bool ReadIntervals(out int api, out int cdn)
    {
        var validApi = int.TryParse(ApiInterval.Text, out api) && api >= 30;
        var validCdn = int.TryParse(CdnInterval.Text, out cdn) && cdn >= 0;
        if (!validApi) StatusText.Text = Strings.Get("InvalidApiInterval");
        else if (!validCdn) StatusText.Text = Strings.Get("InvalidCdnInterval");
        if (!validApi) ApiInterval.Focus();
        else if (!validCdn) CdnInterval.Focus();
        return validApi && validCdn;
    }

    private void AppendLog(string text)
    {
        var line = new Paragraph { Margin = new Thickness(0) };
        ChannelTitle.AppendEmojiText(line.Inlines, Clean(text), Journal.FontSize);
        Journal.Document.Blocks.Add(line);
        // Keep a bounded history in the window for long-running imports.
        if (++journalLines > 3000)
        {
            for (var index = 0; index < 500; index++)
                Journal.Document.Blocks.Remove(Journal.Document.Blocks.FirstBlock);
            journalLines -= 500;
        }
        Journal.ScrollToEnd();
    }

    private void ReportLog(string text) => Dispatcher.Invoke(() => AppendLog(text));

    private void CopyJournalText(object sender, ExecutedRoutedEventArgs e)
    {
        var end = Journal.Selection.End;
        var text = new StringBuilder();
        for (TextPointer? position = Journal.Selection.Start; position is not null && position.CompareTo(end) < 0;
             position = position.GetNextContextPosition(LogicalDirection.Forward))
        {
            switch (position.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    var length = Math.Min(position.GetTextRunLength(LogicalDirection.Forward), position.GetOffsetToPosition(end));
                    text.Append(position.GetTextInRun(LogicalDirection.Forward), 0, length);
                    break;
                case TextPointerContext.EmbeddedElement when position.GetAdjacentElement(LogicalDirection.Forward) is Image { ToolTip: string emoji }:
                    text.Append(emoji);
                    break;
                case TextPointerContext.ElementEnd when position.GetAdjacentElement(LogicalDirection.Forward) is Paragraph:
                    text.AppendLine();
                    break;
            }
        }
        if (text.Length > 0) Clipboard.SetDataObject(text.ToString(), copy: true);
        e.Handled = true;
    }

    private void ResetConnection()
    {
        discord = null;
        connectedToken = null;
        var server = project.Settings?.Server ?? project.Archive?.Server;
        Servers.ItemsSource = server is null ? null : new[] { server };
        if (server is not null) Servers.SelectedIndex = 0;
        availableChannels = [];
        ChannelSearch.Clear();
        SetChannelRows();
        ConnectionText.Text = Strings.Get("UiDisconnected");
    }

    private async Task RunAsync<T>(string status, Func<CancellationToken, Task<T>> run, Func<T, string> complete, bool isCopy = false)
    {
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        copying = isCopy;
        StatusText.Text = status;
        RefreshControls();
        try
        {
            var result = await Task.Run(() => run(cancellation.Token));
            StatusText.Text = complete(result);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = isCopy ? Strings.Get("CopyPaused") : Strings.Get("OperationStopped");
        }
        catch (Exception error)
        {
            StatusText.Text = Clean(error.Message);
        }
        finally
        {
            AppendLog(StatusText.Text);
            operation = null;
            copying = false;
            RefreshControls();
            if (closeWhenReady) Close();
        }
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (Busy || discord is not null || project.CanResume || !ReadIntervals(out var apiInterval, out _)) return;
        var token = TokenBox.Password.Trim();
        if (token.Length == 0) return;
        var archiveServer = project.Archive?.Server;
        ResetConnection();
        await RunAsync(Strings.Get(archiveServer is null ? "LoadingServers" : "LoadingChannels"), async cancellation =>
        {
            var client = new DiscordClient(token) { IntervalMs = apiInterval };
            var response = await client.ConnectAsync(cancellation);
            var channels = archiveServer is null ? null : await client.LoadChannelsAsync(archiveServer.Id, cancellation);
            return (Client: client, Response: response, Token: CopySettings.ProtectToken(token), Channels: channels);
        }, result =>
        {
            discord = result.Client;
            connectedToken = result.Token;
            TokenBox.Clear();
            ConnectionText.Text = Strings.Get("ConnectedBot", Clean(result.Response.Username));
            if (result.Channels is not null)
            {
                availableChannels = result.Channels.ToArray();
                SetChannelRows();
                return Strings.Get(channelRows.Any(row => row.CanSelect) ? "SelectChannelsToSync" : "NoChannelHistory");
            }
            var servers = result.Response.Servers.Select(server => server with { Name = Clean(server.Name) })
                .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            Servers.ItemsSource = servers;
            return servers.Length == 0 ? Strings.Get("NoServers") : Strings.Get("SelectServer");
        });
    }

    private void DisconnectClick(object sender, RoutedEventArgs e)
    {
        if (Busy || discord is null) return;
        try
        {
            if (project.Settings is { Token: not null } settings) project.SaveSettings(settings with { Token = null });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Clean(error.Message);
            AppendLog(StatusText.Text);
            return;
        }

        ResetConnection();
        TokenBox.Clear();
        StatusText.Text = project.Status switch
        {
            ProjectStatus.Copying => Strings.Get("EnterTokenToResume"),
            ProjectStatus.Completed => Strings.Get("EnterReplacementBotToken"),
            _ => Strings.Get("EnterTokenToLoadServers"),
        };
        AppendLog(StatusText.Text);
        RefreshControls();
        TokenBox.Focus();
    }

    private async void ServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Busy || project.Status != ProjectStatus.New || discord is not { } client || Servers.SelectedItem is not DiscordServer server) return;
        availableChannels = [];
        ChannelSearch.Clear();
        SetChannelRows();
        if (!ReadIntervals(out var apiInterval, out _))
        {
            Servers.SelectedIndex = -1;
            return;
        }
        client.IntervalMs = apiInterval;
        var loaded = false;
        await RunAsync(Strings.Get("LoadingChannels"), cancellation => client.LoadChannelsAsync(server.Id, cancellation), response =>
        {
            availableChannels = response.ToArray();
            SetChannelRows();
            loaded = true;
            return availableChannels.Length == 0 ? Strings.Get("NoChannelHistory")
                : Strings.Get("SelectMultipleChannels");
        });
        if (!loaded)
        {
            Servers.SelectedIndex = -1;
            RefreshControls();
        }
    }

    private async void CopyClick(object sender, RoutedEventArgs e)
    {
        if (copying) Stop();
        else if (project.CanResume) await ResumeCopyAsync();
        else await StartCopyAsync();
    }

    private async Task StartCopyAsync()
    {
        if (Busy || discord is not { } client ||
            connectedToken is not { } token || Servers.SelectedItem is not DiscordServer server ||
            !ReadIntervals(out var apiInterval, out var cdnInterval)) return;
        var channels = channelRows.Where(row => row.CanSelect && row.IsChecked).Select(row => row.Channel).ToArray();
        if (channels.Length == 0 || new CopyConfirmation(server, channels, project.Archive?.CopiedAt) { Owner = this }.ShowDialog() != true) return;
        client.IntervalMs = apiInterval;
        var settings = new CopySettings(server, channels, apiInterval, cdnInterval, token, Guid.NewGuid().ToString("N"));
        var existingArchive = project.Status == ProjectStatus.Completed;
        await RunCopyAsync(Strings.Get("CopyingChannels"), async cancellation =>
        {
            project.SaveSettings(settings);
            return existingArchive ? await CopyChannelsAsync(client, settings, cancellation)
                : await ImportNewArchiveAsync(client, settings, cancellation);
        });
    }

    private async Task ResumeCopyAsync()
    {
        if (Busy || !ReadIntervals(out var apiInterval, out var cdnInterval)) return;
        var replacement = TokenBox.Password.Trim();
        await RunCopyAsync(Strings.Get("ResumingCopy"), async cancellation =>
        {
            var progress = SnapshotImporter.InspectProgress(project.DirectoryPath);
            if (progress is not null) MarkArchiveReady();
            var copyStarted = progress is not null && project.IsCurrentCopy(progress);
            if (progress is { Incomplete: false } && copyStarted) return new ImportResult(project.DirectoryPath, false);

            var previous = project.Settings;
            var server = previous?.Server ?? progress?.Server ?? throw new InvalidDataException(Strings.Get("MissingProjectServer"));
            var channels = previous?.Channels ?? progress!.Channels;
            var token = replacement.Length > 0 ? replacement : previous?.ReadToken()
                ?? throw new InvalidOperationException(Strings.Get("EnterBotToken"));
            var client = new DiscordClient(token) { IntervalMs = apiInterval };
            await client.ConnectAsync(cancellation);
            var protectedToken = replacement.Length > 0 ? CopySettings.ProtectToken(token) : previous!.Token;
            var settings = new CopySettings(server, channels, apiInterval, cdnInterval, protectedToken,
                previous?.OperationId ?? progress!.OperationId);
            project.SaveSettings(settings);
            if (progress is null) return await ImportNewArchiveAsync(client, settings, cancellation);
            if (!copyStarted) return await CopyChannelsAsync(client, settings, cancellation);
            return await importer.ResumeAsync(client, project.DirectoryPath, cdnInterval, ReportLog, _ => { }, cancellation);
        });
    }

    private void MarkArchiveReady() => Dispatcher.Invoke(() => archiveReady = true);

    private Task<ImportResult> ImportNewArchiveAsync(DiscordClient client, CopySettings settings,
        CancellationToken cancellation) => importer.StartInDirectoryAsync(client, settings.Server.Id,
            settings.Channels.Select(channel => channel.Id).ToArray(), project.DirectoryPath, settings.CdnInterval, ReportLog, cancellation,
            _ => MarkArchiveReady(), settings.OperationId);

    private Task<ImportResult> CopyChannelsAsync(DiscordClient client, CopySettings settings, CancellationToken cancellation) =>
        importer.CopyChannelsAsync(client, project.DirectoryPath, settings.Channels.Select(channel => channel.Id).ToArray(),
            settings.OperationId, settings.CdnInterval, ReportLog, cancellation);

    private Task RunCopyAsync(string status, Func<CancellationToken, Task<ImportResult>> run)
    {
        StopViewer();
        return RunAsync(status, async cancellation =>
        {
            var result = await run(cancellation);
            project.RefreshArchive();
            if (!result.Incomplete) project.Complete();
            Exception? syncError = null;
            if (remote is not null)
            {
                try { await remote.SaveAsync(cancellation); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    syncError = error;
                }
            }
            return (Result: result, SyncError: syncError);
        }, outcome =>
        {
            TokenBox.Clear();
            SetChannelRows();
            var message = Strings.Get(outcome.Result.Incomplete ? "ArchiveHasGaps" : "CopyFinished");
            if (outcome.SyncError is not null) return Strings.Get("SshCopySyncFailed", message, outcome.SyncError.Message);
            return remote is null ? message : message + " " + Strings.Get("SshSaved");
        }, isCopy: true);
    }

    private async void CheckClick(object sender, RoutedEventArgs e)
    {
        if (!CanCheckArchive) return;
        await RunAsync(Strings.Get("CheckingArchive"), async cancellation =>
        {
            if (remote is not null) await remote.DownloadMediaAsync(cancellation);
            return await SnapshotImporter.CheckAsync(project.DirectoryPath, ReportLog, cancellation);
        }, result =>
        {
            if (result.DamagedFiles > 0) return Strings.Get("DamagedFileCount", result.DamagedFiles);
            return result.Incomplete ? Strings.Get("FilesIntactWithGaps") : Strings.Get("ArchivePassed");
        });
    }

    private void WebUiClick(object sender, RoutedEventArgs e)
    {
        var install = WebUiCheckBox.IsChecked == true;
        if (!install) StopViewer();
        var title = install ? Strings.Get("AddingViewer") : Strings.Get("RemovingViewer");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title });
        panel.Children.Add(new ProgressBar { Height = 5, Margin = new Thickness(0, 14, 0, 0), IsIndeterminate = true });
        var dialog = new Window
        {
            Owner = this, Title = Strings.Get("ArchiveViewer"), Content = panel,
            Width = 360, SizeToContent = SizeToContent.Height, ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var finished = false;
        Exception? failure = null;
        dialog.Closing += (_, args) => args.Cancel = !finished;
        dialog.Loaded += async (_, _) =>
        {
            try { await Task.Run(() => ArchiveWebUi.SetInstalled(project.DirectoryPath, install)); }
            catch (Exception error) { failure = error; }
            finally
            {
                finished = true;
                dialog.Close();
            }
        };
        dialog.ShowDialog();
        WebUiCheckBox.IsChecked = ArchiveWebUi.IsInstalled(project.DirectoryPath);
        RefreshControls();
        if (failure is not null)
            MessageBox.Show(this, failure.Message, dialog.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            AppendLog(install ? Strings.Get("ViewerAdded") : Strings.Get("ViewerRemoved"));
    }

    private void OpenProjectFolderClick(object sender, RoutedEventArgs e) => App.OpenInShell(this, project.DirectoryPath);

    private async void RenameProjectClick(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var dialog = new ProjectNameDialog(ProjectNameText.Text, creating: false) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ProjectName == ProjectNameText.Text) return;
        try
        {
            project.Rename(dialog.ProjectName);
            ProjectNameText.Text = dialog.ProjectName;
            RecentProjectList.Remember(remote?.HistoryKey ?? project.DirectoryPath, project.Name);
            if (remote is not null)
                await RunAsync(Strings.Get("SshSaving"), async cancellation =>
                {
                    await remote.SaveProjectNameAsync(cancellation);
                    return Strings.Get("SshSaved");
                }, message => message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, Strings.Get("UiRenameProject"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AuthorizationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            StopViewer();
            var enabled = AuthorizationCheckBox.IsChecked == true;
            if (enabled && ArchiveWebUi.IsInstalled(project.DirectoryPath))
                ArchiveWebUi.SetInstalled(project.DirectoryPath, true);
            ArchiveAuthorization.SetEnabled(project.DirectoryPath, enabled);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, error.Message, Strings.Get("UiAuthorizationSettings"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        AuthorizationCheckBox.IsChecked = ArchiveAuthorization.IsEnabled(project.DirectoryPath);
        RefreshControls();
    }

    private async void AuthorizationSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (new AuthorizationSettings(project.DirectoryPath) { Owner = this }.ShowDialog() != true) return;
            StopViewer();
            if (remote is not null)
                await RunAsync(Strings.Get("SshSaving"), async cancellation =>
                {
                    await remote.SaveAuthorizationAsync(cancellation);
                    return Strings.Get("SshAuthorizationSaved");
                }, message => message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, error.Message, Strings.Get("UiAuthorizationSettings"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TokenHelpClick(object sender, RoutedEventArgs e) => new TokenHelp { Owner = this }.ShowDialog();

    private async void ViewArchiveClick(object sender, RoutedEventArgs e)
    {
        if (Busy || project.Status != ProjectStatus.Completed || (remote is null && WebUiCheckBox.IsChecked != true)) return;
        if (viewer is { IsRunning: true })
        {
            App.OpenInShell(this, viewer.Address);
            return;
        }

        StopViewer();
        await RunAsync(Strings.Get("StartingViewer"), async cancellation =>
        {
            if (remote is not null) await remote.DownloadMediaAsync(cancellation);
            return await ViewerSession.StartAsync(project.DirectoryPath, cancellation);
        }, session =>
        {
            viewer = session;
            if (!closeWhenReady) App.OpenInShell(this, session.Address);
            return Strings.Get("ViewerRunning");
        });
    }

    private void StopViewer()
    {
        viewer?.Dispose();
        viewer = null;
    }

    private void Stop()
    {
        if (operation is not { IsCancellationRequested: false } cancellation) return;
        StatusText.Text = copying ? Strings.Get("PausingCopy") : Strings.Get("StoppingOperation");
        cancellation.Cancel();
        RefreshControls();
    }

    private void InputsChanged(object sender, RoutedEventArgs e) => RefreshControls();
    private void SelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in Channels.Items.OfType<ChannelRow>().Where(row => row.CanSelect && !row.IsArchived)) row.IsChecked = true;
        RefreshControls();
    }

    private void ClearClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in channelRows.Where(row => row.CanSelect)) row.IsChecked = false;
        RefreshControls();
    }

    private void CloseProjectClick(object sender, RoutedEventArgs e)
    {
        ReturnToPicker = true;
        Close();
    }

    private async void SshSaveClick(object sender, RoutedEventArgs e)
    {
        if (!Busy && remote is not null) await SaveRemoteAsync();
    }

    private async Task<bool> SaveRemoteAsync()
    {
        StopViewer();
        var saved = false;
        await RunAsync(Strings.Get("SshSaving"), async cancellation =>
        {
            await remote!.SaveAsync(cancellation);
            return true;
        }, result =>
        {
            saved = result;
            return Strings.Get("SshSaved");
        });
        return saved;
    }

    private async Task SaveAndCloseRemoteAsync()
    {
        remoteClosing = true;
        await Task.Yield();
        if (!await SaveRemoteAsync())
        {
            var message = Strings.Get("SshCloseWithWorkingCopy", StatusText.Text, remote!.DirectoryPath);
            if (MessageBox.Show(this, message, Strings.Get("UiOpenSshProject"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                ReturnToPicker = false;
                remoteClosing = false;
                return;
            }
            keepWorkingCopy = true;
        }
        remoteCloseReady = true;
        remoteClosing = false;
        Close();
    }

    private void SlowClick(object sender, RoutedEventArgs e)
    {
        ApiInterval.Text = "5000";
        CdnInterval.Text = "3000";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (remoteClosing)
        {
            e.Cancel = true;
            return;
        }
        if (Busy)
        {
            e.Cancel = true;
            closeWhenReady = true;
            Stop();
        }
        else if (remote is not null && !remoteCloseReady)
        {
            e.Cancel = true;
            closeWhenReady = false;
            _ = SaveAndCloseRemoteAsync();
        }
        else if (ReturnToPicker) Application.Current.MainWindow = picker;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopViewer();
        project.Dispose();
        if (remote is not null && !keepWorkingCopy)
        {
            try { remote.RemoveWorkingCopy(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        base.OnClosed(e);
    }
}
