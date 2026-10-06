using System.Windows;
using KodizSignage.Core.Hotkeys;
using KodizSignage.Core.Services;
using Serilog;

namespace KodizSignage.Services;

public enum ShortcutStatus
{
    Unassigned,
    Active,
    /// <summary>Another application owns the combination.</summary>
    InUse,
    Invalid,
}

public interface IShortcutService
{
    event EventHandler? StatusChanged;

    void SetHandler(HotkeyAction action, Action handler);

    /// <summary>Registers the shortcuts from the settings and keeps them in sync with later changes.</summary>
    void Start();

    /// <summary>
    /// Temporarily releases all global shortcuts (while the user records a new one; otherwise
    /// Windows would swallow the key press). Calls nest; every Suspend needs a Resume.
    /// </summary>
    void Suspend();

    void Resume();

    ShortcutStatus GetStatus(HotkeyAction action);
}

public sealed class ShortcutService : IShortcutService
{
    private const int BaseId = 0x4B00;

    private readonly IHotkeyService _hotkeys;
    private readonly ISettingsService _settings;
    private readonly ILogger _log;
    private readonly Dictionary<HotkeyAction, Action> _handlers = new();
    private readonly Dictionary<HotkeyAction, ShortcutStatus> _status = new();
    private int _suspendCount;
    private bool _started;

    public ShortcutService(IHotkeyService hotkeys, ISettingsService settings, ILogger log)
    {
        _hotkeys = hotkeys;
        _settings = settings;
        _log = log.ForContext<ShortcutService>();
    }

    public event EventHandler? StatusChanged;

    public void SetHandler(HotkeyAction action, Action handler) => _handlers[action] = handler;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _settings.Changed += (_, e) =>
        {
            if (e.OldSettings.Hotkeys != e.NewSettings.Hotkeys)
            {
                Application.Current.Dispatcher.BeginInvoke(Apply);
            }
        };
        Apply();
    }

    public void Suspend()
    {
        if (_suspendCount++ == 0)
        {
            _hotkeys.UnregisterAll();
        }
    }

    public void Resume()
    {
        if (_suspendCount > 0 && --_suspendCount == 0)
        {
            Apply();
        }
    }

    public ShortcutStatus GetStatus(HotkeyAction action) =>
        _status.TryGetValue(action, out var status) ? status : ShortcutStatus.Unassigned;

    private void Apply()
    {
        if (_suspendCount > 0 || !_started)
        {
            return; // Re-applied on Resume.
        }

        _hotkeys.UnregisterAll();
        var hotkeys = _settings.Current.Hotkeys;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            _status[action] = Register(action, hotkeys.Get(action));
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private ShortcutStatus Register(HotkeyAction action, string text)
    {
        if (!HotkeyGesture.TryParse(text, out var gesture) || gesture.IsEmpty)
        {
            return gesture.IsEmpty ? ShortcutStatus.Unassigned : ShortcutStatus.Invalid;
        }

        if (HotkeyRules.Validate(gesture) != HotkeyValidation.Valid)
        {
            return ShortcutStatus.Invalid;
        }

        if (!_handlers.TryGetValue(action, out var handler))
        {
            _log.Warning("No handler for shortcut {Action}", action);
            return ShortcutStatus.Invalid;
        }

        return _hotkeys.Register(BaseId + (int)action, gesture, handler) switch
        {
            HotkeyRegistration.Registered => ShortcutStatus.Active,
            HotkeyRegistration.InUse => ShortcutStatus.InUse,
            _ => ShortcutStatus.Invalid,
        };
    }
}
