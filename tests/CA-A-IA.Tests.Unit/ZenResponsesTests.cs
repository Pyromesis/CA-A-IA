// CA-A-IA — Tests del cliente Responses API + routing por familia en Zen.

using System.Net;
using System.Text;
using CaAIA.Domain.AI;
using CaAIA.Infrastructure.AI;
using CaAIA.Infrastructure.Providers.Zen;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaAIA.Tests.Unit;

public sealed class ZenResponsesTests
{
    private static AIRequest Request(string model) => new()
    {
        ModelId = model,
        Messages = new[] { new AIMessage(AIRole.User, "hi") },
        Correlation = new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
    };

    [Theory]
    [InlineData("muse-spark-1.3-contributor-free", true)]
    [InlineData("gpt-5-nano", true)]
    [InlineData("grok-code", true)]
    [InlineData("MUSE-X", true)]
    [InlineData("big-pickle", false)]
    [InlineData("deepseek-v4-flash-free", false)]
    [InlineData("claude-sonnet-4-6", false)]
    public void Routing_FamiliesUseResponsesApi(string model, bool expected) =>
        Assert.Equal(expected, OpenCodeZenProvider.UsesResponsesApi(model));

    [Fact]
    public void AdjustCapabilities_StripsTools_ForResponsesModels()
    {
        var model = new AIModel("muse-spark-1.3-contributor-free", "M", "opencode-zen",
            1000, ProviderCapabilities.Streaming | ProviderCapabilities.Tools | ProviderCapabilities.LongContext);
        var adjusted = OpenCodeZenProvider.AdjustCapabilities(model);
        Assert.Equal(ProviderCapabilities.Tools, model.Capabilities & ProviderCapabilities.Tools);
        Assert.Equal(ProviderCapabilities.None, adjusted.Capabilities & ProviderCapabilities.Tools);
        Assert.Equal(ProviderCapabilities.LongContext, adjusted.Capabilities & ProviderCapabilities.LongContext);

        var chat = new AIModel("big-pickle", "B", "opencode-zen",
            1000, ProviderCapabilities.Streaming | ProviderCapabilities.Tools);
        Assert.Same(chat, OpenCodeZenProvider.AdjustCapabilities(chat));
    }

    [Fact]
    public async Task Responses_ParsesOutputText_AndUsage()
    {
        var client = new OpenAIResponsesClient(
            new HttpClient(new StubHandler(_ => Json("""
                {"id":"r1","output":[
                  {"type":"message","content":[
                    {"type":"output_text","text":"Hello "},
                    {"type":"output_text","text":"world"}]},
                  {"type":"reasoning","content":[]}],
                 "usage":{"input_tokens":7,"output_tokens":3}}
                """))),
            "opencode-zen", "https://example.test/v1",
            _ => Task.FromResult<string?>("key"),
            NullLogger<OpenAIResponsesClient>.Instance);

        var response = await client.CompleteAsync(Request("muse-spark-1.3-contributor-free"),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal("Hello world", response.Content);
        Assert.Equal(7, response.Usage.PromptTokens);
        Assert.Equal(3, response.Usage.CompletionTokens);
        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public async Task Responses_MapsAuthError_NotRetryable()
    {
        var client = new OpenAIResponsesClient(
            new HttpClient(new StubHandler(_ => Json(
                """{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized))),
            "opencode-zen", "https://example.test/v1",
            _ => Task.FromResult<string?>(null),
            NullLogger<OpenAIResponsesClient>.Instance);

        var ex = await Assert.ThrowsAsync<AIProviderException>(() => client.CompleteAsync(
            Request("muse-spark-1.3-contributor-free"), TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(AIErrorKind.Authentication, ex.Kind);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task Responses_Stream_FallsBackToWhole()
    {
        var client = new OpenAIResponsesClient(
            new HttpClient(new StubHandler(_ => Json(
                """{"output":[{"type":"message","content":[{"type":"output_text","text":"abc"}]}]}"""))),
            "opencode-zen", "https://example.test/v1",
            _ => Task.FromResult<string?>("key"),
            NullLogger<OpenAIResponsesClient>.Instance);

        var chunks = new List<AIStreamChunk>();
        await foreach (var chunk in client.StreamAsync(Request("gpt-5-nano"),
            TimeSpan.FromSeconds(10), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("abc", chunks[0].Delta);
        Assert.True(chunks[^1].IsFinal);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_fn(request));
    }
}
