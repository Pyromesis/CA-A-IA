// CA-A-IA — Tests del actualizador (versiones, canal GitHub, descarga verificada).

using System.Net;
using System.Text;
using CaAIA.Domain.Update;
using CaAIA.Infrastructure.Update;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaAIA.Tests.Unit;

public sealed class UpdateTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_fn(request));
    }

    private static GitHubAppUpdater Updater(
        Func<HttpRequestMessage, HttpResponseMessage> fn, string current = "0.1.0") =>
        new(new HttpClient(new StubHandler(fn)),
            NullLogger<GitHubAppUpdater>.Instance, new Version(current));

    private static string ReleaseJson(string tag, string digest, long size = 4,
        bool prerelease = false) => $$"""
        {"tag_name":{{System.Text.Json.JsonSerializer.Serialize(tag)}},
         "draft":false,"prerelease":{{prerelease.ToString().ToLowerInvariant()}},
         "body":"notes",
         "assets":[{"name":"CA-A-IA-Setup-9.9.9.exe",
           "browser_download_url":"https://github.com/Pyromesis/CA-A-IA/releases/download/v9.9.9/CA-A-IA-Setup-9.9.9.exe",
           "size":{{size}},"digest":"sha256:{{digest}}"}]}
        """;

    private const string TestSha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"; // "test"

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.10.3", "0.10.3")]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    public void ParseVersion_HandlesTags(string tag, string? expected)
    {
        var parsed = GitHubAppUpdater.ParseVersion(tag);
        Assert.Equal(expected is null ? null : new Version(expected), parsed);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/x", true)]
    [InlineData("https://github.com/Pyromesis/CA-A-IA/releases/download/v1/a.exe", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://evil.com/a.exe", false)]
    public void IsAllowedUrl_RestrictsHosts(string url, bool expected)
    {
        Assert.Equal(expected, GitHubAppUpdater.IsAllowedUrl(url));
    }

    [Fact]
    public async Task Check_NewRelease_IsOffered()
    {
        var updater = Updater(_ => Json(ReleaseJson("v9.9.9", TestSha256)));
        var result = await updater.CheckForUpdatesAsync(CancellationToken.None);
        Assert.True(result.HasUpdate);
        Assert.Equal(new Version(9, 9, 9), result.AvailableUpdate!.Version);
        Assert.Equal(TestSha256, result.AvailableUpdate.Sha256);
        Assert.Equal(4, result.AvailableUpdate.SizeBytes);
    }

    [Fact]
    public async Task Check_SameVersionOrPrerelease_NoUpdate()
    {
        var same = Updater(_ => Json(ReleaseJson("v0.1.0", TestSha256)));
        Assert.False((await same.CheckForUpdatesAsync(CancellationToken.None)).HasUpdate);

        var pre = Updater(_ => Json(ReleaseJson("v9.9.9", TestSha256, prerelease: true)));
        Assert.False((await pre.CheckForUpdatesAsync(CancellationToken.None)).HasUpdate);
    }

    [Fact]
    public async Task Download_VerifiesHashAndSize()
    {
        var updater = Updater(req => req.RequestUri!.AbsolutePath.EndsWith(".exe")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("test")),
            }
            : Json(ReleaseJson("v9.9.9", TestSha256)));
        var check = await updater.CheckForUpdatesAsync(CancellationToken.None);
        var path = await updater.DownloadAsync(check.AvailableUpdate!,
            new Progress<double>(_ => { }), CancellationToken.None);
        Assert.Equal("test", await File.ReadAllTextAsync(path));
        File.Delete(path);
    }

    [Fact]
    public async Task Download_TamperedFile_Throws()
    {
        var updater = Updater(req => req.RequestUri!.AbsolutePath.EndsWith(".exe")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("evil")),
            }
            : Json(ReleaseJson("v9.9.9", TestSha256)));
        var check = await updater.CheckForUpdatesAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            updater.DownloadAsync(check.AvailableUpdate!,
                new Progress<double>(_ => { }), CancellationToken.None));
    }

    [Fact]
    public void LaunchInstaller_MissingFile_Throws()
    {
        var updater = Updater(_ => Json("{}"));
        Assert.Throws<ArgumentException>(() =>
            updater.LaunchInstaller(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), "C:\\x"));
    }

    [Fact]
    public void CurrentVersion_NormalizesRevision()
    {
        // La assembly es 0.1.0.0 y el tag 0.1.0: deben compararse iguales.
        var updater = new GitHubAppUpdater(new HttpClient(new StubHandler(_ => Json("{}"))),
            NullLogger<GitHubAppUpdater>.Instance, new Version(0, 1, 0, 0));
        Assert.Equal(new Version(0, 1, 0), updater.CurrentVersion);
    }
}
