using System.Windows;
using System.Windows.Controls;

namespace KodizSignage.Views;

public enum MessageKind
{
    Info,
    Warning,
    Question,
}

/// <summary>Themed replacement for MessageBox (follows light/dark, large buttons for touch).</summary>
public partial class MessageWindow : Window
{
    private MessageWindow(string title, string message, MessageKind kind, string primary, string? secondary)
    {
        InitializeComponent();
        Services.ThemeService.Attach(this);
        Title = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        if (secondary is null)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
            PrimaryButton.IsCancel = true;
        }
        else
        {
            SecondaryButton.Content = secondary;
        }

        var (glyph, brush, soft) = kind switch
        {
            MessageKind.Warning => ("", "WarningBrush", "WarningSoftBrush"),
            MessageKind.Question => ("", "AccentBrush", "AccentSoftBrush"),
            _ => ("", "AccentBrush", "AccentSoftBrush"),
        };
        IconGlyph.Text = glyph;
        IconGlyph.SetResourceReference(ForegroundProperty, brush);
        IconBadge.SetResourceReference(Border.BackgroundProperty, soft);
        Loaded += (_, _) => (secondary is null ? PrimaryButton : SecondaryButton).Focus(); // Questions default to "No".
    }

    public static bool Show(Window? owner, string title, string message, MessageKind kind, string primary, string? secondary = null)
    {
        var window = new MessageWindow(title, message, kind, primary, secondary);
        if (owner is not null && owner.IsVisible)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window.ShowDialog() == true;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
