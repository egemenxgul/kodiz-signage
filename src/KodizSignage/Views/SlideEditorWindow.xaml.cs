using System.Windows;
using KodizSignage.Core.Models;
using KodizSignage.Services;
using KodizSignage.ViewModels;

namespace KodizSignage.Views;

/// <summary>Creates or edits a slide (announcement, price list, QR code).</summary>
public partial class SlideEditorWindow : Window
{
    private readonly SlideEditorViewModel _vm;

    private SlideEditorWindow(SlideDefinition? existing, ILocalizationService loc)
    {
        InitializeComponent();
        _vm = new SlideEditorViewModel(existing, loc);
        DataContext = _vm;
        SaveText.Text = loc.Get(existing is null ? "Slide_Create" : "Slide_Update");
    }

    public SlideDefinition? Result { get; private set; }

    /// <summary>Opens the editor; returns the slide to save, or null when cancelled.</summary>
    public static SlideDefinition? Edit(SlideDefinition? existing, ILocalizationService loc)
    {
        var window = new SlideEditorWindow(existing, loc)
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
