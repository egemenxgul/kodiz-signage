using System.IO.Compression;
using KodizSignage.Core.Services;

namespace KodizSignage.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void Secrets_are_hidden_anywhere_in_the_json()
    {
        var json = """
        { "pinHash": "abc:def", "webPanelPinHash": null, "language": "Turkish",
          "items": [ { "slide": { "wifiSsid": "Kafe", "wifiPassword": "gizli123" } } ] }
        """;

        var redacted = DiagnosticsPackage.Redact(json);

        Assert.DoesNotContain("abc:def", redacted);
        Assert.DoesNotContain("gizli123", redacted);
        Assert.Contains("Kafe", redacted);
        Assert.Contains("Turkish", redacted);
        Assert.Contains(DiagnosticsPackage.Hidden, redacted);
    }

    [Fact]
    public void Text_that_is_not_json_is_left_alone() =>
        Assert.Equal("not json {", DiagnosticsPackage.Redact("not json {"));

    [Fact]
    public async Task Package_contains_system_info_redacted_settings_and_recent_logs()
    {
        using var dir = new TempDir();
        dir.Paths.EnsureCreated();
        await File.WriteAllTextAsync(dir.Paths.SettingsFile, """{ "pinHash": "secret" }""");
        await File.WriteAllTextAsync(Path.Combine(dir.Paths.LogFolder, "kodiz-20261006.log"), "started");
        var zipPath = Path.Combine(dir.Root, "diag.zip");

        await DiagnosticsPackage.CreateAsync(zipPath, dir.Paths, "Windows 11", TimeSpan.FromDays(7), CancellationToken.None);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(new[] { "logs/kodiz-20261006.log", "settings.json", "system.txt" }, zip.Entries.Select(e => e.FullName).Order());
        using var reader = new StreamReader(zip.GetEntry("settings.json")!.Open());
        Assert.DoesNotContain("secret", await reader.ReadToEndAsync());
    }
}
