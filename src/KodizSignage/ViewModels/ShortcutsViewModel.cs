using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Core.Hotkeys;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Services;

namespace KodizSignage.ViewModels;

/// <summary>One editable shortcut row.</summary>
public sealed partial class ShortcutItemViewModel : ObservableObject
{
    private readonly ShortcutsViewModel _owner;

    public ShortcutItemViewModel(HotkeyAction action, string icon, ShortcutsViewModel owner)
    {
        Action = action;
        Icon = icon;
        _owner = owner;
    }

    public HotkeyAction Action { get; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Icon { get; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _defaultText = string.Empty;

    /// <summary>Canonical gesture text from the settings ("" = unassigned).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoxText), nameof(IsAssigned), nameof(IsDefault))]
    private string _gesture = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoxText))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoxText))]
    private string? _pendingText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty] private ShortcutStatus _status;
    [ObservableProperty] private string _statusText = string.Empty;

    public bool IsAssigned => Gesture.Length > 0;
    public bool IsDefault => Gesture == HotkeySettings.Defaults.Get(Action);
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>What the recording box shows.</summary>
    public string BoxText => IsRecording
        ? PendingText ?? _owner.Text("Shortcut_Press")
        : IsAssigned ? HotkeyDisplay.Format(Gesture) : _owner.Text("Shortcut_None");

    [RelayCommand]
    private void Reset() => _owner.Assign(this, HotkeySettings.Defaults.Get(Action));

    [RelayCommand]
    private void Clear() => _owner.Assign(this, string.Empty);

    public void RefreshTexts() => OnPropertyChanged(nameof(BoxText));
}

public sealed partial class ShortcutsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IShortcutService _shortcuts;
    private readonly ILocalizationService _loc;

    public ShortcutsViewModel(ISettingsService settings, IShortcutService shortcuts, ILocalizationService loc)
    {
        _settings = settings;
        _shortcuts = shortcuts;
        _loc = loc;

        Items = new[]
        {
            new ShortcutItemViewModel(HotkeyAction.ShowSettings, "", this),
            new ShortcutItemViewModel(HotkeyAction.TogglePlayback, "", this),
            new ShortcutItemViewModel(HotkeyAction.NextItem, "", this),
            new ShortcutItemViewModel(HotkeyAction.Exit, "", this),
        };

        _settings.Changed += (_, e) =>
        {
            if (e.OldSettings.Hotkeys != e.NewSettings.Hotkeys)
            {
                Application.Current.Dispatcher.BeginInvoke(SyncGestures);
            }
        };
        _shortcuts.StatusChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(SyncStatus);
        _loc.LanguageChanged += (_, _) => SyncTexts();

        SyncTexts();
        SyncGestures();
        SyncStatus();
    }

    public IReadOnlyList<ShortcutItemViewModel> Items { get; }

    internal string Text(string key) => _loc.Get(key);

    // ---- Recording (driven by the view's key events) ---------------------------------------------

    public void BeginRecording(ShortcutItemViewModel item)
    {
        if (item.IsRecording)
        {
            return;
        }

        _shortcuts.Suspend(); // Otherwise Windows delivers e.g. Ctrl+Shift+S as a hotkey, not a key press.
        item.ErrorText = null;
        item.PendingText = null;
        item.IsRecording = true;
    }

    public void EndRecording(ShortcutItemViewModel item)
    {
        if (!item.IsRecording)
        {
            return;
        }

        item.IsRecording = false;
        item.PendingText = null;
        _shortcuts.Resume();
    }

    public void ShowPending(ShortcutItemViewModel item, HotkeyModifiers modifiers) =>
        item.PendingText = modifiers == HotkeyModifiers.None ? null : HotkeyDisplay.FormatPending(modifiers);

    /// <summary>Validates and saves a new gesture. Returns false (and sets an error) if it was rejected.</summary>
    public bool Assign(ShortcutItemViewModel item, string gestureText)
    {
        item.ErrorText = null;
        if (!HotkeyGesture.TryParse(gestureText, out var gesture))
        {
            item.ErrorText = _loc.Get("Shortcut_Error_Invalid");
            return false;
        }

        var display = HotkeyDisplay.Format(gesture.ToString());
        switch (HotkeyRules.Validate(gesture))
        {
            case HotkeyValidation.MissingModifier:
                item.ErrorText = _loc.Get("Shortcut_Error_MissingModifier");
                return false;
            case HotkeyValidation.Reserved:
                item.ErrorText = _loc.Format("Shortcut_Error_Reserved", display);
                return false;
        }

        var current = _settings.Current.Hotkeys;
        if (HotkeyRules.FindConflict(current, item.Action, gesture) is { } other)
        {
            item.ErrorText = _loc.Format("Shortcut_Error_Conflict", display, _loc.Get(TitleKey(other)));
            return false;
        }

        _settings.Update(s => s with { Hotkeys = s.Hotkeys.With(item.Action, gesture.ToString()) });
        item.Gesture = _settings.Current.Hotkeys.Get(item.Action);
        return true;
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var item in Items)
        {
            item.ErrorText = null;
        }

        _settings.Update(s => s with { Hotkeys = HotkeySettings.Defaults });
    }

    // ---- Sync ---------------------------------------------------------------------------------------

    private void SyncGestures()
    {
        foreach (var item in Items)
        {
            item.Gesture = _settings.Current.Hotkeys.Get(item.Action);
        }
    }

    private void SyncStatus()
    {
        foreach (var item in Items)
        {
            item.Status = _shortcuts.GetStatus(item.Action);
            item.StatusText = _loc.Get(item.Status switch
            {
                ShortcutStatus.Active => "Shortcut_Status_Active",
                ShortcutStatus.InUse => "Shortcut_Status_InUse",
                ShortcutStatus.Invalid => "Shortcut_Status_Invalid",
                _ => "Shortcut_Status_Unassigned",
            });
        }
    }

    private void SyncTexts()
    {
        foreach (var item in Items)
        {
            item.Title = _loc.Get(TitleKey(item.Action));
            item.Description = _loc.Get($"Shortcut_{item.Action}_Description");
            var defaultGesture = HotkeyDisplay.Format(HotkeySettings.Defaults.Get(item.Action));
            item.DefaultText = _loc.Format("Shortcut_Default", defaultGesture.Length > 0 ? defaultGesture : _loc.Get("Shortcut_None"));
            item.RefreshTexts();
        }

        SyncStatus();
    }

    private static string TitleKey(HotkeyAction action) => $"Shortcut_{action}_Title";
}
