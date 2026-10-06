using KodizSignage.Core.Displays;
using KodizSignage.Core.Models;
using KodizSignage.Views;
using Serilog;

namespace KodizSignage.Services;

/// <summary>What one screen is doing right now (for the settings window and tray).</summary>
public enum ScreenStatus
{
    /// <summary>Disabled by the user.</summary>
    Off,
    /// <summary>Playback is stopped globally.</summary>
    Stopped,
    Playing,
    Empty,
    Closed,
    /// <summary>Its display is missing; hidden until it returns.</summary>
    Waiting,
    /// <summary>Its display is missing; temporarily shown on the primary display.</summary>
    Fallback,
}

public sealed record ScreenState(int Number, ScreenStatus Status, DisplayInfo? Display, Views.Player.NowPlaying? NowPlaying);

/// <summary>The player window and loop of one screen.</summary>
internal sealed class ScreenPlayer
{
    private readonly ILogger _log;
    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;

    public ScreenPlayer(int number, PlayerWindow window, ILogger log)
    {
        Number = number;
        Window = window;
        _log = log;
    }

    public int Number { get; }

    public PlayerWindow Window { get; }

    public bool IsActive => _cts is not null;

    public bool IsWaiting { get; set; }

    public bool IsOnFallback { get; set; }

    public DisplayInfo? Display { get; set; }

    public void Start()
    {
        if (IsActive)
        {
            return;
        }

        _log.Information("Screen {Number}: starting", Number);
        Window.Show();
        _cts = new CancellationTokenSource();
        _loop = Window.Engine.RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _log.Information("Screen {Number}: stopping", Number);
        _cts.Cancel();
        try
        {
            await _loop;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Screen {Number}: loop ended with exception", Number);
        }

        _cts.Dispose();
        _cts = null;
        Window.Hide();
    }

    public async Task CloseAsync()
    {
        await StopAsync();
        Window.AllowClose = true;
        Window.Close();
    }
}
