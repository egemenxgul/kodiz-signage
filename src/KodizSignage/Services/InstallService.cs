using System.Diagnostics;
using System.IO;
using System.Windows;
using KodizSignage.Core;
using KodizSignage.Native;
using KodizSignage.ViewModels;
using Serilog;

namespace KodizSignage.Services;

public enum InstallState
{
    /// <summary>This process is the installed copy.</summary>
    RunningInstalled,
    NotInstalled,
    /// <summary>An older version is installed; this exe can update it.</summary>
    InstalledOlder,
    /// <summary>The same or a newer version is installed.</summary>
    InstalledSameOrNewer,
}

/// <summary>
/// Keeps the app in a stable place (%LocalAppData%\kodiz-signage\app) so the autostart entry does
/// not break when the user cleans the Downloads folder, adds a Start-menu shortcut, and lets a
/// newer exe replace the installed one (simple update path).
/// </summary>
public sealed class InstallService
{
    public const string InstalledArgument = "--installed";

    /// <summary>Started by the updater: install without asking, then start with <see cref="AfterUpdateArgument"/>.</summary>
    public const string SilentUpdateArgument = "--update-silent";

    public const string AfterUpdateArgument = "--after-update";

    /// <summary>A new version that crashes repeatedly within this time after an update is rolled back.</summary>
    public static readonly TimeSpan RollbackWindow = TimeSpan.FromMinutes(30);
    private const string ExeName = "KodizSignage.exe";

    private readonly ILogger _log;

    public InstallService(AppPaths paths, ILogger log)
    {
        _log = log.ForContext<InstallService>();
        InstallFolder = Path.Combine(paths.Root, "app");
    }

    public string InstallFolder { get; }

    public string InstalledExe => Path.Combine(InstallFolder, ExeName);

    private string VersionFile => Path.Combine(InstallFolder, "version.txt");

    private string IconFile => Path.Combine(InstallFolder, "app.ico");

    public string PreviousExe => Path.Combine(InstallFolder, "KodizSignage.previous.exe");

    private string PreviousVersionFile => Path.Combine(InstallFolder, "previous-version.txt");

    private string UpdateMarker => Path.Combine(InstallFolder, "update-pending.txt");

    private string RollbackFolder => Path.Combine(InstallFolder, "rollback");

    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Kodiz Signage.lnk");

    public static string CurrentExe => Environment.ProcessPath ?? string.Empty;

    public static Version CurrentVersion => Version.TryParse(GeneralViewModel.Version, out var v) ? v : new Version(1, 0, 0);

    public bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public Version? InstalledVersion
    {
        get
        {
            try
            {
                return File.Exists(InstalledExe) && Version.TryParse(File.ReadAllText(VersionFile).Trim(), out var v) ? v : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public InstallState GetState()
    {
        if (IsRunningInstalled)
        {
            return InstallState.RunningInstalled;
        }

        if (!File.Exists(InstalledExe))
        {
            return InstallState.NotInstalled;
        }

        if (InstalledVersion is not { } installed || installed < CurrentVersion)
        {
            return InstallState.InstalledOlder;
        }

        // Same version number but a different build (e.g. a test build): offer to update too.
        return installed == CurrentVersion && !IsSameFile(CurrentExe, InstalledExe)
            ? InstallState.InstalledOlder
            : InstallState.InstalledSameOrNewer;
    }

    private static bool IsSameFile(string a, string b)
    {
        try
        {
            var (fa, fb) = (new FileInfo(a), new FileInfo(b));
            return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies this exe into the install folder (overwriting an older copy). The installed app must not
    /// be running. With <paramref name="keepPrevious"/> the replaced exe is kept for a rollback.
    /// </summary>
    public bool Install(bool keepPrevious = false)
    {
        try
        {
            Directory.CreateDirectory(InstallFolder);
            if (keepPrevious && File.Exists(InstalledExe))
            {
                File.Copy(InstalledExe, PreviousExe, overwrite: true);
                File.WriteAllText(PreviousVersionFile, InstalledVersion?.ToString(3) ?? string.Empty);
            }

            var temp = InstalledExe + ".new";
            File.Copy(CurrentExe, temp, overwrite: true);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, InstalledExe, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(500); // The old instance may still be exiting.
                }
            }

            File.WriteAllText(VersionFile, CurrentVersion.ToString(3));
            WriteIcon();
            WriteNotices();
            _log.Information("Installed {Version} to {Path}", CurrentVersion, InstalledExe);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Installation failed");
            return false;
        }
    }

    /// <summary>Creates or refreshes the Start-menu shortcut to the running exe.</summary>
    public void EnsureShortcut()
    {
        try
        {
            WriteIcon();
            ShellLink.Create(ShortcutPath, CurrentExe, null, File.Exists(IconFile) ? IconFile : CurrentExe, "Kodiz Signage");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not create the Start menu shortcut");
        }
    }

    public void LaunchInstalled(string arguments)
    {
        Process.Start(new ProcessStartInfo(InstalledExe, arguments) { UseShellExecute = false, WorkingDirectory = InstallFolder });
    }

    // ---- Update bookkeeping / rollback ---------------------------------------------------------

    /// <summary>Called by the new version on its first start after an update.</summary>
    public void MarkUpdated()
    {
        try
        {
            File.WriteAllText(UpdateMarker, DateTime.UtcNow.ToString("O"));
            _log.Information("Updated to {Version} (previous {Previous})", CurrentVersion, PreviousVersionText);
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "Could not write update marker");
        }
    }

    public string PreviousVersionText
    {
        get
        {
            try
            {
                return File.Exists(PreviousVersionFile) ? File.ReadAllText(PreviousVersionFile).Trim() : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>True while a fresh update is on probation (a crash loop then means: roll back).</summary>
    public bool IsRecentlyUpdated
    {
        get
        {
            try
            {
                return File.Exists(UpdateMarker) && File.Exists(PreviousExe) &&
                       DateTime.TryParse(File.ReadAllText(UpdateMarker), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) &&
                       DateTime.UtcNow - at < RollbackWindow;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>The new version ran stably: forget the probation marker and the data snapshot.</summary>
    public void ConfirmUpdate()
    {
        try
        {
            File.Delete(UpdateMarker);
            if (Directory.Exists(RollbackFolder))
            {
                Directory.Delete(RollbackFolder, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _log.Debug(ex, "Could not clean up update files");
        }
    }

    /// <summary>
    /// Restores the previous exe and the data as it was before the update, then starts it. Runs via
    /// cmd.exe after this (crashing) process has exited, since a running exe cannot be overwritten.
    /// The failed version is remembered so it is not offered again.
    /// </summary>
    public void RollBackAndExit(string dataRoot)
    {
        _log.Warning("Rolling back from {Version} to {Previous}", CurrentVersion, PreviousVersionText);
        try
        {
            File.WriteAllText(Path.Combine(InstallFolder, "skipped-update.txt"), CurrentVersion.ToString(3));
            File.Delete(UpdateMarker);
        }
        catch (IOException)
        {
        }

        static string Q(string path) => "\"" + path + "\"";
        var commands = new List<string>
        {
            "ping 127.0.0.1 -n 4 >nul",
            $"copy /y {Q(PreviousExe)} {Q(InstalledExe)}",
        };
        foreach (var file in new[] { "settings.json", "playlist.json" })
        {
            var backup = Path.Combine(RollbackFolder, file);
            if (File.Exists(backup))
            {
                commands.Add($"copy /y {Q(backup)} {Q(Path.Combine(dataRoot, file))}");
            }
        }

        commands.Add($"start \"\" {Q(InstalledExe)} {InstalledArgument}");
        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", commands))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>License and third-party notices next to the installed exe (they must travel with every copy).</summary>
    private void WriteNotices()
    {
        foreach (var (resource, file) in new[] { ("LICENSE.txt", "LICENSE.txt"), ("THIRD-PARTY-NOTICES.txt", "THIRD-PARTY-NOTICES.txt") })
        {
            try
            {
                var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{resource}"))?.Stream;
                if (stream is null)
                {
                    continue;
                }

                using (stream)
                using (var output = File.Create(Path.Combine(InstallFolder, file)))
                {
                    stream.CopyTo(output);
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Could not write {File}", file);
            }
        }
    }

    private void WriteIcon()
    {
        try
        {
            if (File.Exists(IconFile))
            {
                return;
            }

            Directory.CreateDirectory(InstallFolder);
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is null)
            {
                return;
            }

            using var input = resource.Stream;
            using var output = File.Create(IconFile);
            input.CopyTo(output);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not write icon file");
        }
    }
}
