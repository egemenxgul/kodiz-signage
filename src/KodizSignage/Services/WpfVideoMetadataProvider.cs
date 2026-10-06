using System.Windows;
using KodizSignage.Core.Services;

namespace KodizSignage.Services;

/// <summary>Fallback duration reader for non-MP4 containers (marshals to the UI thread).</summary>
public sealed class WpfVideoMetadataProvider : IVideoMetadataProvider
{
    public Task<TimeSpan?> GetDurationAsync(string path, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.InvokeAsync(() => MediaPlayerProbe.GetDurationAsync(path)).Task.Unwrap();
    }
}
