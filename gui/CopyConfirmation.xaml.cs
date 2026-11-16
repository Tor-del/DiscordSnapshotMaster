using System.Windows;

namespace DiscordSnapshot.Gui;

public partial class CopyConfirmation : Window
{
    internal CopyConfirmation(DiscordServer server, ChannelChoice[] channels, IReadOnlyDictionary<string, DateTimeOffset>? copiedAt)
    {
        InitializeComponent();
        SelectionSummary.Text = Strings.Get("ConfirmCopySummary", server.Label, channels.Length);
        SelectedChannels.ItemsSource = channels.Select(channel => new
        {
            channel.Id,
            channel.Title,
            Action = Strings.Get(copiedAt?.ContainsKey(channel.Id) == true ? "ConfirmUpdateChannel" : "ConfirmNewChannel"),
        });
    }

    private void ConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
