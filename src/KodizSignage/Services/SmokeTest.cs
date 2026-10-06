using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KodizSignage.Core.Services;
using KodizSignage.Views;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace KodizSignage.Services;

/// <summary>
/// "--smoke-test": exercises the real UI on Windows (used by CI): imports generated media, plays,
/// opens every settings tab and the preview, then exits with
/// 0 = OK, 1 = unhandled exception, 2 = data-binding errors, 3 = timeout.
/// </summary>
internal static class SmokeTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static async Task RunAsync(App app, IServiceProvider services, Func<SettingsWindow> showSettings, Action<int> exit)
    {
        var log = Log.ForContext(typeof(SmokeTest));
        var bindingErrors = new BindingErrorCounter(log);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

        var watchdog = Task.Delay(Timeout).ContinueWith(_ => app.Dispatcher.Invoke(() => exit(3)));

        try
        {
            log.Information("Smoke test started");
            var folder = Path.Combine(Path.GetTempPath(), "kodiz-smoke-media-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var files = new[]
            {
                CreatePng(Path.Combine(folder, "red.png"), Colors.IndianRed),
                CreatePng(Path.Combine(folder, "blue.png"), Colors.SteelBlue),
            };

            var import = services.GetRequiredService<IMediaImportService>();
            var result = await import.ImportAsync(files, null, CancellationToken.None);
            log.Information("Smoke: imported {Count}", result.Imported.Count);

            var settings = services.GetRequiredService<ISettingsService>();
            settings.Update(s => s with { DefaultImageDurationSeconds = 2 });

            var playback = services.GetRequiredService<IPlaybackManager>();
            playback.Start();
            await Task.Delay(3000);
            playback.Next();
            await Task.Delay(1500);

            var window = showSettings();
            var tabs = FindChild<TabControl>(window);
            if (tabs is not null)
            {
                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    await Task.Delay(800);
                }

                tabs.SelectedIndex = 0;
            }

            var media = services.GetRequiredService<ViewModels.MediaViewModel>();
            media.SelectedItem = media.Items.FirstOrDefault();
            await Task.Delay(800);

            playback.ShowPreview(null);
            await Task.Delay(2500);

            // Multi-screen: a second screen on a display that does not exist must wait without errors,
            // and the media list must filter / toggle per screen.
            settings.Update(s => s.WithScreen(new Core.Models.ScreenConfig
            {
                Number = 2,
                Name = "Smoke",
                Display = new Core.Models.SavedDisplay(@"\\.\DISPLAY99", 1920, 1080, 99999, 0),
            }));
            await Task.Delay(1500);
            var first = media.Items.FirstOrDefault();
            if (first is not null && first.ScreenChips.Count == 2)
            {
                first.ScreenChips[1].IsOn = false;
            }

            media.FilterToScreen(2);
            await Task.Delay(800);
            media.FilterToScreen(0);
            tabs?.SetCurrentValue(TabControl.SelectedIndexProperty, 1);
            await Task.Delay(800);

            settings.Update(s => s with { Language = s.Language == Core.Models.AppLanguage.English ? Core.Models.AppLanguage.Turkish : Core.Models.AppLanguage.English });
            services.GetRequiredService<ILocalizationService>().Apply(settings.Current.Language);
            await Task.Delay(800);

            await playback.StopAsync();
            await Task.Delay(500);

            var code = app.UnhandledCount > 0 ? 1 : bindingErrors.Count > 0 ? 2 : 0;
            log.Information("Smoke test finished: code {Code}, unhandled {Unhandled}, binding errors {Bindings}",
                code, app.UnhandledCount, bindingErrors.Count);
            exit(code);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Smoke test failed");
            exit(1);
        }

        await watchdog;
    }

    private static string CreatePng(string path, Color color)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 640, 360));
        }

        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private sealed class BindingErrorCounter : TraceListener
    {
        private readonly ILogger _log;

        public BindingErrorCounter(ILogger log) => _log = log;

        public int Count { get; private set; }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            Count++;
            _log.Error("Binding error: {Message}", message);
        }
    }
}
