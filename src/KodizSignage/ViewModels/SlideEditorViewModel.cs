using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Core.Models;
using KodizSignage.Services;

namespace KodizSignage.ViewModels;

public sealed partial class PriceRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public PriceRowViewModel(PriceRow row, Action changed)
    {
        _name = row.Name;
        _price = row.Price;
        _note = row.Note ?? string.Empty;
        _changed = changed;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _price;
    [ObservableProperty] private string _note;

    public PriceRow ToRow() => new(Name.Trim(), Price.Trim(), string.IsNullOrWhiteSpace(Note) ? null : Note.Trim());

    partial void OnNameChanged(string value) => _changed();
    partial void OnPriceChanged(string value) => _changed();
    partial void OnNoteChanged(string value) => _changed();
}

/// <summary>Form + live preview for creating / editing a slide.</summary>
public sealed partial class SlideEditorViewModel : ObservableObject
{
    private readonly DispatcherTimer _previewTimer;
    private bool _loading;

    public SlideEditorViewModel(SlideDefinition? existing, ILocalizationService loc)
    {
        IsEditing = existing is not null;
        OptionItem<int> O(int value, string key) => new(value, key, loc);
        Templates = new[] { O(0, "Slide_TemplateAnnouncement"), O(1, "Slide_TemplatePriceList"), O(2, "Slide_TemplateQr") };
        QrKinds = new[] { O(0, "Slide_QrLink"), O(1, "Slide_QrText"), O(2, "Slide_QrWifi") };
        Themes = SlideThemes.All.Select(t => new ThemeOption(t, loc.Get("Theme_" + t.Key))).ToList();

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            Preview = SlideRenderer.Render(ToDefinition(), Portrait ? 0.3 : 0.4);
        };

        Load(existing ?? DefaultSlide(loc));
    }

    public bool IsEditing { get; }
    public IReadOnlyList<OptionItem<int>> Templates { get; }
    public IReadOnlyList<OptionItem<int>> QrKinds { get; }
    public IReadOnlyList<ThemeOption> Themes { get; }
    public ObservableCollection<PriceRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPriceList), nameof(IsQr))]
    private int _template;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _body = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWifi))]
    private int _qrKind;

    [ObservableProperty] private string _qrText = string.Empty;
    [ObservableProperty] private string _wifiSsid = string.Empty;
    [ObservableProperty] private string _wifiPassword = string.Empty;
    [ObservableProperty] private string _backgroundColor = "#1E1B4B";
    [ObservableProperty] private bool _useGradient = true;
    [ObservableProperty] private string _backgroundColor2 = "#4338CA";
    [ObservableProperty] private string _textColor = "#FFFFFF";
    [ObservableProperty] private string _accentColor = "#FBBF24";
    [ObservableProperty] private bool _portrait;
    [ObservableProperty] private double _textScale = 1.0;
    [ObservableProperty] private ImageSource? _preview;

    public bool IsPriceList => Template == (int)SlideTemplate.PriceList;
    public bool IsQr => Template == (int)SlideTemplate.QrCode;
    public bool IsWifi => QrKind == (int)QrContentKind.Wifi;

    private static SlideDefinition DefaultSlide(ILocalizationService loc) => new()
    {
        Title = loc.Get("Slide_DefaultTitle"),
        Body = loc.Get("Slide_DefaultBody"),
        Rows = new[] { new PriceRow(loc.Get("Slide_DefaultRow1"), "₺60"), new PriceRow(loc.Get("Slide_DefaultRow2"), "₺85") }.ToEquatableList(),
    };

    private void Load(SlideDefinition slide)
    {
        _loading = true;
        try
        {
            Template = (int)slide.Template;
            Title = slide.Title;
            Subtitle = slide.Subtitle;
            Body = slide.Body;
            QrKind = (int)slide.QrKind;
            QrText = slide.QrText;
            WifiSsid = slide.WifiSsid;
            WifiPassword = slide.WifiPassword;
            BackgroundColor = slide.BackgroundColor;
            UseGradient = slide.BackgroundColor2 is not null;
            BackgroundColor2 = slide.BackgroundColor2 ?? slide.BackgroundColor;
            TextColor = slide.TextColor;
            AccentColor = slide.AccentColor;
            Portrait = slide.Portrait;
            TextScale = slide.TextScale;
            Rows.Clear();
            foreach (var row in slide.Rows)
            {
                Rows.Add(new PriceRowViewModel(row, SchedulePreview));
            }
        }
        finally
        {
            _loading = false;
        }

        SchedulePreview();
    }

    public SlideDefinition ToDefinition() => new SlideDefinition
    {
        Template = (SlideTemplate)Template,
        Title = Title,
        Subtitle = Subtitle,
        Body = Body,
        Rows = Rows.Select(r => r.ToRow()).ToEquatableList(),
        QrKind = (QrContentKind)QrKind,
        QrText = QrText,
        WifiSsid = WifiSsid,
        WifiPassword = WifiPassword,
        BackgroundColor = BackgroundColor,
        BackgroundColor2 = UseGradient ? BackgroundColor2 : null,
        TextColor = TextColor,
        AccentColor = AccentColor,
        Portrait = Portrait,
        TextScale = TextScale,
    }.Normalize();

    private void SchedulePreview()
    {
        if (_loading)
        {
            return;
        }

        _previewTimer.Stop();
        _previewTimer.Start();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(Preview) or nameof(IsPriceList) or nameof(IsQr) or nameof(IsWifi)))
        {
            SchedulePreview();
        }
    }

    [RelayCommand]
    private void AddRow()
    {
        Rows.Add(new PriceRowViewModel(new PriceRow(string.Empty, string.Empty), SchedulePreview));
        SchedulePreview();
    }

    [RelayCommand]
    private void RemoveRow(PriceRowViewModel? row)
    {
        if (row is not null)
        {
            Rows.Remove(row);
            SchedulePreview();
        }
    }

    [RelayCommand]
    private void ApplyTheme(ThemeOption? option)
    {
        if (option is null)
        {
            return;
        }

        var t = option.Theme;
        BackgroundColor = t.Background;
        UseGradient = t.Background2 is not null;
        BackgroundColor2 = t.Background2 ?? t.Background;
        TextColor = t.Text;
        AccentColor = t.Accent;
    }
}

public sealed record ThemeOption(SlideThemes.Theme Theme, string Label)
{
    public Brush Swatch { get; } = CreateSwatch(Theme);

    private static Brush CreateSwatch(SlideThemes.Theme theme)
    {
        var a = (Color)ColorConverter.ConvertFromString(theme.Background);
        var b = theme.Background2 is { } second ? (Color)ColorConverter.ConvertFromString(second) : a;
        var brush = new LinearGradientBrush(a, b, 45);
        brush.Freeze();
        return brush;
    }
}
