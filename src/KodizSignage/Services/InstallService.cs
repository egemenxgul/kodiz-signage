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

    /// <summary>Copies this exe into the install folder (overwriting an older copy). The installed app must not be running.</summary>
    public bool Install()
    {
        try
        {
            Directory.CreateDirectory(InstallFolder);
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
