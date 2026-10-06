namespace KodizSignage.Services;

/// <summary>Lets view models restart or exit the application without referencing App.</summary>
public interface IAppLifetime
{
    /// <summary>Starts a fresh process and exits this one immediately (no data is flushed).</summary>
    void Restart();

    /// <summary>Exits this process immediately (e.g. after handing over to the installed copy).</summary>
    void ExitNow();
}

public sealed class AppLifetime : IAppLifetime
{
    public Action? RestartHandler { get; set; }

    public Action? ExitHandler { get; set; }

    public void Restart() => RestartHandler?.Invoke();

    public void ExitNow() => ExitHandler?.Invoke();
}
