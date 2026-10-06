using System.Diagnostics;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Core;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Services;
using Serilog;

namespace KodizSignage.ViewModels;

public sealed partial class GeneralViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IStartupService _startup;
    private readonly ILocalizationService _loc;
    private readonly AppPaths _paths;
    private readonly IPinGate _pin;
    private readonly IBackupService _backup;
    private readonly InstallService _install;
    private readonly IPlaybackManager _playback;
    private readonly IFolderSyncService _folderSync;
    private readonly IDialogService _dialogs;
    private readonly IAppLifetime _lifetime;
    private readonly IUpdateService _updates;
    private readonly ILogger _log;
    private bool _syncing;

    public GeneralViewModel(
        ISettingsService settings,
        IStartupService startup,
        ILocalizationService loc,
        AppPaths paths,
        IPinGate pin,
        IBackupService backup,
        InstallService install,
        IPlaybackManager playback,
        IFolderSyncService folderSync,
        IDialogService dialogs,
        IAppLifetime lifetime,
        IUpdateService updates,
        ILogger log)
    {
        _updates = updates;
        _updates.StateChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(SyncUpdateState);
        _settings = settings;
        _startup = startup;
        _loc = loc;
        _paths = paths;
        _pin = pin;
        _backup = backup;
        _install = install;
        _playback = playback;
        _folderSync = folderSync;
        _dialogs = dialogs;
        _lifetime = lifetime;
        _log = log.ForContext<GeneralViewModel>();

        OpeningHours = new ScheduleEditor(loc, autoCommit: true, (days, open, close) =>
            Save(s => s with
            {
                OperatingHours = s.OperatingHours with
                {
                    Days = days,
                    Open = open ?? TimeOnly.MinValue,
                    Close = close ?? TimeOnly.MinValue, // equal times = all day
                },
            }));

        Languages = new[]
        {
            new OptionItem<AppLanguage>(AppLanguage.Auto, "Language_Auto", loc),
            new OptionItem<AppLanguage>(AppLanguage.Turkish, "Language_Turkish", loc),
            new OptionItem<AppLanguage>(AppLanguage.English, "Language_English", loc),
        };
        Transitions = new[]
        {
            new OptionItem<TransitionType>(TransitionType.None, "Transition_None", loc),
            new OptionItem<TransitionType>(TransitionType.Fade, "Transition_Fade", loc),
        };
        ScalingModes = new[]
        {
            new OptionItem<ScalingMode>(ScalingMode.Fit, "Scaling_Fit", loc),
            new OptionItem<ScalingMode>(ScalingMode.Fill, "Scaling_Fill", loc),
            new OptionItem<ScalingMode>(ScalingMode.Stretch, "Scaling_Stretch", loc),
        };

        _loc.LanguageChanged += (_, _) =>
        {
            foreach (var o in Languages) o.Refresh(_loc);
            foreach (var o in Transitions) o.Refresh(_loc);
            foreach (var o in ScalingModes) o.Refresh(_loc);
            OnPropertyChanged(nameof(VersionText));
            OnPropertyChanged(nameof(LicenseText));
            SyncUpdateState();
            RefreshSystemInfo();
        };
        _settings.Changed += (_, _) => Application.Current.Dispatcher.BeginInvoke(SyncFromSettings);
        SyncFromSettings();
    }

    public IReadOnlyList<OptionItem<AppLanguage>> Languages { get; }
    public IReadOnlyList<OptionItem<TransitionType>> Transitions { get; }
    public IReadOnlyList<OptionItem<ScalingMode>> ScalingModes { get; }

    public IReadOnlyList<string> ColorPresets { get; } = new[] { "#000000", "#FFFFFF", "#1E1E1E", "#0F172A", "#7F1D1D", "#14532D" };

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "1.0.0";

    public string VersionText => _loc.Format("General_Version", Version);

    public string DataFolder => _paths.Root;

    public ScheduleEditor OpeningHours { get; }

    public string ExePath => InstallService.CurrentExe;

    public bool IsInstalled => _install.IsRunningInstalled;

    public bool HasPin => _pin.HasPin;

    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _autoInstallUpdates;
    [ObservableProperty] private string _updateStatusText = string.Empty;
    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty] private bool _isUpdateBusy;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _hasReleasePage;

    public string LicenseText => _loc.Format("License_Line", "PolyForm Noncommercial 1.0.0");

    public string CopyrightText => "© 2026 egemenxgul · github.com/" + UpdateService.Repository;

    [ObservableProperty] private bool _operatingHoursEnabled;
    [ObservableProperty] private bool _allowDisplaySleep;
    [ObservableProperty] private bool _watchFolderEnabled;
    [ObservableProperty] private string? _watchFolderPath;
    [ObservableProperty] private bool _isStartupBlocked;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = string.Empty;
    [ObservableProperty] private double _busyProgress;

    [ObservableProperty] private AppLanguage _language;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _autoPlayOnLaunch;
    [ObservableProperty] private double _defaultImageDuration;
    [ObservableProperty] private TransitionType _transition;
    [ObservableProperty] private double _transitionDuration;
    [ObservableProperty] private ScalingMode _scaling;
    [ObservableProperty] private string _backgroundColor = "#000000";
    [ObservableProperty] private bool _isBackgroundColorValid = true;
    [ObservableProperty] private bool _videoSoundEnabled;
    [ObservableProperty] private double _volumePercent;

    public bool IsFade => Transition == TransitionType.Fade;

    private void SyncFromSettings()
    {
        var s = _settings.Current;
        _syncing = true;
        try
        {
            Language = s.Language;
            StartWithWindows = s.StartWithWindows;
            AutoPlayOnLaunch = s.AutoPlayOnLaunch;
            DefaultImageDuration = s.DefaultImageDurationSeconds;
            Transition = s.Transition;
            TransitionDuration = s.TransitionDurationSeconds;
            Scaling = s.Scaling;
            if (IsBackgroundColorValid)
            {
                BackgroundColor = s.BackgroundColor; // Don't overwrite text the user is still typing.
            }

            VideoSoundEnabled = s.VideoSoundEnabled;
            VolumePercent = Math.Round(s.VideoVolume * 100);
            OperatingHoursEnabled = s.OperatingHours.Enabled;
            AllowDisplaySleep = s.OperatingHours.AllowDisplaySleep;
            OpeningHours.Load(s.OperatingHours.Days, s.OperatingHours.Open, s.OperatingHours.Close);
            WatchFolderEnabled = s.WatchFolderEnabled;
            WatchFolderPath = s.WatchFolderPath;
            OnPropertyChanged(nameof(HasPin));
            CheckForUpdates = s.CheckForUpdates;
            AutoInstallUpdates = s.AutoInstallUpdates;
            RefreshSystemInfo();
            SyncUpdateState();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_syncing)
        {
            _settings.Update(change);
        }
    }

    partial void OnLanguageChanged(AppLanguage value)
    {
        Save(s => s with { Language = value });
        _loc.Apply(value);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        Save(s => s with { StartWithWindows = value });
        if (!_syncing)
        {
            // Turning it on is an explicit wish, so a Task Manager "disabled" flag is cleared too.
            _startup.Apply(value, clearTaskManagerBlock: value);
            RefreshSystemInfo();
        }
    }

    [RelayCommand]
    private void FixStartup()
    {
        _startup.Apply(true, clearTaskManagerBlock: true);
        RefreshSystemInfo();
    }

    /// <summary>Re-reads facts that can change outside the app (Task Manager, install location).</summary>
    public void RefreshSystemInfo()
    {
        IsStartupBlocked = StartWithWindows && _startup.IsBlockedByTaskManager;
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(ExePath));
    }

    partial void OnOperatingHoursEnabledChanged(bool value) =>
        Save(s => s with { OperatingHours = s.OperatingHours with { Enabled = value } });

    partial void OnAllowDisplaySleepChanged(bool value) =>
        Save(s => s with { OperatingHours = s.OperatingHours with { AllowDisplaySleep = value } });

    partial void OnWatchFolderEnabledChanged(bool value)
    {
        if (value && string.IsNullOrEmpty(WatchFolderPath) && !_syncing)
        {
            ChooseWatchFolder();
            if (string.IsNullOrEmpty(WatchFolderPath))
            {
                WatchFolderEnabled = false;
                return;
            }
        }

        Save(s => s with { WatchFolderEnabled = value });
    }

    [RelayCommand]
    private void ChooseWatchFolder()
    {
        var folder = _dialogs.PickFolder(_loc.Get("Folder_Choose"));
        if (folder is null)
        {
            return;
        }

        if (folder.StartsWith(_paths.Root, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.Warning(_loc.Get("Folder_NotDataFolder"));
            return;
        }

        WatchFolderPath = folder;
        Save(s => s with { WatchFolderPath = folder, WatchFolderEnabled = true });
    }

    [RelayCommand]
    private Task SyncFolderNow() => _folderSync.SyncNowAsync();

    // ---- Updates ------------------------------------------------------------------------------

    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s with { CheckForUpdates = value });

    partial void OnAutoInstallUpdatesChanged(bool value) => Save(s => s with { AutoInstallUpdates = value });

    [RelayCommand]
    private Task CheckUpdatesNow() => _updates.CheckAsync(manual: true);

    [RelayCommand]
    private void InstallUpdate()
    {
        if (_dialogs.Confirm(_loc.Format("Update_InstallConfirm", _updates.Release?.Version.ToString(3) ?? string.Empty)))
        {
            _updates.InstallNow();
        }
    }

    [RelayCommand]
    private void OpenReleasePage() => OpenUrl(_updates.Release?.PageUrl ?? $"https://github.com/{UpdateService.Repository}/releases");

    [RelayCommand]
    private void OpenSourceCode() => OpenUrl($"https://github.com/{UpdateService.Repository}");

    [RelayCommand]
    private void ViewLicense() => OpenResourceText("LICENSE.txt");

    [RelayCommand]
    private void ViewNotices() => OpenResourceText("THIRD-PARTY-NOTICES.txt");

    private void SyncUpdateState()
    {
        var version = _updates.Release?.Version.ToString(3) ?? string.Empty;
        var last = _updates.LastCheck is { } at ? _loc.Format("Update_LastCheck", at.ToString("g")) : _loc.Get("Update_NeverChecked");
        IsUpdateReady = _updates.State == UpdateState.Ready;
        IsUpdateBusy = _updates.State is UpdateState.Checking or UpdateState.Downloading;
        UpdateProgress = _updates.Progress * 100;
        HasReleasePage = _updates.Release is not null;
        UpdateStatusText = _updates.State switch
        {
            UpdateState.Checking => _loc.Get("Update_Checking"),
            UpdateState.Downloading => _loc.Format("Update_Downloading", version),
            UpdateState.Ready => _loc.Format(AutoInstallUpdates ? "Update_ReadyAuto" : "Update_Ready", version),
            UpdateState.UpToDate => _loc.Format("Update_UpToDate", Version) + " · " + last,
            UpdateState.Failed => _loc.Format("Update_Failed", _updates.Error == "network" ? _loc.Get("Update_NoNetwork") : _updates.Error ?? string.Empty),
            _ => last,
        };
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not open {Url}", url);
        }
    }

    private void OpenResourceText(string name)
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))?.Stream;
            if (stream is null)
            {
                return;
            }

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KodizSignage-" + name);
            using (stream)
            using (var file = System.IO.File.Create(path))
            {
                stream.CopyTo(file);
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not show {Name}", name);
        }
    }

    // ---- PIN ----------------------------------------------------------------------------------

    [RelayCommand]
    private void SetPin()
    {
        _pin.SetPin();
        OnPropertyChanged(nameof(HasPin));
    }

    [RelayCommand]
    private void RemovePin()
    {
        if (_dialogs.Confirm(_loc.Get("Pin_RemoveConfirm")))
        {
            _pin.RemovePin();
            OnPropertyChanged(nameof(HasPin));
        }
    }

    // ---- Backup -------------------------------------------------------------------------------

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        var path = _dialogs.PickSaveBackup($"kodiz-signage-{DateTime.Now:yyyy-MM-dd}.zip");
        if (path is null)
        {
            return;
        }

        if (await RunBusyAsync("Backup_Creating", progress => _backup.CreateAsync(path, progress, CancellationToken.None)))
        {
            _dialogs.Info(_loc.Format("Backup_Created", path));
        }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        var path = _dialogs.PickBackupToRestore();
        if (path is null || !_dialogs.Confirm(_loc.Get("Backup_RestoreConfirm")))
        {
            return;
        }

        await _playback.StopAsync(); // Releases open media files.
        var ok = await RunBusyAsync("Backup_Restoring", progress => _backup.RestoreAsync(path, progress, CancellationToken.None));
        if (ok)
        {
            _dialogs.Info(_loc.Get("Backup_Restored"));
            _lifetime.Restart();
        }
    }

    private async Task<bool> RunBusyAsync(string key, Func<IProgress<double>, Task> work)
    {
        IsBusy = true;
        BusyText = _loc.Get(key);
        BusyProgress = 0;
        try
        {
            await work(new Progress<double>(p => BusyProgress = p * 100));
            return true;
        }
        catch (InvalidBackupException)
        {
            _dialogs.Warning(_loc.Get("Backup_Invalid"));
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Operation} failed", key);
            _dialogs.Warning(_loc.Format("Backup_Failed", ex.Message));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Install ------------------------------------------------------------------------------

    [RelayCommand]
    private void Install()
    {
        if (!_install.Install())
        {
            _dialogs.Warning(_loc.Get("Install_Failed"));
            return;
        }

        _dialogs.Info(_loc.Get("Install_Done"));
        _install.LaunchInstalled(InstallService.InstalledArgument);
        _lifetime.ExitNow(); // The installed copy takes over (it waits for this one to exit).
    }

    partial void OnAutoPlayOnLaunchChanged(bool value) => Save(s => s with { AutoPlayOnLaunch = value });

    partial void OnDefaultImageDurationChanged(double value)
    {
        if (double.IsFinite(value) && value >= AppSettings.MinImageDuration)
        {
            Save(s => s with { DefaultImageDurationSeconds = value });
        }
    }

    partial void OnTransitionChanged(TransitionType value)
    {
        OnPropertyChanged(nameof(IsFade));
        Save(s => s with { Transition = value });
    }

    partial void OnTransitionDurationChanged(double value) =>
        Save(s => s with { TransitionDurationSeconds = Math.Round(value, 1) });

    partial void OnScalingChanged(ScalingMode value) => Save(s => s with { Scaling = value });

    partial void OnBackgroundColorChanged(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 6 && normalized.All(Uri.IsHexDigit))
        {
            normalized = "#" + normalized;
        }

        IsBackgroundColorValid = AppSettings.IsValidColor(normalized);
        if (IsBackgroundColorValid)
        {
            Save(s => s with { BackgroundColor = normalized.ToUpperInvariant() });
        }
    }

    partial void OnVideoSoundEnabledChanged(bool value) => Save(s => s with { VideoSoundEnabled = value });

    partial void OnVolumePercentChanged(double value) =>
        Save(s => s with { VideoVolume = Math.Clamp(Math.Round(value) / 100.0, 0, 1) });

    [RelayCommand]
    private void PickColor(string? color)
    {
        if (color is not null)
        {
            BackgroundColor = color;
        }
    }

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(_paths.LogFolder);

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_paths.Root);

    private void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not open {Path}", path);
        }
    }
}
