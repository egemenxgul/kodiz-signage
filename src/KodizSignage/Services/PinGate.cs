using KodizSignage.Core.Services;
using KodizSignage.Views;
using Serilog;

namespace KodizSignage.Services;

public interface IPinGate
{
    bool HasPin { get; }

    /// <summary>
    /// Returns true when the action may proceed: no PIN set, already unlocked in this session, or
    /// the correct PIN was entered.
    /// </summary>
    bool Unlock();

    /// <summary>Requires the PIN again for the next protected action (settings window hidden).</summary>
    void Lock();

    /// <summary>Asks for a new PIN and stores it. Returns false when cancelled.</summary>
    bool SetPin();

    void RemovePin();
}

/// <summary>Optional PIN protection for settings, stopping playback and exiting.</summary>
public sealed class PinGate : IPinGate
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settings;
    private readonly ILocalizationService _loc;
    private readonly IDialogService _dialogs;
    private readonly ILogger _log;
    private bool _unlocked;
    private int _failures;
    private DateTime _lockedUntil;

    public PinGate(ISettingsService settings, ILocalizationService loc, IDialogService dialogs, ILogger log)
    {
        _settings = settings;
        _loc = loc;
        _dialogs = dialogs;
        _log = log.ForContext<PinGate>();
    }

    public bool HasPin => !string.IsNullOrEmpty(_settings.Current.PinHash);

    public bool Unlock()
    {
        if (!HasPin || _unlocked)
        {
            return true;
        }

        if (DateTime.UtcNow < _lockedUntil)
        {
            _dialogs.Warning(_loc.Format("Pin_LockedOut", (int)Math.Ceiling((_lockedUntil - DateTime.UtcNow).TotalSeconds)));
            return false;
        }

        var ok = PinWindow.Ask(_loc.Get("Pin_Enter"), Verify, _loc.Get);
        if (ok)
        {
            _unlocked = true;
            _failures = 0;
        }

        return ok;
    }

    public void Lock() => _unlocked = false;

    public bool SetPin()
    {
        var pin = PinWindow.AskNew(_loc.Get("Pin_New"), _loc.Get);
        if (pin is null)
        {
            return false;
        }

        _settings.Update(s => s with { PinHash = PinHasher.Hash(pin) });
        _unlocked = true;
        _log.Information("Settings PIN set");
        return true;
    }

    public void RemovePin()
    {
        _settings.Update(s => s with { PinHash = null });
        _log.Information("Settings PIN removed");
    }

    private bool Verify(string pin)
    {
        if (DateTime.UtcNow < _lockedUntil)
        {
            return false;
        }

        if (PinHasher.Verify(pin, _settings.Current.PinHash))
        {
            return true;
        }

        if (++_failures >= MaxAttempts)
        {
            _failures = 0;
            _lockedUntil = DateTime.UtcNow + Lockout;
            _log.Warning("Too many wrong PIN attempts; locked for {Lockout}", Lockout);
        }

        return false;
    }
}
