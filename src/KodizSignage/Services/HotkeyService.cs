using System.Windows.Input;
using System.Windows.Interop;
using KodizSignage.Core.Hotkeys;
using Serilog;
using static KodizSignage.Native.NativeMethods;

namespace KodizSignage.Services;

public enum HotkeyRegistration
{
    Registered,
    /// <summary>Another application (or Windows) already owns the combination.</summary>
    InUse,
    /// <summary>The key name is not a known key.</summary>
    UnknownKey,
}

public interface IHotkeyService : IDisposable
{
    /// <summary>Registers a system-wide hotkey under <paramref name="id"/> (replacing an existing one with that id).</summary>
    HotkeyRegistration Register(int id, HotkeyGesture gesture, Action callback);

    void UnregisterAll();
}

/// <summary>Global hotkeys via RegisterHotKey on a hidden message-only window.</summary>
public sealed class HotkeyService : IHotkeyService
{
    private readonly ILogger _log;
    private readonly Dictionary<int, Action> _callbacks = new();
    private HwndSource? _source;

    public HotkeyService(ILogger log)
    {
        _log = log.ForContext<HotkeyService>();
    }

    public HotkeyRegistration Register(int id, HotkeyGesture gesture, Action callback)
    {
        _source ??= CreateSource();
        Unregister(id);

        if (!Enum.TryParse<Key>(gesture.Key, ignoreCase: true, out var key) || key == Key.None)
        {
            _log.Warning("Hotkey {Gesture} has an unknown key", gesture);
            return HotkeyRegistration.UnknownKey;
        }

        uint mods = MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= MOD_CONTROL;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= MOD_ALT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= MOD_WIN;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_source.Handle, id, mods, vk))
        {
            _log.Warning("Hotkey {Gesture} could not be registered (in use by another app?)", gesture);
            return HotkeyRegistration.InUse;
        }

        _callbacks[id] = callback;
        _log.Information("Hotkey {Gesture} registered", gesture);
        return HotkeyRegistration.Registered;
    }

    public void UnregisterAll()
    {
        foreach (var id in _callbacks.Keys.ToList())
        {
            Unregister(id);
        }
    }

    private void Unregister(int id)
    {
        if (_source is not null && _callbacks.Remove(id))
        {
            UnregisterHotKey(_source.Handle, id);
        }
    }

    private HwndSource CreateSource()
    {
        var parameters = new HwndSourceParameters("KodizSignageHotkeys")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        var source = new HwndSource(parameters);
        source.AddHook(WndProc);
        return source;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            handled = true;
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Hotkey handler failed");
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is null)
        {
            return;
        }

        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }
}
