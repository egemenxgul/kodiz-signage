using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KodizSignage.Core.Models;
using KodizSignage.Services;

namespace KodizSignage.ViewModels;

public sealed record LogoChoice(Guid? Id, string Label);

/// <summary>The "On screen" tab of a screen: clock, ticker and logo.</summary>
public sealed partial class OverlayEditorViewModel : ObservableObject
{
    private readonly ILocalizationService _loc;
    private readonly Action<Func<ScreenOverlays, ScreenOverlays>> _update;
    private bool _syncing;

    public OverlayEditorViewModel(ILocalizationService loc, Action<Func<ScreenOverlays, ScreenOverlays>> update)
    {
        _loc = loc;
        _update = update;
        OptionItem<int> O(int value, string key) => new(value, key, loc);
        Corners = new[] { O(0, "Overlay_TopLeft"), O(1, "Overlay_TopRight"), O(2, "Overlay_BottomLeft"), O(3, "Overlay_BottomRight") };
        TickerPositions = new[] { O(0, "Overlay_Bottom"), O(1, "Overlay_Top") };
        loc.LanguageChanged += (_, _) =>
        {
            foreach (var option in Corners.Concat(TickerPositions))
            {
                option.Refresh(loc);
            }
        };
    }

    public IReadOnlyList<OptionItem<int>> Corners { get; }
    public IReadOnlyList<OptionItem<int>> TickerPositions { get; }
    public ObservableCollection<LogoChoice> LogoChoices { get; } = new();

    [ObservableProperty] private bool _clockEnabled;
    [ObservableProperty] private int _clockCorner;
    [ObservableProperty] private bool _clockShowDate;
    [ObservableProperty] private bool _clockShowSeconds;
    [ObservableProperty] private bool _tickerEnabled;
    [ObservableProperty] private string _tickerText = string.Empty;
    [ObservableProperty] private int _tickerPosition;
    [ObservableProperty] private double _tickerSpeed;
    [ObservableProperty] private string _tickerBackground = string.Empty;
    [ObservableProperty] private string _tickerTextColor = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogo))]
    private Guid? _logoId;

    [ObservableProperty] private int _logoCorner;
    [ObservableProperty] private double _logoSize;
    [ObservableProperty] private double _logoOpacityPercent;

    public bool HasLogo => LogoId is { } id && id != Guid.Empty;

    public void Load(ScreenOverlays o, IReadOnlyList<PlaylistItem> library)
    {
        _syncing = true;
        try
        {
            SetLogoChoices(library, o.LogoMediaId);
            ClockEnabled = o.ClockEnabled;
            ClockCorner = (int)o.ClockCorner;
            ClockShowDate = o.ClockShowDate;
            ClockShowSeconds = o.ClockShowSeconds;
            TickerEnabled = o.TickerEnabled;
            if ((TickerText ?? string.Empty).ReplaceLineEndings(" ").Trim() != o.TickerText)
            {
                TickerText = o.TickerText; // Not while typing: a trailing space would be trimmed away.
            }

            TickerPosition = (int)o.TickerPosition;
            TickerSpeed = o.TickerSpeed;
            TickerBackground = o.TickerBackground;
            TickerTextColor = o.TickerTextColor;
            LogoId = o.LogoMediaId ?? Guid.Empty;
            LogoCorner = (int)o.LogoCorner;
            LogoSize = o.LogoSizePercent;
            LogoOpacityPercent = Math.Round(o.LogoOpacity * 100);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Images of the library that can be used as a logo (rebuilt only when it changed).</summary>
    private void SetLogoChoices(IReadOnlyList<PlaylistItem> library, Guid? selected)
    {
        var choices = new List<LogoChoice> { new(Guid.Empty, _loc.Get("Overlay_NoLogo")) };
        choices.AddRange(library.Where(i => i.Type == MediaType.Image).Select(i => new LogoChoice(i.Id, i.Title)));
        if (selected is { } id && choices.All(c => c.Id != id))
        {
            choices.Add(new LogoChoice(id, _loc.Get("Overlay_LogoMissing")));
        }

        if (!choices.SequenceEqual(LogoChoices))
        {
            LogoChoices.Clear();
            foreach (var choice in choices)
            {
                LogoChoices.Add(choice);
            }

            OnPropertyChanged(nameof(LogoId));
        }
    }

    private void Update(Func<ScreenOverlays, ScreenOverlays> change)
    {
        if (!_syncing)
        {
            _update(change);
        }
    }

    partial void OnClockEnabledChanged(bool value) => Update(o => o with { ClockEnabled = value });
    partial void OnClockCornerChanged(int value) => Update(o => o with { ClockCorner = (OverlayCorner)Math.Clamp(value, 0, 3) });
    partial void OnClockShowDateChanged(bool value) => Update(o => o with { ClockShowDate = value });
    partial void OnClockShowSecondsChanged(bool value) => Update(o => o with { ClockShowSeconds = value });
    partial void OnTickerEnabledChanged(bool value) => Update(o => o with { TickerEnabled = value });
    partial void OnTickerTextChanged(string value) => Update(o => o with { TickerText = value ?? string.Empty });
    partial void OnTickerPositionChanged(int value) => Update(o => o with { TickerPosition = value == 1 ? Core.Models.TickerPosition.Top : Core.Models.TickerPosition.Bottom });
    partial void OnTickerSpeedChanged(double value) => Update(o => double.IsFinite(value) ? o with { TickerSpeed = (int)Math.Round(value) } : o);
    partial void OnLogoCornerChanged(int value) => Update(o => o with { LogoCorner = (OverlayCorner)Math.Clamp(value, 0, 3) });
    partial void OnLogoSizeChanged(double value) => Update(o => double.IsFinite(value) ? o with { LogoSizePercent = (int)Math.Round(value) } : o);
    partial void OnLogoOpacityPercentChanged(double value) => Update(o => double.IsFinite(value) ? o with { LogoOpacity = Math.Round(value) / 100 } : o);

    partial void OnLogoIdChanged(Guid? value)
    {
        // A ComboBox briefly reports null while its items are rebuilt; that is not a choice.
        if (value is null)
        {
            return;
        }

        Update(o => o with { LogoMediaId = value == Guid.Empty ? null : value });
    }

    partial void OnTickerBackgroundChanged(string value)
    {
        var color = value?.Trim() ?? string.Empty;
        if (AppSettings.IsValidColor(color))
        {
            Update(o => o with { TickerBackground = color.ToUpperInvariant() });
        }
    }

    partial void OnTickerTextColorChanged(string value)
    {
        var color = value?.Trim() ?? string.Empty;
        if (AppSettings.IsValidColor(color))
        {
            Update(o => o with { TickerTextColor = color.ToUpperInvariant() });
        }
    }
}
