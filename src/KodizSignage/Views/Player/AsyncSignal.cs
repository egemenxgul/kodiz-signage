namespace KodizSignage.Views.Player;

/// <summary>Auto-reset signal that can be awaited with a timeout.</summary>
internal sealed class AsyncSignal
{
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Set() => _tcs.TrySetResult();

    /// <summary>Waits until <see cref="Set"/> is called or the timeout elapses. Throws only on cancellation.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var task = _tcs.Task;
        if (!task.IsCompleted)
        {
            var delay = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
            await Task.WhenAny(task, Task.Delay(delay, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (task.IsCompleted)
        {
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
