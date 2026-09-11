// CA-A-IA — Tests de autodetección de servidor local (Ollama/LM Studio).

using System.Net;
using System.Text;
using CaAIA.Infrastructure.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaAIA.Tests.Unit;

public sealed class LocalDetectTests
{
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> fn) =>
        new(new StubHandler(fn));

    [Fact]
    public async Task ProbeFirst_PicksFirstAlive()
    {
        var seen = new List<string>();
        var http = Client(req =>
        {
            seen.Add(req.RequestUri!.ToString());
            return req.RequestUri.ToString().Contains("1234")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : throw new HttpRequestException("refused");
        });
        var found = await LocalModelProvider.ProbeFirstAsync(http,
            new[] { "http://localhost:11434/v1", "http://localhost:1234/v1" },
            TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("http://localhost:1234/v1", found);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task ProbeFirst_AllDead_ReturnsNull()
    {
        var http = Client(_ => throw new HttpRequestException("refused"));
        var found = await LocalModelProvider.ProbeFirstAsync(http,
            new[] { "http://localhost:11434/v1" }, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Null(found);
    }

    [Fact]
    public async Task ProbeFirst_CallerCancellation_Propagates()
    {
        var http = Client(_ => throw new HttpRequestException("refused"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalModelProvider.ProbeFirstAsync(
            http, new[] { "http://localhost:11434/v1" }, TimeSpan.FromSeconds(5), cts.Token));
    }

    [Fact]
    public async Task GetModels_WithoutServer_ReturnsEmptyFast()
    {
        var provider = new LocalModelProvider(
            Options.Create(new LocalModelOptions()),
            NullLogger<Infrastructure.AI.OpenAICompatibleClient>.Instance);
        // Sin Enable explícito usa defaults (Enabled=true) pero sin servidor: vacío, sin colgarse.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var models = await provider.GetModelsAsync(CancellationToken.None);
        sw.Stop();
        Assert.Empty(models);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_fn(request));
    }
}
