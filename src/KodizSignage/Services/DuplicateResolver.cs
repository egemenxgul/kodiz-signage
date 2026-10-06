using System.IO;
using System.Windows;
using KodizSignage.Core.Services;
using KodizSignage.Views;

namespace KodizSignage.Services;

public interface IDuplicateResolver
{
    /// <summary>A handler for one import run ("apply to the rest" is remembered per run).</summary>
    Func<DuplicateQuestion, Task<DuplicateAnswer>> CreateHandler();
}

/// <summary>Asks the user (with both files side by side) what to do with a duplicate.</summary>
public sealed class DuplicateResolver : IDuplicateResolver
{
    private readonly IPlaylistService _playlist;
    private readonly IThumbnailService _thumbnails;
    private readonly ILocalizationService _loc;

    public DuplicateResolver(IPlaylistService playlist, IThumbnailService thumbnails, ILocalizationService loc)
    {
        _playlist = playlist;
        _thumbnails = thumbnails;
        _loc = loc;
    }

    public Func<DuplicateQuestion, Task<DuplicateAnswer>> CreateHandler()
    {
        var remembered = new Dictionary<DuplicateKind, DuplicateAnswer>();
        return async question =>
        {
            if (remembered.TryGetValue(question.Kind, out var answer))
            {
                return answer;
            }

            var (chosen, applyToAll) = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var usedOn = _playlist.ScreenPlaylists
                    .Where(p => p.Entries.Any(e => e.MediaId == question.Existing.Id))
                    .Select(p => p.OwnerScreen)
                    .Distinct()
                    .ToList();
                return DuplicateWindow.Ask(question, usedOn, _thumbnails, _loc);
            }).Task.Unwrap();

            if (applyToAll)
            {
                remembered[question.Kind] = chosen;
            }

            return chosen;
        };
    }
}
