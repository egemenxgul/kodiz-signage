using System.Windows;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Services;
using KodizSignage.ViewModels;

namespace KodizSignage.Views;

/// <summary>Creates or edits a slide (announcement, price list, QR code).</summary>
public partial class SlideEditorWindow : Window
{
    private readonly SlideEditorViewModel _vm;

    private SlideEditorWindow(SlideDefinition? existing, ILocalizationService loc, IPlaylistService playlist, ISlideService slides)
    {
        InitializeComponent();
        Services.ThemeService.Attach(this);
        WindowSizing.FitToWorkArea(this);
        _vm = new SlideEditorViewModel(existing, loc, playlist.Items.Where(i => i.Type == MediaType.Image && i.Slide is null).ToList(), slides.ImagePath);
        DataContext = _vm;
        SaveText.Text = loc.Get(existing is null ? "Slide_Create" : "Slide_Update");
    }

    public SlideDefinition? Result { get; private set; }

    /// <summary>Opens the editor; returns the slide to save, or null when cancelled.</summary>
    public static SlideDefinition? Edit(SlideDefinition? existing, ILocalizationService loc, IPlaylistService playlist, ISlideService slides)
    {
        var window = new SlideEditorWindow(existing, loc, playlist, slides)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };
        return window.ShowDialog() == true ? window.Result : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = _vm.ToDefinition();
        DialogResult = true;
    }
}
