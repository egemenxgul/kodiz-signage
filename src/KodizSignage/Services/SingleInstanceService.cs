using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Serilog;

namespace KodizSignage.Services;

/// <summary>
/// Ensures one instance per user session (named mutex) and lets a second launch ask the running
/// instance to show its settings window (named pipe).
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    public const string ShowSettingsCommand = "show-settings";

    /// <summary>Sent by a newer exe that wants to replace the installed one.</summary>
    public const string ExitCommand = "exit-for-update";

    private readonly ILogger _log;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private Mutex? _mutex;
    private bool _ownsMutex;

    public SingleInstanceService(ILogger log)
    {
        _log = log.ForContext<SingleInstanceService>();
        var session = Process.GetCurrentProcess().SessionId;
        _name = $"KodizSignage-{Environment.UserName}-{session}";
    }

    private string PipeName => _name + "-ipc";

    /// <summary>Returns true if this process is the first instance.</summary>
    public bool TryClaim()
    {
        if (_ownsMutex)
        {
            return true;
        }

        _mutex ??= new Mutex(initiallyOwned: false, @"Local\" + _name);
        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true; // Previous instance crashed; we own it now.
        }

        return _ownsMutex;
    }

    /// <summary>Asks the running instance to show its settings window.</summary>
    public bool NotifyExistingInstance(string command = ShowSettingsCommand)
    {
        try
        {
            // We were started by the user, so we may hand our foreground right to the running
            // instance; otherwise Windows would only flash its settings window in the taskbar.
            Native.NativeMethods.AllowSetForegroundWindow(Native.NativeMethods.ASFW_ANY);

            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(command + "\n");
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not reach the running instance");
            return false;
        }
    }

    /// <summary>Listens for commands from later launches. <paramref name="onCommand"/> runs on a background thread.</summary>
    public void StartListening(Action<string> onCommand)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        onCommand(line.Trim());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "IPC listener error");
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from a different thread; the OS frees it on exit anyway.
            }
        }

        _mutex?.Dispose();
    }
}
