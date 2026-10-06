using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Core.Displays;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Services;
using KodizSignage.Views;
using Microsoft.Win32;

namespace KodizSignage.ViewModels;

public sealed partial class DisplayItemViewModel : ObservableObject
{
    public DisplayItemViewModel(DisplayInfo info, int number, string title)
    {
        Info = info;
        Number = number;
        Title = title;
    }

    public DisplayInfo Info { get; }
    public int Number { get; }
    public string Title { get; }
    public string Resolution => $"{Info.Width} × {Info.Height}";
    public string Details => $"{Info.DeviceName.TrimStart('\\', '.')} · {Info.X}, {Info.Y}";
    public bool IsPrimary => Info.IsPrimary;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class DisplayViewModel : ObservableObject
{
    private readonly IDisplayService _displays;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _loc;

    public DisplayViewModel(IDisplayService displays, ISettingsService settings, ILocalizationService loc)
    {
        _displays = displays;
        _settings = settings;
        _loc = loc;

        _settings.Changed += (_, e) =>
        {
            if (e.OldSettings.SelectedDisplay != e.NewSettings.SelectedDisplay)
            {
                Application.Current.Dispatcher.BeginInvoke(UpdateSelection);
            }
        };
        FallbackOptions = new[]
        {
            new OptionItem<DisplayFallback>(DisplayFallback.Hide, "Fallback_Hide", loc),
            new OptionItem<DisplayFallback>(DisplayFallback.ShowOnPrimary, "Fallback_Primary", loc),
        };
        _loc.LanguageChanged += (_, _) =>
        {
            foreach (var o in FallbackOptions) o.Refresh(_loc);
            Refresh();
        };
        _fallback = settings.Current.DisplayFallback;
        SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public ObservableCollection<DisplayItemViewModel> Displays { get; } = new();

    public IReadOnlyList<OptionItem<DisplayFallback>> FallbackOptions { get; }

    [ObservableProperty]
    private DisplayFallback _fallback;

    partial void OnFallbackChanged(DisplayFallback value) => _settings.Update(s => s with { DisplayFallback = value });

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _hasWarning;

    [RelayCommand]
    private void Refresh()
    {
        Displays.Clear();
        var displays = _displays.GetDisplays();
        for (var i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            var title = string.IsNullOrWhiteSpace(d.FriendlyName)
                ? _loc.Format("Display_Generic", i + 1)
                : $"{i + 1}. {d.FriendlyName}";
            Displays.Add(new DisplayItemViewModel(d, i + 1, title));
        }

        UpdateSelection();
    }

    [RelayCommand]
    private void Identify()
    {
        Refresh();
        IdentifyWindow.ShowAll(Displays.Select(d => d.Info).ToList(), TimeSpan.FromSeconds(3));
    }

    [RelayCommand]
    private void Select(DisplayItemViewModel? item)
    {
        if (item is not null)
        {
            _settings.Update(s => s with { SelectedDisplay = item.Info.ToSaved() });
        }
    }

    private void UpdateSelection()
    {
        var saved = _settings.Current.SelectedDisplay;
        var match = DisplayMatcher.Find(saved, Displays.Select(d => d.Info).ToList());
        foreach (var d in Displays)
        {
            d.IsSelected = match.Display == d.Info;
        }

        if (Displays.Count == 0)
        {
            StatusText = _loc.Get("Display_None");
            HasWarning = true;
        }
        else if (saved is null)
        {
            StatusText = _loc.Get("Display_NotConfigured");
            HasWarning = false;
        }
        else if (!match.IsSatisfied)
        {
            StatusText = _loc.Format("Display_SavedMissing", saved.DeviceName.TrimStart('\\', '.'), saved.Width, saved.Height);
            HasWarning = true;
        }
        else
        {
            StatusText = string.Empty;
            HasWarning = false;
        }
    }
}
