using Microsoft.Win32;
using Serilog;

namespace KodizSignage.Services;

public interface IStartupService
{
    bool IsEnabled { get; }

    /// <summary>
    /// True when the user disabled the entry in Task Manager → Startup apps. Windows then skips it
    /// although the Run entry exists.
    /// </summary>
    bool IsBlockedByTaskManager { get; }

    /// <summary>
    /// Creates, updates (if the exe moved) or removes the HKCU Run entry. With
    /// <paramref name="clearTaskManagerBlock"/> a Task-Manager "disabled" flag is removed as well
    /// (only on an explicit user action – never silently).
    /// </summary>
    void Apply(bool enabled, bool clearTaskManagerBlock = false);
}

/// <summary>Autostart through HKCU\...\Run – needs no administrator rights.</summary>
public sealed class StartupService : IStartupService
{
    public const string AutostartArgument = "--autostart";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KodizSignage";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly ILogger _log;

    public StartupService(ILogger log)
    {
        _log = log.ForContext<StartupService>();
    }

    private static string Command => $"\"{Environment.ProcessPath}\" {AutostartArgument}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public bool IsBlockedByTaskManager
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
                // First byte: even = enabled (0x02/0x06), odd = disabled (0x03/0x07).
                return key?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public void Apply(bool enabled, bool clearTaskManagerBlock = false)
    {
        if (enabled && clearTaskManagerBlock && IsBlockedByTaskManager)
        {
            try
            {
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
                _log.Information("Task Manager startup block removed");
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not remove the Task Manager startup block");
            }
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                var existing = key.GetValue(ValueName) as string;
                if (!string.Equals(existing, Command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, Command, RegistryValueKind.String);
                    _log.Information("Autostart entry set to {Command}", Command);
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _log.Information("Autostart entry removed");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Updating autostart entry failed");
        }
    }
}
