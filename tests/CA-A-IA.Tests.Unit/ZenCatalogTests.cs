// CA-A-IA — Tests del catálogo models.dev (forma real) y del proveedor OpenCode Zen.

using System.Net;
using System.Text;
using CaAIA.Domain.AI;
using CaAIA.Infrastructure.Providers.Zen;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaAIA.Tests.Unit;

public sealed class ZenCatalogTests
{
    private const string ApiJson = """
        {"opencode":{"id":"opencode","name":"OpenCode Zen","api":"https://opencode.ai/zen/v1",
         "models":{
          "muse-spark-1.3-contributor-free":{"id":"muse-spark-1.3-contributor-free","name":"Muse Spark 1.3 Free",
           "tool_call":true,"limit":{"context":1048576,"output":131072},
           "modalities":{"input":["text","image"]},"cost":{"input":0,"output":0}},
          "claude-sonnet-4-6":{"id":"claude-sonnet-4-6","name":"Claude Sonnet 4.6",
           "tool_call":true,"limit":{"context":1000000,"output":64000},
           "modalities":{"input":["text"]},"cost":{"input":3,"output":15}},
          "broken":{"name":"Sin id"},
          "plain":{"id":"plain","name":"Plain"}}},
         "other":{"id":"other","models":{}}}
        """;

    private static ModelsDevCatalog Catalog(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string dataPath, out Counter calls)
    {
        var counter = new Counter();
        calls = counter;
        var handler = new StubHandler(req =>
        {
            counter.Value++;
            return responder(req);
        });
        return new ModelsDevCatalog(new HttpClient(handler), dataPath,
            NullLogger<ModelsDevCatalog>.Instance, "https://example.test/api.json", TimeSpan.FromHours(24));
    }

    private sealed class Counter
    {
        public int Value;
    }

    [Fact]
    public async Task ParseModel_DetectsFreeContextToolsVision()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"id":"m","name":"M","tool_call":true,"limit":{"context":200000},"modalities":{"input":["text","image"]},"cost":{"input":0,"output":0}}""");
        var model = ModelsDevCatalog.ParseModel("opencode-zen", doc.RootElement)!;
        Assert.Equal("m", model.Id);
        Assert.True(model.IsFree);
        Assert.Equal(200000, model.ContextWindowTokens);
        Assert.Equal(ProviderCapabilities.Tools, model.Capabilities & ProviderCapabilities.Tools);
        Assert.Equal(ProviderCapabilities.Vision, model.Capabilities & ProviderCapabilities.Vision);
        Assert.Equal(ProviderCapabilities.LongContext, model.Capabilities & ProviderCapabilities.LongContext);
    }

    [Fact]
    public async Task Catalog_DownloadsThenCaches()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ca-a-ia-zen-{Guid.NewGuid():N}");
        try
        {
            var catalog = Catalog(_ => Json(ApiJson), dir, out var calls);
            var first = (await catalog.GetOpenCodeModelsAsync("opencode-zen", CancellationToken.None)).ToList();
            Assert.Equal(1, calls.Value);
            Assert.Contains(first, m => m.Id == "muse-spark-1.3-contributor-free" && m.IsFree);
            Assert.Contains(first, m => m.Id == "claude-sonnet-4-6" && !m.IsFree);
            Assert.Contains(first, m => m.Id == "plain" && !m.IsFree);
            Assert.DoesNotContain(first, m => m.Id == "broken");
            Assert.True(File.Exists(Path.Combine(dir, "models-dev-api.json")));

            var second = await catalog.GetOpenCodeModelsAsync("opencode-zen", CancellationToken.None);
            Assert.Equal(1, calls.Value); // caché: sin segunda descarga
            Assert.Equal(first.Count, second.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task Catalog_FallsBackToStaleCache_WhenDownloadFails()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ca-a-ia-zen-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var cache = Path.Combine(dir, "models-dev-api.json");
            await File.WriteAllTextAsync(cache, ApiJson);
            File.SetLastWriteTimeUtc(cache, DateTimeOffset.UtcNow.AddDays(-2).DateTime); // caducada

            var catalog = Catalog(
                _ => new HttpResponseMessage(HttpStatusCode.BadGateway), dir, out _);
            var models = await catalog.GetOpenCodeModelsAsync("opencode-zen", CancellationToken.None);
            Assert.NotEmpty(models); // caché vieja antes que nada
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }

    [Fact]
    public void ParseReasoningOptions_ReadsEffortValues()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{"id":"m","reasoning_options":[{"type":"effort","values":["Low","Medium","Xhigh"]},{"type":"other","values":["z"]}]}""");
        Assert.Equal(new[] { "low", "medium", "xhigh" },
            ModelsDevCatalog.ParseReasoningOptions(doc.RootElement));
        using var empty = System.Text.Json.JsonDocument.Parse("""{"id":"m"}""");
        Assert.Empty(ModelsDevCatalog.ParseReasoningOptions(empty.RootElement));
    }
    [Fact]
    public async Task Catalog_UnparsableShape_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ca-a-ia-zen-{Guid.NewGuid():N}");
        try
        {
            var catalog = Catalog(_ => Json("""{"nada":[]}"""), dir, out _);
            Assert.Empty(await catalog.GetOpenCodeModelsAsync("opencode-zen", CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_fn(request));
    }
}
