using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using KodizSignage.Core;
using KodizSignage.Core.Hotkeys;
using KodizSignage.Core.Services;
using KodizSignage.Services;
using KodizSignage.ViewModels;
using KodizSignage.Views;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace KodizSignage;

public partial class App : Application
{
    private const string RestartArgument = "--restart";
    private const string SafeModeArgument = "--safe-mode";
    private const string SmokeTestArgument = "--smoke-test";
    private const string DataDirVariable = "KODIZ_SIGNAGE_DATA_DIR";

    /// <summary>After a crash loop the show starts automatically only after this delay.</summary>
    private static readonly TimeSpan SafeModeAutoPlayDelay = TimeSpan.FromMinutes(10);

    /// <summary>Running this long without crashing clears the crash history.</summary>
    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(30);

    private ServiceProvider? _services;
    private SingleInstanceService? _singleInstance;
    private SettingsWindow? _settingsWindow;
    private CrashGuard? _crashGuard;
    private bool _exiting;
    private int _fatalCount;

    private ILogger Log => Serilog.Log.Logger;

    /// <summary>Unhandled exceptions so far (the smoke test fails on any).</summary>
    internal int UnhandledCount { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var isSmokeTest = args.Contains(SmokeTestArgument);
        var launchedByWindows = args.Contains(StartupService.AutostartArgument);
        var isHandOver = args.Contains(RestartArgument) || args.Contains(InstallService.InstalledArgument);

        var paths = ResolvePaths(isSmokeTest);
        paths.EnsureCreated();
        ConfigureLogging(paths);
        RegisterGlobalExceptionHandlers();
        Log.Information("Kodiz Signage {Version} starting (args: {Args}, exe: {Exe})",
            GeneralViewModel.Version, string.Join(' ', args), Environment.ProcessPath);

        // Bindings (numbers, dates) use the OS culture instead of WPF's en-US default.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        _services = ConfigureServices(paths);
        _crashGuard = new CrashGuard(paths, Log);
        var lifetime = _services.GetRequiredService<AppLifetime>();
        lifetime.RestartHandler = () => RestartProcess(RestartArgument);
        lifetime.ExitHandler = () => ExitProcess(0);

        var settings = _services.GetRequiredService<ISettingsService>();
        settings.Load();
        _services.GetRequiredService<ILocalizationService>().Apply(settings.Current.Language);

        // ---- Installation / update / single instance ----
        _singleInstance = new SingleInstanceService(Log);
        if (!isSmokeTest)
        {
            if (!launchedByWindows && !HandleInstallation())
            {
                Shutdown();
                return;
            }

            if (!ClaimSingleInstance(wait: isHandOver))
            {
                Log.Information("Another instance is running; asking it to show settings");
                _singleInstance.NotifyExistingInstance();
                _singleInstance.Dispose();
                Shutdown();
                return;
            }
        }

        // ---- Data ----
        var playlist = _services.GetRequiredService<IPlaylistService>();
        playlist.Load();
        playlist.CleanupOrphanFiles();

        // ---- System integration ----
        if (!isSmokeTest)
        {
            _services.GetRequiredService<IStartupService>().Apply(settings.Current.StartWithWindows);
            var install = _services.GetRequiredService<InstallService>();
            if (install.IsRunningInstalled)
            {
                install.EnsureShortcut();
            }
        }

        var playback = _services.GetRequiredService<IPlaybackManager>();
        var tray = _services.GetRequiredService<TrayService>();
        tray.SettingsRequested += (_, _) => ShowSettings();
        tray.ToggleRequested += (_, _) => _ = TogglePlaybackAsync();
        tray.ExitRequested += async (_, _) => await ExitAsync(confirm: true);
        tray.Initialize();

        RegisterHotkeys(playback);

        _singleInstance.StartListening(command => Dispatcher.BeginInvoke(() =>
        {
            switch (command)
            {
                case SingleInstanceService.ShowSettingsCommand:
                    ShowSettings();
                    break;
                case SingleInstanceService.ExitCommand:
                    Log.Information("A newer version asked this instance to exit");
                    _ = ExitAsync(confirm: false);
                    break;
            }
        }));

        _services.GetRequiredService<IFolderSyncService>().Start();
        SessionEnding += (_, _) => FlushData();
        StartMemoryLogging();
        StartStabilityTimer();

        // ---- Go ----
        if (args.Contains(SafeModeArgument))
        {
            EnterSafeMode(playback);
        }
        else if (settings.Current.AutoPlayOnLaunch)
        {
            playback.Start();
        }

        if (isSmokeTest)
        {
            _ = SmokeTest.RunAsync(this, _services, ShowSettingsWindow, ExitProcess);
            return;
        }

        if (!settings.Current.AutoPlayOnLaunch || settings.IsFirstRun || (!launchedByWindows && playlist.Items.Count == 0))
        {
            ShowSettings();
        }
    }

    private static AppPaths ResolvePaths(bool isSmokeTest)
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppPaths(overridden);
        }

        return isSmokeTest
            ? new AppPaths(Path.Combine(Path.GetTempPath(), "kodiz-signage-smoke-" + Guid.NewGuid().ToString("N")))
            : AppPaths.Default;
    }

    // ---- Installation -------------------------------------------------------------------------------

    /// <summary>
    /// Offers to install (or update) the app into its stable folder. Returns false when this
    /// process handed over to the installed copy and must exit.
    /// </summary>
    private bool HandleInstallation()
    {
        var services = _services!;
        var install = services.GetRequiredService<InstallService>();
        var settings = services.GetRequiredService<ISettingsService>();
        var dialogs = services.GetRequiredService<IDialogService>();
        var loc = services.GetRequiredService<ILocalizationService>();

        switch (install.GetState())
        {
            case InstallState.RunningInstalled:
                return true;

            case InstallState.NotInstalled:
                if (settings.Current.InstallDeclined)
                {
                    return true;
                }

                if (!dialogs.Confirm(loc.Get("Install_Prompt")))
                {
                    settings.Update(s => s with { InstallDeclined = true });
                    return true;
                }

                return !InstallAndHandOver(install, dialogs, loc);

            case InstallState.InstalledOlder:
                if (!dialogs.Confirm(loc.Format("Update_Prompt", install.InstalledVersion?.ToString(3) ?? "?", InstallService.CurrentVersion.ToString(3))))
                {
                    return true;
                }

                return !InstallAndHandOver(install, dialogs, loc);

            default:
                // The same or a newer version is installed: use that one (it shows its settings if already running).
                Log.Information("Installed copy is current; starting it instead");
                install.LaunchInstalled(string.Empty);
                return false;
        }
    }

    private bool InstallAndHandOver(InstallService install, IDialogService dialogs, ILocalizationService loc)
    {
        // A running (older) instance holds the exe open: ask it to exit first.
        if (!_singleInstance!.TryClaim())
        {
            _singleInstance.NotifyExistingInstance(SingleInstanceService.ExitCommand);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!_singleInstance.TryClaim() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(300);
            }
        }

        // Release the mutex so the installed copy can take it.
        _singleInstance.Dispose();
        _singleInstance = new SingleInstanceService(Log);

        if (!install.Install())
        {
            dialogs.Warning(loc.Get("Install_Failed"));
            return false;
        }

        install.LaunchInstalled(InstallService.InstalledArgument);
        return true;
    }

    private bool ClaimSingleInstance(bool wait)
    {
        // After a restart/hand-over the old process may still be exiting; wait for it briefly.
        var deadline = DateTime.UtcNow + (wait ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        while (true)
        {
            if (_singleInstance!.TryClaim())
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(300);
        }
    }

    // ---- Services ------------------------------------------------------------------------------------

    private static ServiceProvider ConfigureServices(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(Serilog.Log.Logger);

        // Core
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IPlaylistService, PlaylistService>();
        services.AddSingleton<IVideoMetadataProvider, WpfVideoMetadataProvider>();
        services.AddSingleton<IMediaInspector, WpfMediaInspector>();
        services.AddSingleton<IMediaImportService, MediaImportService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IFolderSyncService, FolderSyncService>();

        // Platform
        services.AddSingleton<AppLifetime>();
        services.AddSingleton<IAppLifetime>(sp => sp.GetRequiredService<AppLifetime>());
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IDisplayService, DisplayService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<InstallService>();
        services.AddSingleton<IPowerService, PowerService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IShortcutService, ShortcutService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IPinGate, PinGate>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<IPlaybackManager, PlaybackManager>();
        services.AddSingleton<TrayService>();

        // UI
        services.AddSingleton<MediaViewModel>();
        services.AddSingleton<DisplayViewModel>();
        services.AddSingleton<GeneralViewModel>();
        services.AddSingleton<ShortcutsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();

        return services.BuildServiceProvider();
    }

    private void RegisterHotkeys(IPlaybackManager playback)
    {
        // The key combinations come from the settings (Shortcuts tab) and are re-registered live.
        var shortcuts = _services!.GetRequiredService<IShortcutService>();
        shortcuts.SetHandler(HotkeyAction.ShowSettings, ShowSettings);
        shortcuts.SetHandler(HotkeyAction.TogglePlayback, () => _ = TogglePlaybackAsync());
        shortcuts.SetHandler(HotkeyAction.NextItem, playback.Next);
        shortcuts.SetHandler(HotkeyAction.Exit, () => _ = ExitAsync(confirm: true));
        shortcuts.Start();
    }

    // ---- Protected actions (PIN) -----------------------------------------------------------------

    private void ShowSettings()
    {
        if (_services is null || _exiting)
        {
            return;
        }

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.ShowAndActivate();
            return;
        }

        if (_services.GetRequiredService<IPinGate>().Unlock())
        {
            ShowSettingsWindow();
        }
    }

    private SettingsWindow ShowSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = _services!.GetRequiredService<SettingsWindow>();
            _settingsWindow.HiddenByUser += OnSettingsHidden;
        }

        _settingsWindow.ShowAndActivate();
        return _settingsWindow;
    }

    private void OnSettingsHidden(object? sender, EventArgs e)
    {
        var services = _services!;
        services.GetRequiredService<IPinGate>().Lock();
        services.GetRequiredService<MediaViewModel>().CommitPendingDelete();

        // Windows 11 hides tray icons by default: tell the user once where the app went.
        var settings = services.GetRequiredService<ISettingsService>();
        if (!settings.Current.TrayHintShown)
        {
            var loc = services.GetRequiredService<ILocalizationService>();
            var shortcut = HotkeyDisplay.Format(settings.Current.Hotkeys.ShowSettings);
            services.GetRequiredService<TrayService>().ShowNotification(
                loc.Get("TrayHint_Title"),
                loc.Format("TrayHint_Text", shortcut.Length > 0 ? shortcut : loc.Get("Shortcut_None")));
            settings.Update(s => s with { TrayHintShown = true });
        }
    }

    private async Task TogglePlaybackAsync()
    {
        var services = _services!;
        var playback = services.GetRequiredService<IPlaybackManager>();
        if (playback.IsRunning && !services.GetRequiredService<IPinGate>().Unlock())
        {
            return; // Stopping the show is protected; starting it is not.
        }

        await playback.ToggleAsync();
    }

    private async Task ExitAsync(bool confirm)
    {
        if (_exiting || _services is null)
        {
            return;
        }

        var loc = _services.GetRequiredService<ILocalizationService>();
        if (confirm && (!_services.GetRequiredService<IPinGate>().Unlock() ||
                        !_services.GetRequiredService<IDialogService>().Confirm(loc.Get("Confirm_Exit"))))
        {
            return;
        }

        _exiting = true;
        Log.Information("Exiting");
        try
        {
            _services.GetRequiredService<MediaViewModel>().CommitPendingDelete();
            await _services.GetRequiredService<IPlaybackManager>().ShutdownAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while stopping playback");
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.AllowClose = true;
            _settingsWindow.Close();
        }

        Shutdown();
    }

    // ---- Safe mode & stability -----------------------------------------------------------------

    private void EnterSafeMode(IPlaybackManager playback)
    {
        var services = _services!;
        var loc = services.GetRequiredService<ILocalizationService>();
        Log.Warning("Started in safe mode after repeated crashes; playback starts in {Delay}", SafeModeAutoPlayDelay);
        var vm = services.GetRequiredService<SettingsViewModel>();
        vm.SafeModeText = loc.Format("SafeMode_Text", (int)SafeModeAutoPlayDelay.TotalMinutes);
        ShowSettingsWindow();

        // Unattended PCs must recover by themselves: try again later.
        var timer = new DispatcherTimer { Interval = SafeModeAutoPlayDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!playback.IsRunning && services.GetRequiredService<ISettingsService>().Current.AutoPlayOnLaunch)
            {
                Log.Information("Safe mode delay over, starting playback");
                playback.Start();
            }
        };
        timer.Start();
    }

    private void StartStabilityTimer()
    {
        var timer = new DispatcherTimer { Interval = StableAfter };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _crashGuard?.Reset();
        };
        timer.Start();
    }

    private void StartMemoryLogging()
    {
        LogMemory();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        timer.Tick += (_, _) => LogMemory();
        timer.Start();
    }

    private void LogMemory()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            Log.Information("Memory: working set {WorkingSet} MB, private {Private} MB, managed {Managed} MB, handles {Handles}, uptime {Uptime}",
                process.WorkingSet64 / (1024 * 1024),
                process.PrivateMemorySize64 / (1024 * 1024),
                GC.GetTotalMemory(false) / (1024 * 1024),
                process.HandleCount,
                DateTime.Now - process.StartTime);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Memory statistics unavailable");
        }
    }

    // ---- Exit / restart ------------------------------------------------------------------------------

    protected override void OnExit(ExitEventArgs e)
    {
        FlushData();
        _services?.GetService<TrayService>()?.Dispose();
        _services?.GetService<IHotkeyService>()?.Dispose();
        (_services?.GetService<IFolderSyncService>() as IDisposable)?.Dispose();
        _singleInstance?.Dispose();
        Log.Information("Exited with code {Code}", e.ApplicationExitCode);
        Serilog.Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void FlushData()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            Task.WhenAll(
                    _services.GetRequiredService<ISettingsService>().FlushAsync(),
                    _services.GetRequiredService<IPlaylistService>().FlushAsync())
                .Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Flushing data failed");
        }
    }

    /// <summary>Starts a new process of this exe and terminates the current one at once.</summary>
    private void RestartProcess(string arguments)
    {
        _exiting = true;
        try
        {
            Log.Warning("Restarting application ({Arguments})", arguments);
            Serilog.Log.CloseAndFlush();
            if (Environment.ProcessPath is { } exe)
            {
                Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
            }
        }
        catch
        {
            // Nothing else we can do.
        }

        Environment.Exit(0);
    }

    private void ExitProcess(int code)
    {
        _exiting = true;
        Log.Information("Exiting process with code {Code}", code);
        Serilog.Log.CloseAndFlush();
        Environment.Exit(code);
    }

    // ---- Logging & error handling ---------------------------------------------------------------

    private static void ConfigureLogging(AppPaths paths)
    {
        Serilog.Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogFolder, "kodiz-signage-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(2),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // UI thread: log and keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            UnhandledCount++;
            Log.Error(args.Exception, "Unhandled UI exception (continuing)");
            args.Handled = true;
            if (Interlocked.Increment(ref _fatalCount) > 50)
            {
                // Something is badly broken (exception storm) – restart cleanly.
                RestartAfterCrash();
            }
        };

        // Fire-and-forget tasks.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            UnhandledCount++;
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        // Background threads: the runtime will terminate the process, so restart ourselves.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Fatal unhandled exception (terminating: {Terminating})", args.IsTerminating);
            if (args.IsTerminating)
            {
                RestartAfterCrash();
            }
        };

        // Reset the exception-storm counter regularly.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) => Interlocked.Exchange(ref _fatalCount, 0);
        timer.Start();
    }

    private void RestartAfterCrash()
    {
        if (_exiting)
        {
            return;
        }

        // Too many crashes in a short time: come back in safe mode instead of looping.
        var safeMode = _crashGuard?.RegisterRestart(DateTime.Now) ?? false;
        RestartProcess(safeMode ? $"{RestartArgument} {SafeModeArgument}" : RestartArgument);
    }
}
