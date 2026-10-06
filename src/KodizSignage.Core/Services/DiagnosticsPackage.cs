using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KodizSignage.Core.Services;

/// <summary>
/// One zip for support: recent logs, settings and playlist (secrets removed) and a system summary.
/// Media files are not included.
/// </summary>
public static class DiagnosticsPackage
{
    public const string Hidden = "(hidden)";

    /// <summary>JSON properties that never leave the PC.</summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "pinHash", "webPanelPinHash", "wifiPassword",
    };

    public static async Task CreateAsync(string zipPath, AppPaths paths, string systemInfo, TimeSpan logAge, CancellationToken cancellationToken)
    {
        var partial = zipPath + ".part";
        await using (var stream = File.Create(partial))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await AddTextAsync(zip, "system.txt", systemInfo, cancellationToken);
            foreach (var (file, name) in new[] { (paths.SettingsFile, "settings.json"), (paths.PlaylistFile, "playlist.json") })
            {
                if (File.Exists(file))
                {
                    await AddTextAsync(zip, name, Redact(await ReadSharedAsync(file, cancellationToken)), cancellationToken);
                }
            }

            if (Directory.Exists(paths.LogFolder))
            {
                var since = DateTime.Now - logAge;
                foreach (var log in Directory.EnumerateFiles(paths.LogFolder).Where(f => File.GetLastWriteTime(f) >= since))
                {
                    await AddTextAsync(zip, "logs/" + Path.GetFileName(log), await ReadSharedAsync(log, cancellationToken), cancellationToken);
                }
            }
        }

        File.Move(partial, zipPath, overwrite: true);
    }

    /// <summary>Replaces secret values; text that is not JSON is returned unchanged.</summary>
    public static string Redact(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (root is null)
        {
            return json;
        }

        RedactNode(root);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void RedactNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (SecretKeys.Contains(key))
                    {
                        if (value is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                        {
                            obj[key] = Hidden;
                        }
                    }
                    else if (value is not null)
                    {
                        RedactNode(value);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array.OfType<JsonNode>())
                {
                    RedactNode(item);
                }

                break;
        }
    }

    /// <summary>Reads a file another process may be writing (the current log).</summary>
    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string text, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }
}
