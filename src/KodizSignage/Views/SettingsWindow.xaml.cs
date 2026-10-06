using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KodizSignage.Core.Hotkeys;
using KodizSignage.Services;
using KodizSignage.ViewModels;

namespace KodizSignage.Views;

public partial class SettingsWindow : Window
{
    private const double CompactBelowWidth = 1100;

    private readonly SettingsViewModel _vm;
    private readonly IPlaybackManager _playback;
    private readonly DispatcherTimer _tick;
    private Point? _dragStart;
    private MediaItemViewModel? _dragItem;

    public SettingsWindow(SettingsViewModel viewModel, IPlaybackManager playback)
    {
        InitializeComponent();
        _vm = viewModel;
        _playback = playback;
        DataContext = viewModel;
        FitToWorkArea();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            _vm.Media.Tick();
            _vm.Display.Editor.Tick();
            UpdateTopmost();
        };

        _vm.Media.PropertyChanged += OnMediaPropertyChanged;
        _playback.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTopmost);
        IsVisibleChanged += OnIsVisibleChanged;
        Activated += (_, _) =>
        {
            _vm.Media.RefreshTexts();
            _vm.Display.Editor.RefreshTexts();
            _vm.General.RefreshSystemInfo();
        };
        Deactivated += (_, _) => CommitFocusedTextBox();
        LocationChanged += (_, _) => UpdateTopmost();
        SizeChanged += (_, _) => _vm.IsCompact = ActualWidth < CompactBelowWidth;
        Drop += Window_Drop;
        DragOver += Window_DragOver;
    }

    /// <summary>Set on application exit; otherwise closing only hides the window.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Raised when the user closes (hides) the window.</summary>
    public event EventHandler? HiddenByUser;

    /// <summary>Never larger than the screen (e.g. a 1080p TV at 150 % scaling has only 1280×720 DIPs).</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width * 0.95));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height * 0.95));
        if (area.Width < MinWidth || area.Height < MinHeight)
        {
            MinWidth = Math.Min(MinWidth, area.Width);
            MinHeight = Math.Min(MinHeight, area.Height);
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Stay above the full-screen player only when it covers the same monitor (single-screen café
    /// setups); otherwise behave like a normal window so Explorer can be used next to it.
    /// </summary>
    private void UpdateTopmost()
    {
        if (!IsVisible)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var shouldBeTopmost = _playback.CoversMonitorOf(hwnd);
        if (Topmost != shouldBeTopmost)
        {
            Topmost = shouldBeTopmost;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdatePreviewVideo();
        if (IsVisible)
        {
            _tick.Start();
            _vm.Media.Tick();
            UpdateTopmost();
        }
        else
        {
            _tick.Stop();
        }
    }

    /// <summary>Text boxes save with a short delay; make sure nothing typed is lost when focus leaves the window.</summary>
    private static void CommitFocusedTextBox()
    {
        if (Keyboard.FocusedElement is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }

    public void ShowAndActivate()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        UpdateTopmost();
        if (!Topmost && _playback.CoversMonitorOf(new WindowInteropHelper(this).Handle) is false)
        {
            // Bring to front once even when not topmost.
            Topmost = true;
            Topmost = false;
        }

        Activate();
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitFocusedTextBox();
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenByUser?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    // ---- Video preview ----------------------------------------------------------------------

    private void OnMediaPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaViewModel.PreviewVideoPath))
        {
            UpdatePreviewVideo();
        }
    }

    private void UpdatePreviewVideo()
    {
        var path = IsVisible ? _vm.Media.PreviewVideoPath : null;
        if (path is null)
        {
            PreviewVideo.Stop();
            PreviewVideo.Close();
            PreviewVideo.Source = null;
            return;
        }

        var uri = new Uri(path, UriKind.Absolute);
        if (PreviewVideo.Source != uri)
        {
            PreviewVideo.Source = uri;
        }

        PreviewVideo.Play();
    }

    private void PreviewVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        PreviewVideo.Position = TimeSpan.Zero;
        PreviewVideo.Play();
    }

    // ---- Drag & drop: files from Explorer ---------------------------------------------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Handled = true;
            await _vm.Media.ImportAsync(files);
        }
    }

    // ---- Drag & drop: reordering via the handle ---------------------------------------------

    private void MediaList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _dragItem = null;
        if (e.OriginalSource is FrameworkElement { Tag: "DragHandle", DataContext: MediaItemViewModel item })
        {
            _dragStart = e.GetPosition(MediaList);
            _dragItem = item;
        }
    }

    private void MediaList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(MediaList) - start;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        var item = _dragItem;
        _dragStart = null;
        _dragItem = null;
        DragDrop.DoDragDrop(MediaList, new DataObject(typeof(MediaItemViewModel), item), DragDropEffects.Move);
    }

    private void MediaList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(MediaItemViewModel)))
        {
            e.Effects = DragDropEffects.Move;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void MediaList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(MediaItemViewModel)) is MediaItemViewModel dragged)
        {
            e.Handled = true;
            // Use the target row's position in the whole playlist (the list may be filtered to a screen).
            var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            var index = target?.DataContext is MediaItemViewModel targetItem
                ? targetItem.Position
                : _vm.Media.Items.Count - 1;
            if (index >= 0 && dragged.Position != index)
            {
                _vm.Media.Move(dragged, index);
                MediaList.SelectedItem = dragged;
            }
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Handled = true;
            await _vm.Media.ImportAsync(files);
        }
    }

    private void MediaList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && e.OriginalSource is ListBoxItem && _vm.Media.SelectedItems.Count > 0)
        {
            _vm.Media.DeleteItems(_vm.Media.SelectedItems.ToList());
            e.Handled = true;
        }
    }

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.Media.SelectedItems = MediaList.SelectedItems.Cast<MediaItemViewModel>().ToList();
    }

    // ---- Shortcut recording -------------------------------------------------------------------

    private void ShortcutBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShortcutItemViewModel item })
        {
            _vm.Shortcuts.BeginRecording(item);
        }
    }

    private void ShortcutBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShortcutItemViewModel item })
        {
            _vm.Shortcuts.EndRecording(item);
        }
    }

    private void ShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShortcutItemViewModel item })
        {
            return;
        }

        var key = HotkeyDisplay.RealKey(e);
        var modifiers = HotkeyDisplay.CurrentModifiers();

        if (key == Key.Tab && modifiers == HotkeyModifiers.None)
        {
            return; // Keep keyboard navigation working.
        }

        e.Handled = true;

        if (HotkeyDisplay.IsModifierKey(key))
        {
            _vm.Shortcuts.ShowPending(item, modifiers);
            return;
        }

        if (modifiers == HotkeyModifiers.None && key == Key.Escape)
        {
            Keyboard.ClearFocus(); // Cancel: ends recording, keeps the old shortcut.
            return;
        }

        if (modifiers == HotkeyModifiers.None && key is Key.Back or Key.Delete)
        {
            _vm.Shortcuts.Assign(item, string.Empty);
            Keyboard.ClearFocus();
            return;
        }

        var gesture = new HotkeyGesture(modifiers, key.ToString()).ToString();
        if (_vm.Shortcuts.Assign(item, gesture))
        {
            Keyboard.ClearFocus();
        }
        else
        {
            _vm.Shortcuts.ShowPending(item, HotkeyModifiers.None); // Stay in recording mode; error is shown.
        }
    }

    private void ShortcutBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShortcutItemViewModel { IsRecording: true } item } &&
            HotkeyDisplay.IsModifierKey(HotkeyDisplay.RealKey(e)))
        {
            _vm.Shortcuts.ShowPending(item, HotkeyDisplay.CurrentModifiers());
            e.Handled = true;
        }
    }

    // ---- Screen playlist (entries) -------------------------------------------------------------

    private Point? _entryDragStart;
    private EntryViewModel? _entryDragItem;

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.Display.Editor.SelectedEntries = EntryList.SelectedItems.Cast<EntryViewModel>().ToList();

    private void EntryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _entryDragStart = null;
        _entryDragItem = null;
        if (e.OriginalSource is FrameworkElement { Tag: "DragHandle", DataContext: EntryViewModel entry })
        {
            _entryDragStart = e.GetPosition(EntryList);
            _entryDragItem = entry;
        }
    }

    private void EntryList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_entryDragStart is not { } start || _entryDragItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(EntryList) - start;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        var entry = _entryDragItem;
        _entryDragStart = null;
        _entryDragItem = null;
        DragDrop.DoDragDrop(EntryList, new DataObject(typeof(EntryViewModel), entry), DragDropEffects.Move);
    }

    private void EntryList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(EntryViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void EntryList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(EntryViewModel)) is not EntryViewModel dragged)
        {
            return;
        }

        e.Handled = true;
        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var index = target?.DataContext is EntryViewModel targetEntry ? targetEntry.Position : _vm.Display.Editor.Entries.Count - 1;
        if (index >= 0 && dragged.Position != index)
        {
            _vm.Display.Editor.Move(dragged, index);
        }
    }

    private void EntryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && e.OriginalSource is ListBoxItem && _vm.Display.Editor.SelectedEntries.Count > 0)
        {
            _vm.Display.Editor.RemoveEntries(_vm.Display.Editor.SelectedEntries.ToList());
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }
}
