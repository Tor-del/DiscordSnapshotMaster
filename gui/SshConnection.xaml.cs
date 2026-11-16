using System.Windows;

namespace DiscordSnapshot.Gui;

public partial class SshConnection : Window
{
    internal SshAddress? Address { get; private set; }
    internal string? NewProjectName { get; private set; }

    public SshConnection() => InitializeComponent();

    private void ConnectClick(object sender, RoutedEventArgs e)
    {
        var host = HostName.Text.Trim();
        var user = UserName.Text.Trim();
        var path = RemoteDirectory.Text.Trim();
        Address = int.TryParse(Port.Text, out var port) ? SshAddress.Create(host, user, port, path) : null;
        if (Address is null)
        {
            ErrorText.Text = Strings.Get("SshInvalidAddress");
            return;
        }
        var name = NameInput.Text.Trim();
        NewProjectName = name.Length > 0 ? name : null;
        DialogResult = true;
    }
}
