using System.IO;
using System.Windows;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Services;
using KodizSignage.ViewModels;

namespace KodizSignage.Views;

/// <summary>"This media is already in the library – what now?" with both files side by side.</summary>
public partial class DuplicateWindow : Window
{
    private DuplicateAnswer _answer = DuplicateAnswer.Skip;

    private DuplicateWindow()
    {
        InitializeComponent();
        Services.ThemeService.Attach(this);
    }

    public static async Task<(DuplicateAnswer Answer, bool ApplyToAll)> Ask(
        DuplicateQuestion question, IReadOnlyList<int> usedOnScreens, IThumbnailService thumbnails, ILocalizationService loc)
    {
        var window = new DuplicateWindow();
        var kind = question.Kind;
        window.Title = loc.Get("App_Name");
        window.HeaderText.Text = loc.Get($"Duplicate_Title_{kind}");
        window.ExplanationText.Text = loc.Format($"Duplicate_Explain_{kind}", MediaViewModel.FormatBytes(question.SourceSize));

        window.NewName.Text = question.FileName;
        window.NewDetails.Text = $"{MediaViewModel.FormatBytes(question.SourceSize)} · {Path.GetDirectoryName(question.SourcePath)}";
        window.ExistingName.Text = question.Existing.Title;
        window.ExistingDetails.Text = string.Join(" · ", new[]
        {
            question.Existing.FileSize is { } size ? MediaViewModel.FormatBytes(size) : null,
            question.Existing.OriginalName != question.Existing.Title ? question.Existing.OriginalName : null,
            usedOnScreens.Count > 0 ? loc.Format("Library_UsedOn", string.Join(", ", usedOnScreens)) : loc.Get("Library_NotUsed"),
        }.Where(t => t is not null));

        window.CopyButton.Content = loc.Format("Duplicate_AddCopy", MediaViewModel.FormatBytes(question.SourceSize));
        // "Use existing" makes no sense for a different file that only shares the name; "replace" makes
        // no sense for identical content.
        window.UseButton.Visibility = kind == DuplicateKind.SameName ? Visibility.Collapsed : Visibility.Visible;
        window.ReplaceButton.Visibility = kind == DuplicateKind.Exact ? Visibility.Collapsed : Visibility.Visible;
        if (kind == DuplicateKind.SameName)
        {
            window.ReplaceButton.Style = (Style)Application.Current.FindResource("AccentButton");
            window.ReplaceButton.IsDefault = true;
        }

        if (question.Existing.Type == MediaType.Image)
        {
            window.ExistingImage.Source = await thumbnails.GetAsync(question.Existing);
            try
            {
                window.NewImage.Source = await Task.Run(() => ImageLoader.Load(question.SourcePath, 480));
            }
            catch (Exception)
            {
                // Preview is optional.
            }
        }

        window.ShowDialog();
        return (window._answer, window.ApplyToAll.IsChecked == true);
    }

    private void Close(DuplicateAnswer answer)
    {
        _answer = answer;
        DialogResult = true;
    }

    private void Use_Click(object sender, RoutedEventArgs e) => Close(DuplicateAnswer.UseExisting);

    private void Copy_Click(object sender, RoutedEventArgs e) => Close(DuplicateAnswer.AddCopy);

    private void Replace_Click(object sender, RoutedEventArgs e) => Close(DuplicateAnswer.ReplaceExisting);

    private void Skip_Click(object sender, RoutedEventArgs e) => Close(DuplicateAnswer.Skip);
}
