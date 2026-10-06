using System.Diagnostics;
using System.IO;
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
    private readonly IPlayStatsService _stats;
    private readonly IWebPanelService _web;
    private readonly IThemeService _themeService;
    private readonly IMusicService _music;
    private readonly IAutoBackupService _autoBackup;
    private readonly IPlaylistService _playlist;
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
        IPlayStatsService stats,
        IWebPanelService web,
        IThemeService theme,
        IMusicService music,
        IAutoBackupService autoBackup,
        IPlaylistService playlist,
        ILogger log)
    {
        _playlist = playlist;
        _autoBackup = autoBackup;
        _autoBackup.StateChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(SyncAutoBackupState);
        _music = music;
        _music.StateChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(SyncMusicState);
        _themeService = theme;
        _web = web;
        _web.StateChanged += (_, _) => SyncWebState();
        _stats = stats;
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

        AutoBackupIntervals = new[]
        {
            new OptionItem<int>(1, "AutoBackup_Daily", loc),
            new OptionItem<int>(7, "AutoBackup_Weekly", loc),
            new OptionItem<int>(30, "AutoBackup_Monthly", loc),
        };
        AutoBackupKeepChoices = new[] { 2, 4, 8, 12 }.Select(n => new OptionItem<int>(n, "AutoBackup_Keep" + n, loc)).ToList();
        MusicDuringChoices = new[]
        {
            new OptionItem<int>(0, "Music_DuringLower", loc),
            new OptionItem<int>(1, "Music_DuringPause", loc),
            new OptionItem<int>(2, "Music_DuringIgnore", loc),
        };
        AutoLockChoices = new[]
        {
            new OptionItem<int>(1, "Pin_AutoLock1", loc),
            new OptionItem<int>(5, "Pin_AutoLock5", loc),
            new OptionItem<int>(15, "Pin_AutoLock15", loc),
            new OptionItem<int>(60, "Pin_AutoLock60", loc),
            new OptionItem<int>(0, "Pin_AutoLockNever", loc),
        };
        Themes = new[]
        {
            new OptionItem<AppTheme>(AppTheme.System, "AppTheme_System", loc),
            new OptionItem<AppTheme>(AppTheme.Light, "AppTheme_Light", loc),
            new OptionItem<AppTheme>(AppTheme.Dark, "AppTheme_Dark", loc),
        };
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
            new OptionItem<TransitionType>(TransitionType.Slide, "Transition_Slide", loc),
            new OptionItem<TransitionType>(TransitionType.Zoom, "Transition_Zoom", loc),
            new OptionItem<TransitionType>(TransitionType.SlideUp, "Transition_SlideUp", loc),
            new OptionItem<TransitionType>(TransitionType.FadeThroughBackground, "Transition_FadeThroughBackground", loc),
            new OptionItem<TransitionType>(TransitionType.Wipe, "Transition_Wipe", loc),
            new OptionItem<TransitionType>(TransitionType.Circle, "Transition_Circle", loc),
            new OptionItem<TransitionType>(TransitionType.Blur, "Transition_Blur", loc),
        };
        Motions = new[]
        {
            new OptionItem<ImageMotion>(ImageMotion.None, "Motion_None", loc),
            new OptionItem<ImageMotion>(ImageMotion.KenBurns, "Motion_KenBurns", loc),
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
            foreach (var o in Themes) o.Refresh(_loc);
            foreach (var o in AutoLockChoices) o.Refresh(_loc);
            foreach (var o in MusicDuringChoices) o.Refresh(_loc);
            foreach (var o in AutoBackupIntervals.Concat(AutoBackupKeepChoices)) o.Refresh(_loc);
            SyncAutoBackupState();
            SyncMusicState();
            foreach (var o in Transitions) o.Refresh(_loc);
            foreach (var o in Motions) o.Refresh(_loc);
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

    public IReadOnlyList<OptionItem<AppTheme>> Themes { get; }

    [ObservableProperty] private AppTheme _theme;

    [ObservableProperty] private bool _longPressOpensSettings;

    [ObservableProperty] private int _pinAutoLockMinutes;

    public IReadOnlyList<OptionItem<int>> AutoLockChoices { get; private set; } = Array.Empty<OptionItem<int>>();

    partial void OnPinAutoLockMinutesChanged(int value) => Save(s => s with { PinAutoLockMinutes = value });

    partial void OnThemeChanged(AppTheme value)
    {
        Save(s => s with { Theme = value });
        _themeService.Apply(value);
    }

    partial void OnLongPressOpensSettingsChanged(bool value) => Save(s => s with { LongPressOpensSettings = value });
    public IReadOnlyList<OptionItem<TransitionType>> Transitions { get; }
    public IReadOnlyList<OptionItem<ImageMotion>> Motions { get; }

    [ObservableProperty] private ImageMotion _imageMotion;

    partial void OnImageMotionChanged(ImageMotion value) => Save(s => s with { ImageMotion = value });
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

    public bool IsFade => Transition != TransitionType.None;

    private void SyncFromSettings()
    {
        var s = _settings.Current;
        _syncing = true;
        try
        {
            Language = s.Language;
            Theme = s.Theme;
            LongPressOpensSettings = s.LongPressOpensSettings;
            PinAutoLockMinutes = s.PinAutoLockMinutes;
            MusicEnabled = s.Music.Enabled;
            MusicFolder = s.Music.Folder;
            MusicVolumePercent = Math.Round(s.Music.Volume * 100);
            MusicShuffle = s.Music.Shuffle;
            MusicDuring = (int)s.Music.DuringVideoSound;
            MusicFollowHours = s.Music.FollowOpeningHours;
            SyncMusicState();
            AutoBackupEnabled = s.AutoBackup.Enabled;
            AutoBackupFolder = s.AutoBackup.Folder;
            AutoBackupInterval = s.AutoBackup.IntervalDays;
            AutoBackupKeep = s.AutoBackup.Keep;
            SyncAutoBackupState();
            StartWithWindows = s.StartWithWindows;
            AutoPlayOnLaunch = s.AutoPlayOnLaunch;
            DefaultImageDuration = s.DefaultImageDurationSeconds;
            Transition = s.Transition;
            ImageMotion = s.ImageMotion;
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
            WebPanelEnabled = s.WebPanelEnabled;
            WebPanelPort = s.WebPanelPort;
            SyncWebState();
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

    /// <summary>"What's new" of an available update (Markdown simplified to plain text).</summary>
    [ObservableProperty] private string? _releaseNotes;

    private static string CleanNotes(string markdown) =>
        string.Join('\n', markdown.Replace("\r", string.Empty).Split('\n')
            .Select(l => l.TrimStart('#', ' ').Replace("**", string.Empty))
            .Where(l => !l.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase)))
        .Trim();

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
        ReleaseNotes = _updates.Release is { } release && release.IsNewerThan(InstallService.CurrentVersion, null) && !string.IsNullOrWhiteSpace(release.Notes)
            ? CleanNotes(release.Notes)
            : null;
        UpdateStatusText = _updates.State switch
        {
            UpdateState.Checking => _loc.Get("Update_Checking"),
            UpdateState.Downloading => _loc.Format("Update_Downloading", version),
            UpdateState.Ready => _loc.Format(AutoInstallUpdates ? "Update_ReadyAuto" : "Update_Ready", version),
            UpdateState.UpToDate => _loc.Format("Update_UpToDate", Version) + " · " + last,
            UpdateState.Failed => _loc.Format("Update_Failed", _updates.Error switch
            {
                "network" => _loc.Get("Update_NoNetwork"),
                "rate-limit" => _loc.Get("Update_RateLimit"),
                "not-found" => _loc.Get("Update_NotFound"),
                var other => other ?? string.Empty,
            }),
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
        if (_pin.SetPin())
        {
            OnPropertyChanged(nameof(HasPin));
            _dialogs.Info(_loc.Get("Pin_SetInfo"));
        }
    }

    [RelayCommand]
    private void RemovePin()
    {
        if (_dialogs.Confirm(_loc.Get("Pin_RemoveConfirm")) && _pin.RemovePin())
        {
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

    // ---- Automatic backup ------------------------------------------------------------------------

    public IReadOnlyList<OptionItem<int>> AutoBackupIntervals { get; }
    public IReadOnlyList<OptionItem<int>> AutoBackupKeepChoices { get; }

    [ObservableProperty] private bool _autoBackupEnabled;
    [ObservableProperty] private string? _autoBackupFolder;
    [ObservableProperty] private int _autoBackupInterval;
    [ObservableProperty] private int _autoBackupKeep;
    [ObservableProperty] private string _autoBackupStatus = string.Empty;
    [ObservableProperty] private bool _autoBackupRunning;

    private void SaveAutoBackup(Func<AutoBackupSettings, AutoBackupSettings> change) => Save(s => s with { AutoBackup = change(s.AutoBackup) });

    partial void OnAutoBackupEnabledChanged(bool value)
    {
        if (value && string.IsNullOrEmpty(AutoBackupFolder) && !_syncing)
        {
            PickAutoBackupFolder();
            if (string.IsNullOrEmpty(AutoBackupFolder))
            {
                Application.Current.Dispatcher.BeginInvoke(() => AutoBackupEnabled = false);
                return;
            }
        }

        SaveAutoBackup(a => a with { Enabled = value });
    }

    partial void OnAutoBackupIntervalChanged(int value) => SaveAutoBackup(a => a with { IntervalDays = value });
    partial void OnAutoBackupKeepChanged(int value) => SaveAutoBackup(a => a with { Keep = value });

    [RelayCommand]
    private void PickAutoBackupFolder()
    {
        if (_dialogs.PickFolder(_loc.Get("AutoBackup_PickFolder")) is { } folder)
        {
            AutoBackupFolder = folder;
            SaveAutoBackup(a => a with { Folder = folder });
        }
    }

    [RelayCommand]
    private Task BackupNowAsync() => _autoBackup.RunNowAsync();

    private void SyncAutoBackupState()
    {
        AutoBackupRunning = _autoBackup.IsRunning;
        var auto = _settings.Current.AutoBackup;
        AutoBackupStatus = _autoBackup.IsRunning ? _loc.Get("AutoBackup_Running")
            : _autoBackup.LastResult is { } result ? result
            : auto.LastRun is { } last ? _loc.Format("AutoBackup_Last", last.ToString("g"))
            : _loc.Get("AutoBackup_Never");
    }

    // ---- Background music ------------------------------------------------------------------------

    public IReadOnlyList<OptionItem<int>> MusicDuringChoices { get; }

    [ObservableProperty] private bool _musicEnabled;
    [ObservableProperty] private string? _musicFolder;
    [ObservableProperty] private double _musicVolumePercent;
    [ObservableProperty] private bool _musicShuffle;
    [ObservableProperty] private int _musicDuring;
    [ObservableProperty] private bool _musicFollowHours;
    [ObservableProperty] private string _musicStatus = string.Empty;
    [ObservableProperty] private bool _musicIsPlaying;

    private void SaveMusic(Func<MusicSettings, MusicSettings> change) => Save(s => s with { Music = change(s.Music) });

    partial void OnMusicEnabledChanged(bool value)
    {
        if (value && string.IsNullOrEmpty(MusicFolder) && !_syncing)
        {
            PickMusicFolder();
            if (string.IsNullOrEmpty(MusicFolder))
            {
                Application.Current.Dispatcher.BeginInvoke(() => MusicEnabled = false);
                return;
            }
        }

        SaveMusic(m => m with { Enabled = value });
    }

    partial void OnMusicVolumePercentChanged(double value) => SaveMusic(m => m with { Volume = Math.Round(value) / 100 });
    partial void OnMusicShuffleChanged(bool value) => SaveMusic(m => m with { Shuffle = value });
    partial void OnMusicDuringChanged(int value) => SaveMusic(m => m with { DuringVideoSound = (MusicDuring)Math.Clamp(value, 0, 2) });
    partial void OnMusicFollowHoursChanged(bool value) => SaveMusic(m => m with { FollowOpeningHours = value });

    [RelayCommand]
    private void PickMusicFolder()
    {
        if (_dialogs.PickFolder(_loc.Get("Music_PickFolder")) is { } folder)
        {
            MusicFolder = folder;
            SaveMusic(m => m with { Folder = folder });
        }
    }

    [RelayCommand]
    private void NextSong() => _music.Next();

    private void SyncMusicState()
    {
        MusicIsPlaying = _music.IsPlaying;
        MusicStatus = _music.IsPlaying && _music.CurrentSong is { } song
            ? _loc.Format("Music_Playing", song, _music.SongCount)
            : !_settings.Current.Music.Enabled ? _loc.Get("Music_Off")
            : _music.SongCount == 0 && _settings.Current.Music.Folder is not null ? _loc.Get("Music_NoSongs")
            : _loc.Get("Music_Waiting");
    }

    // ---- Phone panel ---------------------------------------------------------------------------

    [ObservableProperty] private bool _webPanelEnabled;
    [ObservableProperty] private double _webPanelPort;
    [ObservableProperty] private string _newWebPin = string.Empty;
    [ObservableProperty] private string _webStatusText = string.Empty;
    [ObservableProperty] private bool _webIsRunning;
    [ObservableProperty] private IReadOnlyList<string> _webUrls = Array.Empty<string>();
    [ObservableProperty] private FrameworkElement? _webQr;

    public bool HasWebPin => !string.IsNullOrEmpty(_settings.Current.WebPanelPinHash);

    private void SyncWebState()
    {
        OnPropertyChanged(nameof(HasWebPin));
        WebIsRunning = _web.IsRunning;
        WebUrls = _web.Urls;
        WebStatusText = _web.Error is { } error ? error
            : _web.IsRunning ? (WebUrls.Count > 0 ? _loc.Get("Web_Running") : _loc.Get("Web_NoNetwork"))
            : !HasWebPin ? _loc.Get("Web_NeedPin")
            : _loc.Get("Web_Off");
        WebQr = WebUrls.Count > 0 ? SlideRenderer.QrElement(WebUrls[0]) : null;
    }

    partial void OnWebPanelEnabledChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        if (value && !HasWebPin)
        {
            _dialogs.Warning(_loc.Get("Web_NeedPin"));
            Application.Current.Dispatcher.BeginInvoke(() => WebPanelEnabled = false);
            return;
        }

        Save(s => s with { WebPanelEnabled = value });
    }

    partial void OnWebPanelPortChanged(double value)
    {
        if (double.IsFinite(value) && value is >= 1024 and <= 65535)
        {
            Save(s => s with { WebPanelPort = (int)value });
        }
    }

    [RelayCommand]
    private void SaveWebPin()
    {
        var pin = NewWebPin.Trim();
        if (!PinHasher.IsValidPin(pin))
        {
            _dialogs.Warning(_loc.Get("Pin_Invalid"));
            return;
        }

        if (!_pin.Unlock())
        {
            return;
        }

        _settings.Update(s => s with { WebPanelPinHash = PinHasher.Hash(pin) });
        _web.SignOutAll();
        NewWebPin = string.Empty;
        _dialogs.Info(_loc.Get("Web_PinSaved"));
        SyncWebState();
    }

    [RelayCommand]
    private void CopyWebUrl()
    {
        if (WebUrls.Count > 0)
        {
            try
            {
                Clipboard.SetText(WebUrls[0]);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        }
    }

    // ---- Statistics chart -------------------------------------------------------------------------

    public System.Collections.ObjectModel.ObservableCollection<StatBar> TopMedia { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<StatBar> DailyBars { get; } = new();
    [ObservableProperty] private string _statsSummaryText = string.Empty;
    [ObservableProperty] private string _statsScreensText = string.Empty;
    [ObservableProperty] private bool _hasStats;

    private const double MaxBarWidth = 260;
    private const double MaxColumnHeight = 80;

    /// <summary>Recomputes the chart (window activated / refresh button).</summary>
    [RelayCommand]
    public void RefreshStats()
    {
        var summary = PlayStatsSummary.Build(_stats.Snapshot(), DateTime.Now);
        HasStats = summary.TotalPlays > 0;
        StatsSummaryText = _loc.Format("Stats_Last30", summary.TotalPlays, FormatDuration(TimeSpan.FromSeconds(summary.TotalSeconds)));

        var maxPlays = Math.Max(1, summary.TopMedia.Select(m => m.Plays).DefaultIfEmpty().Max());
        TopMedia.Clear();
        foreach (var media in summary.TopMedia)
        {
            TopMedia.Add(new StatBar(media.Title, media.Plays.ToString(System.Globalization.CultureInfo.CurrentCulture),
                MaxBarWidth * media.Plays / maxPlays, FormatDuration(TimeSpan.FromSeconds(media.Seconds))));
        }

        var maxDay = Math.Max(1, summary.Daily.Select(d => d.Plays).DefaultIfEmpty().Max());
        DailyBars.Clear();
        foreach (var day in summary.Daily)
        {
            DailyBars.Add(new StatBar(day.Day.ToString("d MMM ddd", System.Globalization.CultureInfo.CurrentUICulture), day.Plays.ToString(System.Globalization.CultureInfo.CurrentCulture),
                Math.Max(2, MaxColumnHeight * day.Plays / maxDay), string.Empty));
        }

        StatsScreensText = string.Join("   ·   ", summary.Screens.Select(s =>
            $"{(_settings.Current.GetScreen(s.Screen)?.Name is { } n ? $"{s.Screen} · {n}" : _loc.Format("Screen_Default", s.Screen))}: {s.Plays}"));
    }

    private string FormatDuration(TimeSpan time) =>
        time.TotalHours >= 1 ? _loc.Format("Stats_Hours", (int)time.TotalHours, time.Minutes)
        : time.TotalMinutes >= 1 ? _loc.Format("Stats_Minutes", (int)time.TotalMinutes)
        : _loc.Format("Stats_Seconds", (int)time.TotalSeconds);

    [RelayCommand]
    private void ExportStats()
    {
        var path = _dialogs.PickSaveCsv($"kodiz-signage-istatistik-{DateTime.Now:yyyy-MM-dd}.csv");
        if (path is null)
        {
            return;
        }

        try
        {
            var headers = new[] { "Stats_ColDate", "Stats_ColScreen", "Stats_ColMedia", "Stats_ColPlays", "Stats_ColSeconds", "Stats_ColTime" }.Select(_loc.Get).ToList();
            string ScreenName(int n) => _settings.Current.GetScreen(n)?.Name is { } name ? $"{n} · {name}" : _loc.Format("Screen_Default", n);
            // UTF-8 with BOM so Excel shows Turkish characters correctly.
            File.WriteAllText(path, _stats.ExportCsv(headers, ScreenName), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _dialogs.Info(_loc.Format("Stats_Exported", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Statistics export failed");
            _dialogs.Warning(_loc.Format("Stats_ExportFailed", ex.Message));
        }
    }

    [RelayCommand]
    private void ResetStats()
    {
        if (_pin.Unlock() && _dialogs.Confirm(_loc.Get("Stats_ResetConfirm")))
        {
            _stats.Reset();
            RefreshStats();
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

    /// <summary>Zip with logs, settings and playlist (secrets removed) and a system summary, for support.</summary>
    [RelayCommand]
    private async Task CreateDiagnosticsAsync()
    {
        var path = _dialogs.PickSaveDiagnostics($"kodiz-signage-tani-{DateTime.Now:yyyy-MM-dd_HHmm}.zip");
        if (path is null)
        {
            return;
        }

        try
        {
            await _playlist.FlushAsync();
            await DiagnosticsPackage.CreateAsync(path, _paths, BuildSystemInfo(), TimeSpan.FromDays(7), CancellationToken.None);
            _dialogs.Info(_loc.Format("Diagnostics_Created", path));
            OpenFolder(System.IO.Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Diagnostics package failed");
            _dialogs.Warning(_loc.Format("Diagnostics_Failed", ex.Message));
        }
    }

    private string BuildSystemInfo()
    {
        var s = _settings.Current;
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Kodiz Signage {InstallService.CurrentVersion}");
        text.AppendLine($"Created: {DateTime.Now:O}");
        text.AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}), process {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        text.AppendLine($".NET: {Environment.Version}; culture {System.Globalization.CultureInfo.CurrentCulture.Name} / UI {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        text.AppendLine($"Uptime: {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm}; memory {GC.GetTotalMemory(false) / (1024 * 1024)} MB managed, {Environment.WorkingSet / (1024 * 1024)} MB working set");
        text.AppendLine($"Data folder: {_paths.Root}");
        text.AppendLine($"Library: {_playlist.Items.Count} items; playing: {_playback.IsRunning}");
        foreach (var screen in s.Screens)
        {
            var state = _playback.Screens.FirstOrDefault(x => x.Number == screen.Number);
            text.AppendLine($"Screen {screen.Number}: enabled={screen.Enabled} rotation={screen.Rotation} status={state?.Status} display={state?.Display?.DeviceName} {state?.Display?.Width}x{state?.Display?.Height} now={state?.NowPlaying?.Item.OriginalName}");
        }

        text.AppendLine($"Theme={s.Theme} language={s.Language} transition={s.Transition} hours={s.OperatingHours.Enabled} webPanel={s.WebPanelEnabled} music={s.Music.Enabled} autoBackup={s.AutoBackup.Enabled}");
        return text.ToString();
    }

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

/// <summary>One bar of the statistics chart (Size = width for media bars, height for day columns).</summary>
public sealed record StatBar(string Label, string Value, double Size, string Detail);
