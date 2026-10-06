using Serilog;

namespace KodizSignage.Core.Storage;

/// <summary>
/// Persists snapshots of a document on a background thread. Rapid successive saves
/// (e.g. dragging a slider) are coalesced so only the latest snapshot is written.
/// </summary>
public sealed class JsonStore<T> where T : class
{
    private readonly string _path;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private T? _pending;
    private Task _lastWrite = Task.CompletedTask;

    public JsonStore(string path, ILogger log)
    {
        _path = path;
        _log = log;
    }

    public string FilePath => _path;

    public T Load(Func<T> createDefault) => AtomicJsonFile.Read(_path, createDefault, _log);

    /// <summary>Queues <paramref name="snapshot"/> for writing. The snapshot must not be mutated afterwards.</summary>
    public Task SaveAsync(T snapshot)
    {
        lock (_gate)
        {
            _pending = snapshot;
            _lastWrite = Task.Run(WritePendingAsync);
            return _lastWrite;
        }
    }

    /// <summary>Waits until every queued snapshot is on disk.</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            return _lastWrite;
        }
    }

    private async Task WritePendingAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            T? snapshot;
            lock (_gate)
            {
                snapshot = _pending;
                _pending = null;
            }

            if (snapshot is null)
            {
                return; // Already written by an earlier queued call.
            }

            AtomicJsonFile.Write(_path, snapshot);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Saving {File} failed", _path);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
