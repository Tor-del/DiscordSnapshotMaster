using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace DiscordSnapshot.Gui;

public partial class AuthorizationSettings : Window
{
    private readonly string directory;
    private readonly ObservableCollection<UserRow> users;

    public sealed class UserRow
    {
        public string Id { get; set; } = "";
        public bool Admin { get; set; }
    }

    internal AuthorizationSettings(string directory)
    {
        this.directory = directory;
        InitializeComponent();
        ArchiveAuthorization settings;
        try { settings = ArchiveAuthorization.Load(directory); }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            settings = ArchiveAuthorization.Empty;
            ErrorText.Text = Strings.Get("InvalidAuthorizationSettings");
        }
        ClientId.Text = settings.ClientId;
        ClientSecret.Password = settings.ClientSecret;
        RedirectUri.Text = settings.RedirectUri;
        users = new ObservableCollection<UserRow>(settings.Users.Select(user => new UserRow { Id = user.Id, Admin = user.Admin }));
        Users.ItemsSource = users;
        Guilds.Text = string.Join(Environment.NewLine, settings.Guilds);
    }

    private void AddUserClick(object sender, RoutedEventArgs e)
    {
        var user = new UserRow();
        users.Add(user);
        Users.UpdateLayout();
        if (Users.ItemContainerGenerator.ContainerFromItem(user) is ContentPresenter row &&
            row.ContentTemplate.FindName("UserIdInput", row) is TextBox input)
        {
            input.BringIntoView();
            input.Focus();
        }
    }

    private void RemoveUserClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: UserRow user }) return;
        users.Remove(user);
        AddUserButton.Focus();
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var clientId = ClientId.Text.Trim();
        var secret = ClientSecret.Password.Trim();
        var redirect = RedirectUri.Text.Trim();
        if (!ArchiveAuthorization.IsDiscordId(clientId) || secret.Length == 0 || !ArchiveAuthorization.IsValidRedirect(redirect))
        {
            ErrorText.Text = Strings.Get("InvalidOAuthSettings");
            return;
        }

        var entries = users.Select(user => new ArchiveUser(user.Id.Trim(), user.Admin))
            .Where(user => user.Id.Length > 0 || user.Admin).ToArray();
        var guilds = Guilds.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        if (entries.Any(user => !ArchiveAuthorization.IsDiscordId(user.Id)) ||
            entries.Select(user => user.Id).Distinct().Count() != entries.Length ||
            guilds.Any(guild => !ArchiveAuthorization.IsDiscordId(guild)))
        {
            ErrorText.Text = Strings.Get("InvalidAuthorizationIds");
            return;
        }
        if (entries.Length == 0 && guilds.Length == 0)
        {
            ErrorText.Text = Strings.Get("AuthorizationListEmpty");
            return;
        }

        try
        {
            new ArchiveAuthorization(clientId, secret, redirect, entries, guilds).Save(directory);
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = error.Message;
        }
    }

    private void OpenDiscordPortal(object sender, RoutedEventArgs e) =>
        App.OpenInShell(this, "https://discord.com/developers/applications");
}
