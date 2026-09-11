// CA-A-IA — Tests de la integración OpenCode (parseo + proveedor con HTTP simulado).

using System.Net;
using System.Text;
using System.Text.Json;
using CaAIA.Domain.AI;
using CaAIA.Domain.Security;
using CaAIA.Infrastructure.Providers.OpenCode;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaAIA.Tests.Unit;

public sealed class OpenCodeTests
{
    [Fact]
    public void ParseAnswer_ConcatenatesTextParts_AndReadsTokens()
    {
        using var doc = JsonDocument.Parse("""
            {"info":{"tokens":{"input":12,"output":34}},
             "parts":[{"type":"text","text":"Hello "},{"type":"reasoning","text":"hmm"},
                      {"type":"text","text":"world"}]}
            """);
        var answer = OpenCodeServerClient.ParseAnswer(doc.RootElement);
        Assert.Equal("Hello world", answer.Text);
        Assert.Equal(12, answer.InputTokens);
        Assert.Equal(34, answer.OutputTokens);
    }

    [Fact]
    public void ParseAnswer_UnknownShape_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"weird":[1,2]}""");
        var answer = OpenCodeServerClient.ParseAnswer(doc.RootElement);
        Assert.Equal(string.Empty, answer.Text);
    }

    [Fact]
    public void ParseProviders_FlattensModels()
    {
        using var doc = JsonDocument.Parse("""
            {"providers":[
              {"id":"anthropic","models":[{"id":"claude-x"},{"modelID":"claude-y"}]},
              {"id":"local","models":["m1"]},
              {"id":"empty"}]}
            """);
        var models = OpenCodeServerClient.ParseProviders(
            OpenCodeServerClient.UnwrapData(doc.RootElement)).ToList();
        Assert.Equal(3, models.Count);
        Assert.Contains(models, m => m.Id == "anthropic/claude-x");
        Assert.Contains(models, m => m.Id == "local/m1");
    }

    [Fact]
    public void ParseProviders_ServerShape_ObjectModels_WithCosts()
    {
        // Forma real del servidor (verificada v1.18): models como objeto + cost/limit/capabilities.
        using var doc = JsonDocument.Parse("""
            {"providers":[
              {"id":"opencode","name":"OpenCode Zen","models":{
                "nemotron-3-ultra-free":{"id":"nemotron-3-ultra-free","name":"Nemotron 3 Ultra Free",
                 "limit":{"context":1000000,"output":128000},
                 "capabilities":{"toolcall":true,"input":{"text":true,"image":false}},
                 "cost":{"input":0,"output":0}},
                "claude-sonnet-4-6":{"id":"claude-sonnet-4-6","name":"Claude Sonnet 4.6",
                 "limit":{"context":1000000},"cost":{"input":3,"output":15}}}},
              {"id":"empty","models":{}}]}
            """);
        var models = OpenCodeServerClient.ParseProviders(
            OpenCodeServerClient.UnwrapData(doc.RootElement)).ToList();
        Assert.Equal(2, models.Count);
        var free = models.First(m => m.Id == "opencode/nemotron-3-ultra-free");
        Assert.True(free.IsFree);
        Assert.Equal(1000000, free.ContextWindow);
        Assert.True(free.SupportsTools);
        Assert.False(free.SupportsVision);
        var paid = models.First(m => m.Id == "opencode/claude-sonnet-4-6");
        Assert.False(paid.IsFree);
    }

    [Fact]
    public void SplitModel_HandlesForms()
    {
        Assert.Equal(("anthropic", "claude-x"), OpenCodeProvider.SplitModel("anthropic/claude-x"));
        Assert.Equal(("a", "b/c"), OpenCodeProvider.SplitModel("a/b/c"));
        Assert.Equal((null, null), OpenCodeProvider.SplitModel("plainmodel"));
        Assert.Equal((null, null), OpenCodeProvider.SplitModel("/x"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Default", null)]
    [InlineData("High", "high")]
    [InlineData("Xhigh", "xhigh")]
    [InlineData("bogus", null)]
    public void NormalizeEffort_MapsKnownValues(string? input, string? expected) =>
        Assert.Equal(expected, OpenCodeProvider.NormalizeEffort(input));

    [Fact]
    public async Task Complete_FullFlow_ReturnsServerText()
    {
        var provider = ProviderWithStub(out var seen);
        var response = await provider.CompleteAsync(Request("anthropic/claude-x"), CancellationToken.None);

        Assert.Equal("Hello world", response.Content);
        Assert.Empty(response.ToolCalls);
        Assert.True(SeenContains(seen, "session") && SeenContains(seen, "/message"));
    }

    [Fact]
    public void ParseAnswer_ModelError_Throws()
    {
        using var doc = JsonDocument.Parse("""
            {"info":{"role":"assistant","error":{"name":"APIError","data":{"message":"bad creds"}}}}
            """);
        var ex = Assert.Throws<AIProviderException>(
            () => OpenCodeServerClient.ParseAnswer(doc.RootElement));
        Assert.Contains("bad creds", ex.Message);
    }

    [Fact]
    public async Task GetModels_MapsCatalog()
    {
        var provider = ProviderWithStub(out _);
        var models = (await provider.GetModelsAsync(CancellationToken.None)).ToList();
        Assert.Contains(models, m => m.Id == "anthropic/claude-x" && m.ProviderId == "opencode");
    }

    [Fact]
    public async Task Complete_SendsVariant_WhenEffortSet()
    {
        string? body = null;
        var handler = new StubHandler(req =>
        {
            if (req.Content is not null)
            {
                body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }

            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/session", StringComparison.Ordinal))
            {
                return Json("""{"id":"s9","title":"t"}""");
            }

            return Json("""{"parts":[{"type":"text","text":"ok"}]}""");
        });
        var provider = new OpenCodeProvider(
            Options.Create(new OpenCodeOptions()),
            new NullSecrets(),
            new Infrastructure.Process.ProcessRunner(),
            NullLogger<OpenCodeServerClient>.Instance,
            NullLogger<ProcessServerLifecycle>.Instance,
            new HttpClient(handler),
            new NullServerLifecycle("http://127.0.0.1:9"));

        var request = new AIRequest
        {
            ModelId = "opencode/big-pickle",
            Messages = new[] { new AIMessage(AIRole.User, "do the thing") },
            ReasoningEffort = "high",
            Correlation = new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
        };
        var response = await provider.CompleteAsync(request, CancellationToken.None);
        Assert.Equal("ok", response.Content);
        Assert.Contains("\"variant\":\"high\"", body);
    }

    [Fact]
    public async Task Health_WithoutBinary_IsFalse_AndModelsExplainWhy()
    {
        var options = Options.Create(new OpenCodeOptions { Command = "definitely-not-opencode-xyz" });
        var provider = new OpenCodeProvider(options, new NullSecrets(),
            new Infrastructure.Process.ProcessRunner(),
            NullLogger<OpenCodeServerClient>.Instance, NullLogger<ProcessServerLifecycle>.Instance);
        Assert.False(await provider.CheckHealthAsync(CancellationToken.None));
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.GetModelsAsync(CancellationToken.None));
        Assert.Contains("opencode-zen", ex.Message);
    }

    [Fact]
    public void Lifecycle_FindBinary_Behaves()
    {
        Assert.Null(ProcessServerLifecycle.FindBinary("definitely-not-opencode-xyz"));
        var tmp = Path.Combine(Path.GetTempPath(), $"fake-tool-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(tmp, "x");
            Assert.NotNull(ProcessServerLifecycle.FindBinary(tmp));
            // Sin extensión no es ejecutable en Windows (shim sh de npm).
            var bare = Path.Combine(Path.GetTempPath(), $"fake-tool-{Guid.NewGuid():N}");
            File.WriteAllText(bare, "x");
            try
            {
                Assert.Null(ProcessServerLifecycle.FindBinary(bare));
            }
            finally
            {
                File.Delete(bare);
            }
        }
        finally
        {
            File.Delete(tmp);
        }

        var port = ProcessServerLifecycle.FreePort();
        Assert.InRange(port, 1, 65535);
    }

    // ---- Helpers ----

    private static bool SeenContains(List<string> seen, string part) =>
        seen.Any(s => s.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static AIRequest Request(string model) => new()
    {
        ModelId = model,
        Messages = new[] { new AIMessage(AIRole.User, "do the thing") },
        Correlation = new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
    };

    private static OpenCodeProvider ProviderWithStub(out List<string> seen)
    {
        var seenPaths = new List<string>();
        seen = seenPaths;
        var handler = new StubHandler(req =>
        {
            seenPaths.Add($"{req.Method} {req.RequestUri!.AbsolutePath}");
            var path = req.RequestUri.AbsolutePath;
            if (path.EndsWith("/session", StringComparison.Ordinal))
            {
                return Json("""{"id":"s1","title":"t"}""");
            }

            if (path.Contains("/message", StringComparison.Ordinal))
            {
                return Json("""{"parts":[{"type":"text","text":"Hello "},{"type":"text","text":"world"}]}""");
            }

            if (path.EndsWith("/config/providers", StringComparison.Ordinal))
            {
                return Json("""{"providers":[{"id":"anthropic","models":[{"id":"claude-x"}]}]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        return new OpenCodeProvider(
            Options.Create(new OpenCodeOptions()),
            new NullSecrets(),
            new Infrastructure.Process.ProcessRunner(),
            NullLogger<OpenCodeServerClient>.Instance,
            NullLogger<ProcessServerLifecycle>.Instance,
            new HttpClient(handler),
            new NullServerLifecycle("http://127.0.0.1:9"));
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

    private sealed class NullSecrets : ISecretStore
    {
        public Task StoreAsync(string key, string secret, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> RetrieveAsync(string key, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task RemoveAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }
}
