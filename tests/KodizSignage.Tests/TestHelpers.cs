using KodizSignage.Core;
using KodizSignage.Core.Models;
using Serilog;
using Serilog.Core;

namespace KodizSignage.Tests;

/// <summary>A throw-away data folder per test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "kodiz-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Root);
        Paths.EnsureCreated();
    }

    public string Root { get; }
    public AppPaths Paths { get; }

    public string File(string name, string content = "x")
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public static class TestLog
{
    public static ILogger None => Logger.None;
}

public static class Items
{
    public static PlaylistItem Image(int order, string name = "img", bool active = true, double? duration = null,
        DateTime? start = null, DateTime? end = null) => new()
    {
        OriginalName = $"{name}{order}.jpg",
        FilePath = $"{name}{order}.jpg",
        Type = MediaType.Image,
        Order = order,
        IsActive = active,
        DurationSeconds = duration,
        StartDate = start,
        EndDate = end,
    };

    public static PlaylistItem Video(int order, bool active = true) => new()
    {
        OriginalName = $"video{order}.mp4",
        FilePath = $"video{order}.mp4",
        Type = MediaType.Video,
        Order = order,
        IsActive = active,
    };
}
