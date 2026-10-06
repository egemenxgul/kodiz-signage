using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KodizSignage.ViewModels;

/// <summary>
/// The "… deleted – Undo" snackbar. One pending action at a time: showing a new one (or the
/// timeout, or closing the window) makes the previous one final.
/// </summary>
public sealed partial class UndoBar : ObservableObject
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer _timer;
    private Action? _undo;
    private Action? _commit;

    public UndoBar()
    {
        _timer = new DispatcherTimer { Interval = Window };
        _timer.Tick += (_, _) => Commit();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private string? _text;

    public bool IsVisible => Text is not null;

    public void Show(string text, Action undo, Action? commit = null)
    {
        Commit();
        _undo = undo;
        _commit = commit;
        Text = text;
        _timer.Start();
    }

    [RelayCommand]
    private void Undo()
    {
        _timer.Stop();
        var undo = _undo;
        _undo = null;
        _commit = null;
        Text = null;
        undo?.Invoke();
    }

    /// <summary>Makes the pending action final (e.g. deletes files from disk).</summary>
    [RelayCommand]
    public void Commit()
    {
        _timer.Stop();
        var commit = _commit;
        _undo = null;
        _commit = null;
        Text = null;
        commit?.Invoke();
    }
}
