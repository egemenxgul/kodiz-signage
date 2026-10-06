using System.Net;
using System.Net.Http.Json;
using System.Text;
using KodizSignage.Core.Web;

namespace KodizSignage.Tests;

public sealed class WebPanelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend;
    private readonly WebPanelServer _server;

    public WebPanelTests()
    {
        _backend = new FakeBackend(Path.Combine(_dir.Root, "uploads"));
        _server = new WebPanelServer(_backend, TestLog.None);
        _server.Start(0);
    }

    public void Dispose()
    {
        _server.Dispose();
        _dir.Dispose();
    }

    private HttpClient Client(bool csrf = true)
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}"),
        };
        if (csrf)
        {
            client.DefaultRequestHeaders.Add("X-Kodiz", "1");
        }

        return client;
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string pin) =>
        client.PostAsync("/api/login", new StringContent($"{{\"pin\":\"{pin}\"}}", Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Page_is_served_without_login()
    {
        using var client = Client();
        var html = await client.GetStringAsync("/");
        Assert.Contains("Kodiz Signage", html);
    }

    [Fact]
    public async Task Api_requires_login()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/toggle", null)).StatusCode);
        Assert.Equal(0, _backend.Toggles);
    }

    [Fact]
    public async Task Login_then_status_and_actions_work()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.OK, (await Login(client, "4321")).StatusCode);

        var status = await client.GetFromJsonAsync<WebStatus>("/api/status");
        Assert.Equal(2, status!.Screens.Count);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/toggle", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/next?screen=2", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/screen?n=2&on=0", null)).StatusCode);
        Assert.Equal(1, _backend.Toggles);
        Assert.Equal(2, _backend.LastNext);
        Assert.Equal((2, false), _backend.LastScreen);

        await client.PostAsync("/api/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task Posts_without_the_custom_header_are_refused()
    {
        using var client = Client(csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Login(client, "4321")).StatusCode);
    }

    [Fact]
    public async Task Wrong_pins_lock_the_client_out()
    {
        using var client = Client();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "0000")).StatusCode);
        }

        // Even the right PIN is refused while locked.
        Assert.Equal((HttpStatusCode)429, (await Login(client, "4321")).StatusCode);
    }

    [Fact]
    public async Task Foreign_host_names_are_refused()
    {
        using var client = Client();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "evil.example.com";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Upload_is_stored_under_a_safe_name_and_imported()
    {
        using var client = Client();
        await Login(client, "4321");
        var data = new byte[300_000];
        new Random(1).NextBytes(data);

        var response = await client.PostAsync("/api/upload?name=" + Uri.EscapeDataString("../../evil/menü.jpg"), new ByteArrayContent(data));
        var result = await response.Content.ReadFromJsonAsync<WebUploadResult>();

        Assert.True(result!.Ok);
        Assert.Equal("menü.jpg", Path.GetFileName(_backend.LastUpload));
        Assert.StartsWith(_backend.UploadFolder, _backend.LastUpload);
        Assert.Equal(data, _backend.LastUploadBytes);
    }

    [Theory]
    [InlineData("..\\..\\x.png", "x.png")]
    [InlineData("a<b>:c.mp4", "abc.mp4")]
    [InlineData("", "upload.bin")]
    [InlineData("...", "upload.bin")]
    public void Unsafe_names_are_cleaned(string input, string expected) =>
        Assert.Equal(expected, WebPanelServer.SafeFileName(input));

    private sealed class FakeBackend : IWebPanelBackend
    {
        public FakeBackend(string folder) => UploadFolder = folder;

        public int Toggles { get; private set; }
        public int? LastNext { get; private set; }
        public (int, bool)? LastScreen { get; private set; }
        public string? LastUpload { get; private set; }
        public byte[]? LastUploadBytes { get; private set; }
        public string UploadFolder { get; }

        public bool VerifyPin(string pin) => pin == "4321";

        public Task<WebStatus> GetStatusAsync() => Task.FromResult(new WebStatus(true, "tr", "1.4.0",
            new[] { new WebScreen(1, "Ekran 1", true, "Gösterim açık", "Menü"), new WebScreen(2, "Ekran 2", true, "Gösterim açık", null) }, 3));

        public Task<IReadOnlyList<WebMedia>> GetLibraryAsync() => Task.FromResult<IReadOnlyList<WebMedia>>(Array.Empty<WebMedia>());
        public Task<byte[]?> GetThumbnailAsync(Guid id) => Task.FromResult<byte[]?>(null);

        public Task ToggleAsync()
        {
            Toggles++;
            return Task.CompletedTask;
        }

        public Task NextAsync(int? screen)
        {
            LastNext = screen;
            return Task.CompletedTask;
        }

        public Task SetScreenEnabledAsync(int screen, bool enabled)
        {
            LastScreen = (screen, enabled);
            return Task.CompletedTask;
        }

        public Task SetActiveAsync(Guid id, bool active) => Task.CompletedTask;
        public Task SetOnScreenAsync(Guid id, int screen, bool on) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;

        public Task<WebUploadResult> ImportAsync(string path)
        {
            LastUpload = path;
            LastUploadBytes = File.ReadAllBytes(path);
            return Task.FromResult(new WebUploadResult(true, "ok"));
        }
    }
}
