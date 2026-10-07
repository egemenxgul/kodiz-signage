using System.Windows;
using System.Windows.Controls;
using KodizSignage.Core.Services;
using KodizSignage.Services;
using KodizSignage.ViewModels;

namespace KodizSignage.Views;

/// <summary>Sends a full-screen notice to every screen.</summary>
public partial class AlertWindow : Window
{
    private readonly IAlertService _alerts;
    private readonly ILocalizationService _loc;

    private AlertWindow(IAlertService alerts, ILocalizationService loc)
    {
        InitializeComponent();
        Services.ThemeService.Attach(this);
        _alerts = alerts;
        _loc = loc;
        OptionItem<int> O(int value, string key) => new(value, key, loc);
        DurationBox.ItemsSource = new[] { O(5, "Alert_5min"), O(15, "Alert_15min"), O(30, "Alert_30min"), O(60, "Alert_60min"), O(0, "Alert_UntilRemoved") };
        DurationBox.SelectedValue = 15;
        StyleBox.ItemsSource = new[] { O(0, "Alert_StyleInfo"), O(1, "Alert_StyleUrgent") };
        StyleBox.SelectedValue = 0;

        // One-tap texts for the most common notices.
        foreach (var key in new[] { "Alert_QuickClosed", "Alert_QuickBack", "Alert_QuickOrder", "Alert_QuickWifi" })
        {
            var button = new Button { Content = loc.Get(key), Margin = new Thickness(0, 0, 6, 6) };
            button.Click += (_, _) =>
            {
                TitleBox.Text = loc.Get(key);
                TitleBox.Focus();
                TitleBox.CaretIndex = TitleBox.Text.Length;
            };
            QuickPanel.Children.Add(button);
        }

        if (alerts.Current is { } current)
        {
            ActiveBanner.Visibility = Visibility.Visible;
            ActiveText.Text = loc.Format("Alert_Active", current.Title.Length > 0 ? current.Title : current.Message);
            TitleBox.Text = current.Title;
            MessageBox.Text = current.Message;
        }

        Loaded += (_, _) => TitleBox.Focus();
    }

    public static void Open(Window? owner, IAlertService alerts, ILocalizationService loc)
    {
        var window = new AlertWindow(alerts, loc);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text) && string.IsNullOrWhiteSpace(MessageBox.Text))
        {
            TitleBox.Focus();
            return;
        }

        var minutes = DurationBox.SelectedValue is int m ? m : 15;
        _alerts.Show(TitleBox.Text, MessageBox.Text, StyleBox.SelectedValue is 1 ? AlertStyle.Urgent : AlertStyle.Info,
            minutes > 0 ? TimeSpan.FromMinutes(minutes) : null);
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _alerts.Clear();
        DialogResult = false;
    }
}
