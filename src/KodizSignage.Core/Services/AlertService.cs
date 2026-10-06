using Serilog;

namespace KodizSignage.Core.Services;

public enum AlertStyle
{
    Info,
    Urgent,
}

/// <summary>A full-screen notice over all screens ("We are closed", "Order 42 is ready").</summary>
public sealed record ScreenAlert(string Title, string Message, AlertStyle Style, DateTime? Until)
{
    public bool IsActiveAt(DateTime now) => Until is not { } until || now < until;
}

public interface IAlertService
{
    /// <summary>The notice being shown; null when none (or it expired).</summary>
    ScreenAlert? Current { get; }

    /// <summary>Raised (on any thread) when a notice appears, is removed or expires.</summary>
    event EventHandler? Changed;

    /// <summary>Shows a notice; <paramref name="duration"/> null = until removed.</summary>
    void Show(string title, string message, AlertStyle style, TimeSpan? duration);

    void Clear();
}

public sealed class AlertService : IAlertService, IDisposable
{
    public const int MaxTitle = 80;
    public const int MaxMessage = 300;

    private readonly Func<DateTime> _now;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private ScreenAlert? _current;
    private Timer? _expiry;

    public AlertService(ILogger log)
        : this(log, () => DateTime.Now)
    {
    }

    public AlertService(ILogger log, Func<DateTime> now)
    {
        _log = log.ForContext<AlertService>();
        _now = now;
    }

    public ScreenAlert? Current
    {
        get
        {
            lock (_gate)
            {
                return _current is { } alert && alert.IsActiveAt(_now()) ? alert : null;
            }
        }
    }

    public event EventHandler? Changed;

    public void Show(string title, string message, AlertStyle style, TimeSpan? duration)
    {
        title = Trim(title, MaxTitle);
        message = Trim(message, MaxMessage);
        if (title.Length == 0 && message.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _current = new ScreenAlert(title, message, Enum.IsDefined(style) ? style : AlertStyle.Info,
                duration is { } d && d > TimeSpan.Zero ? _now() + d : null);
            _expiry?.Dispose();
            _expiry = duration is { } wait && wait > TimeSpan.Zero
                ? new Timer(_ => Expire(), null, wait + TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan)
                : null;
        }

        _log.Information("Alert shown: {Title} ({Duration})", title, duration?.ToString() ?? "until removed");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        bool had;
        lock (_gate)
        {
            had = _current is not null;
            _current = null;
            _expiry?.Dispose();
            _expiry = null;
        }

        if (had)
        {
            _log.Information("Alert removed");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Expire()
    {
        lock (_gate)
        {
            if (_current is null || _current.IsActiveAt(_now()))
            {
                return;
            }

            _current = null;
        }

        _log.Information("Alert expired");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Trim(string? text, int max)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length > max ? value[..max] : value;
    }

    public void Dispose() => _expiry?.Dispose();
}
