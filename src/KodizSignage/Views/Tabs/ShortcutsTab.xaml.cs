using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KodizSignage.Core.Hotkeys;
using KodizSignage.Services;
using KodizSignage.ViewModels;

namespace KodizSignage.Views.Tabs;

/// <summary>Shortcut editor (DataContext: ShortcutsViewModel); records key combinations.</summary>
public partial class ShortcutsTab : UserControl
{
    public ShortcutsTab()
    {
        InitializeComponent();
    }

    private ShortcutsViewModel Shortcuts => (ShortcutsViewModel)DataContext;

    private void ShortcutBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShortcutItemViewModel item })
        {
            Shortcuts.BeginRecording(item);
        }
    }

    private void ShortcutBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShortcutItemViewModel item })
        {
            Shortcuts.EndRecording(item);
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
            Shortcuts.ShowPending(item, modifiers);
            return;
        }

        if (modifiers == HotkeyModifiers.None && key == Key.Escape)
        {
            Keyboard.ClearFocus(); // Cancel: ends recording, keeps the old shortcut.
            return;
        }

        if (modifiers == HotkeyModifiers.None && key is Key.Back or Key.Delete)
        {
            Shortcuts.Assign(item, string.Empty);
            Keyboard.ClearFocus();
            return;
        }

        var gesture = new HotkeyGesture(modifiers, key.ToString()).ToString();
        if (Shortcuts.Assign(item, gesture))
        {
            Keyboard.ClearFocus();
        }
        else
        {
            Shortcuts.ShowPending(item, HotkeyModifiers.None); // Stay in recording mode; error is shown.
        }
    }

    private void ShortcutBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShortcutItemViewModel { IsRecording: true } item } &&
            HotkeyDisplay.IsModifierKey(HotkeyDisplay.RealKey(e)))
        {
            Shortcuts.ShowPending(item, HotkeyDisplay.CurrentModifiers());
            e.Handled = true;
        }
    }
}
