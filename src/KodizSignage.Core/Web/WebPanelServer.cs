using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace KodizSignage.Core.Web;

public sealed record WebScreen(int Number, string Name, bool Enabled, string Status, string? NowPlaying,
    Guid? NowPlayingId = null, string? ActiveList = null, string Ticker = "", bool TickerOn = false);

public sealed record WebAlert(string Title, string Message, bool Urgent, DateTime? Until);

public sealed record WebMusic(bool Enabled, bool Playing, string? Song);

public sealed record WebStatus(bool Running, string Language, string Version, IReadOnlyList<WebScreen> Screens, int MediaCount,
    WebAlert? Alert = null, WebMusic? Music = null);

public sealed record WebMedia(Guid Id, string Title, string Type, bool Active, bool PlayableNow, bool HasWarning, IReadOnlyList<int> Screens);

public sealed record WebUploadResult(bool Ok, string Message);

/// <summary>What the phone panel can see and do; implemented by the app (calls are marshalled to the UI thread there).</summary>
public interface IWebPanelBackend
{
    bool VerifyPin(string pin);
    Task<WebStatus> GetStatusAsync();
    Task<IReadOnlyList<WebMedia>> GetLibraryAsync();
    Task<byte[]?> GetThumbnailAsync(Guid id);
    Task ToggleAsync();
    Task NextAsync(int? screen);
    Task SetScreenEnabledAsync(int screen, bool enabled);
    Task SetActiveAsync(Guid id, bool active);
    Task SetOnScreenAsync(Guid id, int screen, bool on);
    Task DeleteAsync(Guid id);

    Task ShowAlertAsync(string title, string message, bool urgent, int minutes);
    Task ClearAlertAsync();
    Task SetTickerAsync(int screen, string text, bool on);

    /// <summary>Creates an announcement slide; returns its title.</summary>
    Task<string> CreateSlideAsync(string title, string body, int theme);

    Task SetMusicAsync(bool enabled);
    Task NextSongAsync();

    /// <summary>Folder for incoming uploads (emptied by the app after import).</summary>
    string UploadFolder { get; }

    Task<WebUploadResult> ImportAsync(string path);
}

/// <summary>
/// Minimal HTTP/1.1 server for the phone panel on the local network. One request per connection,
/// PIN login with session cookie, lockout after failed attempts, custom-header CSRF protection
/// and a Host check against DNS rebinding.
/// </summary>
public sealed class WebPanelServer : IDisposable
{
    public const string SessionCookie = "ks_session";
    public const string CsrfHeader = "x-kodiz";
    public const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxSmallBody = 64 * 1024;
    private const int MaxFailures = 5;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IWebPanelBackend _backend;
    private readonly ILogger _log;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime LockedUntil)> _failures = new();
    private readonly SemaphoreSlim _connections = new(16, 16);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public WebPanelServer(IWebPanelBackend backend, ILogger log, Func<DateTime>? now = null)
    {
        _backend = backend;
        _log = log.ForContext<WebPanelServer>();
        _now = now ?? (() => DateTime.UtcNow);
    }

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>Starts listening on all interfaces; port 0 picks a free port (tests).</summary>
    public void Start(int port)
    {
        Stop();
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(listener, _cts.Token);
        _log.Information("Web panel listening on port {Port}", Port);
    }

    public void Stop()
    {
        if (_listener is null)
        {
            return;
        }

        _cts?.Cancel();
        _listener.Stop();
        _listener = null;
        _sessions.Clear();
        _log.Information("Web panel stopped");
    }

    /// <summary>Signs every phone out (e.g. after the PIN changed).</summary>
    public void ClearSessions() => _sessions.Clear();

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Web panel accept failed");
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (!await _connections.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                client.Dispose(); // Too many at once.
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using (client)
                    {
                        await HandleConnectionAsync(client, stop).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // Phone went away, timeout, shutdown.
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Web panel request failed");
                }
                finally
                {
                    _connections.Release();
                }
            }, CancellationToken.None);
        }
    }

    // ---- HTTP ------------------------------------------------------------------------------

    private sealed class Request
    {
        public string Method { get; init; } = "GET";
        public string Path { get; init; } = "/";
        public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string? UploadPath { get; set; }
        public string ClientIp { get; init; } = string.Empty;

        public string? Cookie(string name)
        {
            if (!Headers.TryGetValue("cookie", out var header))
            {
                return null;
            }

            foreach (var part in header.Split(';'))
            {
                var kv = part.Trim().Split('=', 2);
                if (kv.Length == 2 && kv[0] == name)
                {
                    return kv[1];
                }
            }

            return null;
        }
    }

    private sealed record Response(int Status, string ContentType, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null);

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken stop)
    {
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        var stream = client.GetStream();
        var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";

        var (head, leftover) = await ReadHeadAsync(stream, headerTimeout.Token).ConfigureAwait(false);
        if (head is null)
        {
            return;
        }

        var request = ParseHead(head, ip);
        if (request is null)
        {
            await WriteAsync(stream, Text(400, "Bad request"), stop).ConfigureAwait(false);
            return;
        }

        var length = request.Headers.TryGetValue("content-length", out var cl) && long.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        Response response;
        if (!IsAllowedHost(request))
        {
            response = Text(403, "Forbidden");
        }
        else if (request.Path == "/api/upload" && request.Method == "POST")
        {
            if (!IsAuthorized(request) || !HasCsrfHeader(request))
            {
                response = Text(401, "Unauthorized");
            }
            else if (length <= 0 || length > MaxUploadBytes)
            {
                response = Text(413, "Too large");
            }
            else
            {
                using var uploadTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                uploadTimeout.CancelAfter(TimeSpan.FromHours(1));
                request.UploadPath = await ReceiveUploadAsync(stream, leftover, length, request, uploadTimeout.Token).ConfigureAwait(false);
                response = await RouteAsync(request).ConfigureAwait(false);
            }
        }
        else if (length > MaxSmallBody)
        {
            response = Text(413, "Too large");
        }
        else
        {
            request.Body = await ReadBodyAsync(stream, leftover, (int)length, headerTimeout.Token).ConfigureAwait(false);
            response = await RouteAsync(request).ConfigureAwait(false);
        }

        await WriteAsync(stream, response, stop).ConfigureAwait(false);
    }

    private static async Task<(string? Head, byte[] Leftover)> ReadHeadAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHeaderBytes];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), token).ConfigureAwait(false);
            if (read == 0)
            {
                return (null, Array.Empty<byte>());
            }

            filled += read;
            var end = IndexOfHeaderEnd(buffer, filled);
            if (end >= 0)
            {
                return (Encoding.ASCII.GetString(buffer, 0, end), buffer[(end + 4)..filled]);
            }
        }

        return (null, Array.Empty<byte>());
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i - 3;
            }
        }

        return -1;
    }

    private static Request? ParseHead(string head, string ip)
    {
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return null;
        }

        var target = first[1];
        var q = target.IndexOf('?');
        var path = Uri.UnescapeDataString(q >= 0 ? target[..q] : target);
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q >= 0)
        {
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                query[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : string.Empty;
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return new Request { Method = first[0].ToUpperInvariant(), Path = path, Query = query, Headers = headers, ClientIp = ip };
    }

    private static async Task<byte[]> ReadBodyAsync(Stream stream, byte[] leftover, int length, CancellationToken token)
    {
        var body = new byte[length];
        var filled = Math.Min(leftover.Length, length);
        Array.Copy(leftover, body, filled);
        while (filled < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(filled), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Body ended early");
            }

            filled += read;
        }

        return body;
    }

    private async Task<string> ReceiveUploadAsync(Stream stream, byte[] leftover, long length, Request request, CancellationToken token)
    {
        var name = SafeFileName(request.Query.GetValueOrDefault("name"));
        var folder = Path.Combine(_backend.UploadFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        await using (var file = File.Create(path))
        {
            var first = (int)Math.Min(leftover.Length, length);
            await file.WriteAsync(leftover.AsMemory(0, first), token).ConfigureAwait(false);
            var remaining = length - first;
            var buffer = new byte[1 << 20];
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("Upload ended early");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
            }
        }

        return path;
    }

    /// <summary>Keeps the original name (it becomes the media title) but nothing that could leave the folder.</summary>
    public static string SafeFileName(string? name)
    {
        var file = Path.GetFileName((name ?? string.Empty).Replace('\\', '/').Split('/').Last());
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '|', '?', '*' }).ToHashSet();
        file = new string(file.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimStart('.');
        if (file.Length > 120)
        {
            var ext = Path.GetExtension(file);
            file = file[..(120 - ext.Length)] + ext;
        }

        return string.IsNullOrWhiteSpace(file) ? "upload.bin" : file;
    }

    private static async Task WriteAsync(Stream stream, Response response, CancellationToken token)
    {
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Type: {response.ContentType}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {response.Body.Length}\r\n")
            .Append("Connection: close\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("X-Frame-Options: DENY\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("Content-Security-Policy: default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'\r\n");
        foreach (var (key, value) in response.Headers ?? new Dictionary<string, string>())
        {
            head.Append(key).Append(": ").Append(value).Append("\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token).ConfigureAwait(false);
        await stream.WriteAsync(response.Body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        _ => "Error",
    };

    private static Response Text(int status, string text) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static Response JsonResponse(object value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Json), headers);

    private static readonly Response Ok = new(200, "application/json; charset=utf-8", "{\"ok\":true}"u8.ToArray());

    // ---- Security --------------------------------------------------------------------------

    /// <summary>Only IP literals, localhost and this PC's name: a rebinding attacker's domain is refused.</summary>
    private static bool IsAllowedHost(Request request)
    {
        if (!request.Headers.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var close = host.IndexOf(']');
        var name = host.StartsWith('[') ? (close > 1 ? host[1..close] : string.Empty) : host.Split(':')[0];
        return IPAddress.TryParse(name, out _) ||
               name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(Environment.MachineName + ".local", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCsrfHeader(Request request) => request.Headers.TryGetValue(CsrfHeader, out var v) && v == "1";

    private bool IsAuthorized(Request request)
    {
        var token = request.Cookie(SessionCookie);
        if (token is null || !_sessions.TryGetValue(token, out var expires))
        {
            return false;
        }

        if (expires < _now())
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        return true;
    }

    private Response Login(Request request)
    {
        var now = _now();
        if (_failures.TryGetValue(request.ClientIp, out var failure) && failure.LockedUntil > now)
        {
            var wait = (int)Math.Ceiling((failure.LockedUntil - now).TotalSeconds);
            return new Response(429, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { error = "locked", wait }, Json));
        }

        string? pin = null;
        try
        {
            pin = JsonDocument.Parse(request.Body).RootElement.GetProperty("pin").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        if (pin is null || !_backend.VerifyPin(pin))
        {
            var count = failure.Count + 1;
            // 5 wrong PINs: 1 minute, then doubling up to an hour.
            var locked = count >= MaxFailures ? now + TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, count - MaxFailures))) : DateTime.MinValue;
            _failures[request.ClientIp] = (count, locked);
            _log.Warning("Web panel: wrong PIN from {Ip} ({Count})", request.ClientIp, count);
            return new Response(401, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { error = "pin" }, Json));
        }

        _failures.TryRemove(request.ClientIp, out _);
        foreach (var expired in _sessions.Where(s => s.Value < now).Select(s => s.Key).ToList())
        {
            _sessions.TryRemove(expired, out _);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = now + SessionLifetime;
        _log.Information("Web panel: signed in from {Ip}", request.ClientIp);
        return JsonResponse(new { ok = true }, new Dictionary<string, string>
        {
            ["Set-Cookie"] = $"{SessionCookie}={token}; Path=/; HttpOnly; SameSite=Strict; Max-Age={(int)SessionLifetime.TotalSeconds}",
        });
    }

    // ---- Routes ----------------------------------------------------------------------------

    private async Task<Response> RouteAsync(Request request)
    {
        try
        {
            switch (request.Method, request.Path)
            {
                case ("GET", "/"):
                case ("GET", "/index.html"):
                    return new Response(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(WebPanelPage.Html));
                case ("GET", "/favicon.ico"):
                    return new Response(204, "image/x-icon", Array.Empty<byte>());
                case ("POST", "/api/login"):
                    return HasCsrfHeader(request) ? Login(request) : Text(403, "Forbidden");
            }

            if (!request.Path.StartsWith("/api/", StringComparison.Ordinal))
            {
                return Text(404, "Not found");
            }

            if (!IsAuthorized(request))
            {
                return Text(401, "Unauthorized");
            }

            if (request.Method == "POST" && !HasCsrfHeader(request))
            {
                return Text(403, "Forbidden");
            }

            switch (request.Method, request.Path)
            {
                case ("GET", "/api/status"):
                    return JsonResponse(await _backend.GetStatusAsync().ConfigureAwait(false));
                case ("GET", "/api/library"):
                    return JsonResponse(await _backend.GetLibraryAsync().ConfigureAwait(false));
                case ("GET", "/api/thumb"):
                    return Guid.TryParse(request.Query.GetValueOrDefault("id"), out var thumbId) &&
                           await _backend.GetThumbnailAsync(thumbId).ConfigureAwait(false) is { } jpg
                        ? new Response(200, "image/jpeg", jpg, new Dictionary<string, string> { ["Cache-Control"] = "private, max-age=300" })
                        : Text(404, "Not found");
                case ("POST", "/api/logout"):
                    if (request.Cookie(SessionCookie) is { } token)
                    {
                        _sessions.TryRemove(token, out _);
                    }

                    return JsonResponse(new { ok = true }, new Dictionary<string, string> { ["Set-Cookie"] = $"{SessionCookie}=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0" });
                case ("POST", "/api/toggle"):
                    await _backend.ToggleAsync().ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/next"):
                    await _backend.NextAsync(int.TryParse(request.Query.GetValueOrDefault("screen"), out var s) ? s : null).ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/screen") when Int("n", request) is { } screen && Bool("on", request) is { } on:
                    await _backend.SetScreenEnabledAsync(screen, on).ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/media/active") when Id(request) is { } id && Bool("on", request) is { } active:
                    await _backend.SetActiveAsync(id, active).ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/media/screen") when Id(request) is { } id && Int("n", request) is { } n && Bool("on", request) is { } onScreen:
                    await _backend.SetOnScreenAsync(id, n, onScreen).ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/media/delete") when Id(request) is { } id:
                    await _backend.DeleteAsync(id).ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/alert") when JsonBody(request) is { } alert:
                    await _backend.ShowAlertAsync(Str(alert, "title"), Str(alert, "message"), Flag(alert, "urgent"), Num(alert, "minutes")).ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/alert/clear"):
                    await _backend.ClearAlertAsync().ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/ticker") when Int("n", request) is { } tickerScreen && JsonBody(request) is { } ticker:
                    await _backend.SetTickerAsync(tickerScreen, Str(ticker, "text"), Flag(ticker, "on")).ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/slide") when JsonBody(request) is { } slide:
                    var created = await _backend.CreateSlideAsync(Str(slide, "title"), Str(slide, "body"), Num(slide, "theme")).ConfigureAwait(false);
                    return JsonResponse(new WebUploadResult(true, created));
                case ("POST", "/api/music") when Bool("on", request) is { } musicOn:
                    await _backend.SetMusicAsync(musicOn).ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/music/next"):
                    await _backend.NextSongAsync().ConfigureAwait(false);
                    return Ok;
                case ("POST", "/api/upload") when request.UploadPath is { } upload:
                    return JsonResponse(await _backend.ImportAsync(upload).ConfigureAwait(false));
                default:
                    return Text(404, "Not found");
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Web panel: {Method} {Path} failed", request.Method, request.Path);
            return new Response(500, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { error = "server" }, Json));
        }
    }

    /// <summary>The JSON object body, or null when missing / not an object.</summary>
    private static JsonElement? JsonBody(Request request)
    {
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement? json, string name, int max = 300)
    {
        var value = json is { } j && j.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
        return value.Length > max ? value[..max] : value;
    }

    private static bool Flag(JsonElement? json, string name) =>
        json is { } j && j.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static int Num(JsonElement? json, string name) =>
        json is { } j && j.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : 0;

    private static Guid? Id(Request request) => Guid.TryParse(request.Query.GetValueOrDefault("id"), out var id) ? id : null;

    private static int? Int(string key, Request request) =>
        int.TryParse(request.Query.GetValueOrDefault(key), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool? Bool(string key, Request request) => request.Query.GetValueOrDefault(key) switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null,
    };
}
