namespace KodizSignage.Core.Playback;

/// <summary>
/// Order of the background music: alphabetical, or shuffled so that every song plays once per
/// round and the last song of a round never starts the next one.
/// </summary>
public sealed class MusicQueue
{
    private readonly Random _random;
    private readonly Queue<string> _round = new();
    private List<string> _songs = new();
    private string? _last;

    public MusicQueue(Random? random = null)
    {
        _random = random ?? new Random();
    }

    public bool Shuffle { get; set; } = true;

    public int Count => _songs.Count;

    /// <summary>Replaces the song list (e.g. after rescanning the folder); keeps the current position when possible.</summary>
    public void SetSongs(IEnumerable<string> songs)
    {
        var list = songs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (list.SequenceEqual(_songs, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _songs = list;
        var keep = _round.Where(s => list.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        _round.Clear();
        foreach (var song in keep)
        {
            _round.Enqueue(song);
        }
    }

    public string? Next()
    {
        if (_songs.Count == 0)
        {
            return null;
        }

        if (_round.Count == 0)
        {
            var order = Shuffle ? _songs.OrderBy(_ => _random.Next()).ToList() : StartAfter(_last);
            if (Shuffle && order.Count > 1 && string.Equals(order[0], _last, StringComparison.OrdinalIgnoreCase))
            {
                (order[0], order[^1]) = (order[^1], order[0]);
            }

            foreach (var song in order)
            {
                _round.Enqueue(song);
            }
        }

        _last = _round.Dequeue();
        return _last;
    }

    /// <summary>Alphabetical order continuing after <paramref name="last"/>.</summary>
    private List<string> StartAfter(string? last)
    {
        var index = last is null ? -1 : _songs.FindIndex(s => string.Equals(s, last, StringComparison.OrdinalIgnoreCase));
        return _songs.Skip(index + 1).Concat(_songs.Take(index + 1)).ToList();
    }
}
