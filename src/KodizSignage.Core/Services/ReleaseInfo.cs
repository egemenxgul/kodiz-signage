using System.Text.Json;

namespace KodizSignage.Core.Services;

/// <summary>A published version on GitHub Releases.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string ExeUrl, string? HashUrl, string PageUrl, string Notes)
{
    public const string ExeAssetName = "KodizSignage.exe";
    public const string HashAssetName = "KodizSignage.exe.sha256";

    /// <summary>
    /// Reads the GitHub "latest release" JSON. Returns null for drafts/pre-releases, unparseable tags
    /// or releases without the exe asset.
    /// </summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
                root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            {
                return null;
            }

            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!TryParseVersion(tag, out var version))
            {
                return null;
            }

            string? exe = null, hash = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (string.Equals(name, ExeAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        exe = url;
                    }
                    else if (string.Equals(name, HashAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        hash = url;
                    }
                }
            }

            if (exe is null)
            {
                return null;
            }

            var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? string.Empty : string.Empty;
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
            return new ReleaseInfo(version, tag, exe, hash, page, notes);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"v1.3.0" / "1.3" / "1.3.0.0" → 1.3.0.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        var trimmed = text?.Trim().TrimStart('v', 'V') ?? string.Empty;
        var dash = trimmed.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0)
        {
            trimmed = trimmed[..dash];
        }

        if (!Version.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    /// <summary>Reads "&lt;sha256 hex&gt;  file" (sha256sum format) or a bare hash.</summary>
    public static string? ParseHash(string text)
    {
        var first = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: 64 } && first.All(Uri.IsHexDigit) ? first.ToUpperInvariant() : null;
    }

    /// <summary>True when this release should be offered to a client running <paramref name="current"/>.</summary>
    public bool IsNewerThan(Version current, string? skippedVersion) =>
        Version > current && !(TryParseVersion(skippedVersion, out var skipped) && skipped == Version);
}
