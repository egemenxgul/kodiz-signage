using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Services;
using KodizSignage.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KodizSignage.Views;

/// <summary>First start: language → screens → content and startup. Everything can be changed later.</summary>
public partial class WizardWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _loc;
    private readonly IPlaylistService _playlist;
    private readonly MediaViewModel _media;
    private readonly GeneralViewModel _general;
    private readonly List<DisplayOption> _displays;
    private int _page = 1;

    public sealed class DisplayOption
    {
        public DisplayOption(DisplayInfo display, string title, string detail)
        {
            Display = display;
            Title = title;
            Detail = detail;
        }

        public DisplayInfo Display { get; }
        public string Title { get; }
        public string Detail { get; }
        public bool Use { get; set; } = true;
    }

    public WizardWindow(IServiceProvider services)
    {
        InitializeComponent();
        _settings = services.GetRequiredService<ISettingsService>();
        _loc = services.GetRequiredService<ILocalizationService>();
        _playlist = services.GetRequiredService<IPlaylistService>();
        _media = services.GetRequiredService<MediaViewModel>();
        _general = services.GetRequiredService<GeneralViewModel>();

        _displays = services.GetRequiredService<IDisplayService>().GetDisplays()
            .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.X).ThenBy(d => d.Y)
            .Select((d, i) => new DisplayOption(d, $"{i + 1} · {d.FriendlyName}",
                $"{d.Width} × {d.Height}{(d.IsPrimary ? " · " + _loc.Get("Wizard_Primary") : string.Empty)}"))
            .ToList();
        DisplayList.ItemsSource = _displays;

        AutostartBox.IsChecked = _settings.Current.StartWithWindows;
        AutoplayBox.IsChecked = _settings.Current.AutoPlayOnLaunch;
        _playlist.Changed += OnPlaylistChanged;
        Closed += (_, _) => _playlist.Changed -= OnPlaylistChanged;
        _loc.LanguageChanged += (_, _) => ShowPage();
        ShowPage();
    }

    /// <summary>Shows the wizard on the very first start (not after updates).</summary>
    public static void ShowIfNeeded(IServiceProvider services, ISettingsService settings)
    {
        if (settings.Current.WizardDone || !settings.IsFirstRun)
        {
            return;
        }

        new WizardWindow(services).ShowDialog();
    }

    private void OnPlaylistChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateMediaCount);

    private void UpdateMediaCount() =>
        MediaCountText.Text = _playlist.Items.Count == 0 ? string.Empty : _loc.Format("Wizard_MediaCount", _playlist.Items.Count);

    private void ShowPage()
    {
        Page1.Visibility = _page == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed;
        Page3.Visibility = _page == 3 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _page > 1 ? Visibility.Visible : Visibility.Hidden;
        NextButton.Content = _loc.Get(_page == 3 ? "Wizard_Finish" : "Wizard_Next");

        var accent = (Brush)FindResource("AccentBrush");
        var muted = (Brush)FindResource("BorderBrush");
        Dot1.Fill = _page >= 1 ? accent : muted;
        Dot2.Fill = _page >= 2 ? accent : muted;
        Dot3.Fill = _page >= 3 ? accent : muted;

        var turkish = _loc.CurrentCode == "tr";
        TurkishButton.Style = turkish ? (Style)FindResource("AccentButton") : (Style)FindResource(typeof(Button));
        EnglishButton.Style = !turkish ? (Style)FindResource("AccentButton") : (Style)FindResource(typeof(Button));

        ScreensText.Text = _displays.Count > 1
            ? _loc.Format("Wizard_ScreensMany", _displays.Count)
            : _loc.Get("Wizard_ScreensOne");
        DisplayList.Visibility = _displays.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SameContentBox.Visibility = DisplayList.Visibility;
        UpdateMediaCount();
    }

    private void SetLanguage(AppLanguage language)
    {
        _general.Language = language; // Saves and applies.
        ShowPage();
    }

    private void Turkish_Click(object sender, RoutedEventArgs e) => SetLanguage(AppLanguage.Turkish);

    private void English_Click(object sender, RoutedEventArgs e) => SetLanguage(AppLanguage.English);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _page = Math.Max(1, _page - 1);
        ShowPage();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page == 2)
        {
            ApplyScreens();
        }

        if (_page == 3)
        {
            Finish();
            return;
        }

        _page++;
        ShowPage();
    }

    private void ApplyScreens()
    {
        var chosen = _displays.Where(d => d.Use).ToList();
        if (_displays.Count <= 1 || chosen.Count == 0)
        {
            return;
        }

        var screens = chosen.Select((d, i) => new ScreenConfig { Number = i + 1, Display = d.Display.ToSaved(), Enabled = true }).ToEquatableList();
        _settings.Update(s => s with { Screens = screens });
        _playlist.EnsureScreens(screens.Select(s => s.Number));
        if (SameContentBox.IsChecked == true)
        {
            // Screen 2..n play screen 1's list, so media added once shows everywhere.
            foreach (var screen in screens.Skip(1))
            {
                if (_playlist.GetScreenPlaylist(screen.Number) is { } playlist)
                {
                    _playlist.UpdateScreenOptions(playlist with { LinkedTo = 1 });
                }
            }
        }
    }

    private async void AddMedia_Click(object sender, RoutedEventArgs e)
    {
        Topmost = false;
        try
        {
            await _media.AddFilesCommand.ExecuteAsync(null);
        }
        finally
        {
            Topmost = true;
        }
    }

    private void CreateSlide_Click(object sender, RoutedEventArgs e)
    {
        Topmost = false;
        try
        {
            _media.CreateSlideCommand.Execute(null);
        }
        finally
        {
            Topmost = true;
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s with { WizardDone = true });
        Close();
    }

    private void Finish()
    {
        _general.StartWithWindows = AutostartBox.IsChecked == true;
        _general.AutoPlayOnLaunch = AutoplayBox.IsChecked == true;
        _settings.Update(s => s with { WizardDone = true });
        Close();
    }
}
