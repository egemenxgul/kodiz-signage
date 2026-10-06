using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;

namespace KodizSignage.Tests;

public class MusicTests
{
    private static readonly string[] Songs = { "c.mp3", "a.mp3", "b.mp3", "d.mp3" };

    [Fact]
    public void Alphabetical_order_wraps_around()
    {
        var queue = new MusicQueue { Shuffle = false };
        queue.SetSongs(Songs);
        var played = Enumerable.Range(0, 6).Select(_ => queue.Next()).ToList();
        Assert.Equal(new[] { "a.mp3", "b.mp3", "c.mp3", "d.mp3", "a.mp3", "b.mp3" }, played);
    }

    [Fact]
    public void Shuffle_plays_every_song_once_per_round_without_repeating_at_the_seam()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var queue = new MusicQueue(new Random(seed));
            queue.SetSongs(Songs);
            var first = Enumerable.Range(0, 4).Select(_ => queue.Next()!).ToList();
            var second = Enumerable.Range(0, 4).Select(_ => queue.Next()!).ToList();

            Assert.Equal(Songs.Order(), first.Order());
            Assert.Equal(Songs.Order(), second.Order());
            Assert.NotEqual(first[^1], second[0]);
        }
    }

    [Fact]
    public void Removed_songs_are_not_played_and_an_empty_folder_plays_nothing()
    {
        var queue = new MusicQueue { Shuffle = false };
        queue.SetSongs(Songs);
        queue.Next();
        queue.SetSongs(new[] { "a.mp3", "d.mp3" });
        Assert.All(Enumerable.Range(0, 4).Select(_ => queue.Next()), s => Assert.Contains(s, new[] { "a.mp3", "d.mp3" }));

        queue.SetSongs(Array.Empty<string>());
        Assert.Null(queue.Next());
    }

    [Fact]
    public void Settings_are_normalized_and_music_files_recognized()
    {
        var music = new MusicSettings { Volume = 5, Folder = "  ", DuringVideoSound = (MusicDuring)9 }.Normalize();
        Assert.Equal((1d, (string?)null, MusicDuring.Lower), (music.Volume, music.Folder, music.DuringVideoSound));
        Assert.True(MusicSettings.IsMusicFile(@"C:\Müzik\Şarkı.MP3"));
        Assert.False(MusicSettings.IsMusicFile("video.mp4"));
    }
}
