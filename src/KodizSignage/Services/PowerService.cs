using KodizSignage.Native;
using Serilog;
using static KodizSignage.Native.NativeMethods;

namespace KodizSignage.Services;

public interface IPowerService
{
    /// <summary>
    /// Keeps the system awake; with <paramref name="keepDisplayOn"/> also the display.
    /// Call from the UI thread.
    /// </summary>
    void PreventSleep(bool keepDisplayOn = true);

    void AllowSleep();
}

/// <summary>
/// Wraps SetThreadExecutionState. The state is bound to the calling thread, so both calls must
/// come from the (long-lived) UI thread.
/// </summary>
public sealed class PowerService : IPowerService
{
    private readonly ILogger _log;
    private EXECUTION_STATE? _state;

    public PowerService(ILogger log)
    {
        _log = log.ForContext<PowerService>();
    }

    public void PreventSleep(bool keepDisplayOn = true)
    {
        var state = EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED;
        if (keepDisplayOn)
        {
            state |= EXECUTION_STATE.ES_DISPLAY_REQUIRED;
        }

        if (_state == state)
        {
            return;
        }

        if (SetThreadExecutionState(state) == 0)
        {
            _log.Warning("SetThreadExecutionState failed");
            return;
        }

        _state = state;
        _log.Information(keepDisplayOn ? "Sleep and display-off prevented" : "Sleep prevented, display may turn off");
    }

    public void AllowSleep()
    {
        if (_state is null)
        {
            return;
        }

        SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        _state = null;
        _log.Information("Sleep allowed again");
    }
}
