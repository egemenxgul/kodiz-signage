using System.Windows;
using Microsoft.Win32;
using KodizSignage.Core.Media;

namespace KodizSignage.Services;

public interface IDialogService
{
    bool Confirm(string message, string? title = null);

    void Info(string message, string? title = null);

    void Warning(string message, string? title = null);

    IReadOnlyList<string> PickMediaFiles();

    string? PickFolder(string title);

    string? PickSaveBackup(string defaultName);

    string? PickBackupToRestore();

    string? PickSaveCsv(string defaultName);
}

/// <summary>
/// Message boxes that always appear above the topmost full-screen player window.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly ILocalizationService _loc;

    public DialogService(ILocalizationService loc)
    {
        _loc = loc;
    }

    public bool Confirm(string message, string? title = null) =>
        WithOwner(owner => MessageBox.Show(owner, message, title ?? _loc.Get("App_Name"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) == MessageBoxResult.Yes;

    public void Info(string message, string? title = null) =>
        WithOwner(owner => MessageBox.Show(owner, message, title ?? _loc.Get("App_Name"), MessageBoxButton.OK, MessageBoxImage.Information));

    public void Warning(string message, string? title = null) =>
        WithOwner(owner => MessageBox.Show(owner, message, title ?? _loc.Get("App_Name"), MessageBoxButton.OK, MessageBoxImage.Warning));

    public IReadOnlyList<string> PickMediaFiles()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = _loc.Get("Media_AddFiles"),
            Filter = $"{_loc.Get("Dialog_MediaFilter")}|{MediaFormats.AllPatterns}|{_loc.Get("Dialog_AllFiles")}|*.*",
        };

        return WithOwner(owner => dialog.ShowDialog(owner) == true ? dialog.FileNames : Array.Empty<string>());
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return WithOwner(owner => dialog.ShowDialog(owner) == true ? dialog.FolderName : null);
    }

    public string? PickSaveBackup(string defaultName)
    {
        var dialog = new SaveFileDialog
        {
            FileName = defaultName,
            DefaultExt = ".zip",
            Filter = $"{_loc.Get("Backup_FileType")}|*.zip",
            Title = _loc.Get("Backup_Create"),
        };
        return WithOwner(owner => dialog.ShowDialog(owner) == true ? dialog.FileName : null);
    }

    public string? PickSaveCsv(string defaultName)
    {
        var dialog = new SaveFileDialog
        {
            FileName = defaultName,
            DefaultExt = ".csv",
            Filter = $"{_loc.Get("Stats_FileType")}|*.csv",
            Title = _loc.Get("Stats_Export"),
        };
        return WithOwner(owner => dialog.ShowDialog(owner) == true ? dialog.FileName : null);
    }

    public string? PickBackupToRestore()
    {
        var dialog = new OpenFileDialog
        {
            Filter = $"{_loc.Get("Backup_FileType")}|*.zip",
            Title = _loc.Get("Backup_Restore"),
        };
        return WithOwner(owner => dialog.ShowDialog(owner) == true ? dialog.FileName : null);
    }

    private static T WithOwner<T>(Func<Window, T> show)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w.ShowInTaskbar);
        if (owner is not null)
        {
            return show(owner);
        }

        // No suitable owner (e.g. triggered by a hotkey while only the player is visible):
        // use an invisible topmost window so the dialog is not hidden behind the player.
        var helper = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        helper.Show();
        helper.Activate();
        try
        {
            return show(helper);
        }
        finally
        {
            helper.Close();
        }
    }
}
