using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Services;

namespace KodizSignage.Views;

public sealed partial class PickerItem : ObservableObject
{
    public PickerItem(PlaylistItem media, string typeText, bool alreadyThere)
    {
        Media = media;
        TypeText = typeText;
        AlreadyThereVisibility = alreadyThere ? Visibility.Visible : Visibility.Collapsed;
    }

    public PlaylistItem Media { get; }
    public string Title => Media.Title;
    public string TypeText { get; }
    public Visibility AlreadyThereVisibility { get; }

    [ObservableProperty] private ImageSource? _thumbnail;
}

/// <summary>Picks library media to add to a screen's playlist.</summary>
public partial class LibraryPickerWindow : Window
{
    private readonly ILocalizationService _loc;
    private readonly List<PickerItem> _all;
    private readonly HashSet<PickerItem> _selected = new();
    private bool _refreshing;

    private LibraryPickerWindow(IReadOnlyList<PickerItem> items, ILocalizationService loc, string screenTitle)
    {
        InitializeComponent();
        WindowSizing.FitToWorkArea(this);
        _loc = loc;
        _all = items.ToList();
        Title = loc.Format("Picker_Title", screenTitle);
        HeaderText.Text = Title;
        List.ItemsSource = _all;
        UpdateCount();
        Loaded += (_, _) => SearchBox.Focus();
    }

    public IReadOnlyList<Guid> Picked { get; private set; } = Array.Empty<Guid>();

    public static IReadOnlyList<Guid> Pick(IPlaylistService playlist, IThumbnailService thumbnails, ILocalizationService loc,
        string screenTitle, IReadOnlySet<Guid> alreadyOnScreen)
    {
        var items = playlist.Items
            .Select(m => new PickerItem(m, loc.Get(m.Type == MediaType.Video ? "Type_Video" : "Type_Image"), alreadyOnScreen.Contains(m.Id)))
            .ToList();
        if (items.Count == 0)
        {
            MessageWindow.Show(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive), loc.Get("App_Name"), loc.Get("Picker_LibraryEmpty"), MessageKind.Info, loc.Get("Common_Ok"));
            return Array.Empty<Guid>();
        }

        var window = new LibraryPickerWindow(items, loc, screenTitle)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };

        foreach (var item in items)
        {
            _ = LoadThumbnailAsync(item, thumbnails);
        }

        return window.ShowDialog() == true ? window.Picked : Array.Empty<Guid>();
    }

    private static async Task LoadThumbnailAsync(PickerItem item, IThumbnailService thumbnails) =>
        item.Thumbnail = await thumbnails.GetAsync(item.Media);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Selections are kept even for items hidden by the search.
        _refreshing = true;
        List.ItemsSource = query.Length == 0
            ? _all
            : _all.Where(i => i.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                              i.Media.OriginalName.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        foreach (var item in List.Items.Cast<PickerItem>().Where(_selected.Contains))
        {
            List.SelectedItems.Add(item);
        }

        _refreshing = false;
        UpdateCount();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => List.SelectAll();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }

        foreach (PickerItem item in e.AddedItems)
        {
            _selected.Add(item);
        }

        foreach (PickerItem item in e.RemovedItems)
        {
            _selected.Remove(item);
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        var count = _selected.Count;
        CountText.Text = _loc.Format("Picker_Count", count);
        AddButton.Content = _loc.Format("Picker_Add", count);
        AddButton.IsEnabled = count > 0;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // Keep library order regardless of click order.
        Picked = _all.Where(_selected.Contains).Select(i => i.Media.Id).ToList();
        DialogResult = true;
    }
}
