namespace KodizSignage.Core;

/// <summary>Locations of all persisted data.</summary>
public sealed class AppPaths
{
    public const string AppFolderName = "kodiz-signage";

    public AppPaths(string root)
    {
        Root = root;
    }

    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName));

    public string Root { get; }
    public string MediaFolder => Path.Combine(Root, "media");
    public string ThumbnailFolder => Path.Combine(MediaFolder, "thumbs");
    public string LogFolder => Path.Combine(Root, "logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string PlaylistFile => Path.Combine(Root, "playlist.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MediaFolder);
        Directory.CreateDirectory(ThumbnailFolder);
        Directory.CreateDirectory(LogFolder);
    }

    /// <summary>Resolves an item's stored path (relative to the media folder) to an absolute path.</summary>
    public string ResolveMediaPath(string storedPath) =>
        Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(MediaFolder, storedPath);

    public string GetThumbnailPath(Guid itemId) => Path.Combine(ThumbnailFolder, itemId.ToString("N") + ".jpg");
}
