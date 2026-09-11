// CA-A-IA — Tests del cliente OpenAI-compatible con HttpMessageHandler simulado (sin red).

using System.Net;
using System.Text;
using CaAIA.Domain.AI;
using CaAIA.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaAIA.Tests.Unit;

public sealed class ProviderClientTests
{
    private static OpenAICompatibleClient Client(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string providerId = "test") =>
        new(new HttpClient(new StubHandler(responder)), providerId, "https://example.test/v1",
            _ => Task.FromResult<string?>("key"),
            ProviderCapabilities.Streaming | ProviderCapabilities.Tools,
            NullLogger<OpenAICompatibleClient>.Instance);

    private static AIRequest Request(string model = "m") => new()
    {
        ModelId = model,
        Messages = new[] { new AIMessage(AIRole.User, "hi") },
        Correlation = new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
    };

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Complete_ParsesContentUsageAndToolCalls()
    {
        var client = Client(_ => Json("""
            {"id":"c1","choices":[{"message":{
              "role":"assistant","content":"reading file",
              "tool_calls":[{"id":"call1","type":"function",
                "function":{"name":"ReadFile","arguments":"{\"path\":\"f\"}"}}]},
              "finish_reason":"tool_calls"}],
             "usage":{"prompt_tokens":10,"completion_tokens":5}}
            """));

        var response = await client.CompleteAsync(Request(), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal("reading file", response.Content);
        Assert.Equal(10, response.Usage.PromptTokens);
        Assert.Equal(5, response.Usage.CompletionTokens);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call1", call.Id);
        Assert.Equal("ReadFile", call.ToolName);
        Assert.Contains("path", call.ArgumentsJson);
    }

    [Fact]
    public async Task Complete_SendsToolsSchemaAndSystemPrompt()
    {
        string? body = null;
        var client = Client(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        var tools = new[]
        {
            new Domain.Tools.ToolDefinition("ReadFile", "ReadFile", "Reads.", Domain.Enums.ToolKind.FileSystem,
                Domain.Enums.ToolPermission.Read,
                new[] { new Domain.Tools.ToolParameter("path", "File path.", "string", true) },
                TimeSpan.FromSeconds(30)),
        };
        var req = Request();
        var withTools = new AIRequest
        {
            ModelId = req.ModelId, Messages = req.Messages, Tools = tools, Correlation = req.Correlation,
        };
        await client.CompleteAsync(withTools, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.NotNull(body);
        Assert.Contains("\"tools\"", body);
        Assert.Contains("ReadFile", body);
        Assert.Contains("\"required\":[\"path\"]", body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AIErrorKind.Authentication, false)]
    [InlineData(HttpStatusCode.Forbidden, AIErrorKind.Authorization, false)]
    [InlineData(HttpStatusCode.NotFound, AIErrorKind.ModelNotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, AIErrorKind.RateLimited, true)]
    [InlineData(HttpStatusCode.InternalServerError, AIErrorKind.ProviderInternal, true)]
    public async Task Complete_MapsErrors_WithRetryability(
        HttpStatusCode status, AIErrorKind kind, bool retryable)
    {
        var client = Client(_ => Json("""{"error":{"message":"boom"}}""", status));
        var ex = await Assert.ThrowsAsync<AIProviderException>(
            () => client.CompleteAsync(Request(), TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(retryable, ex.IsRetryable);
    }

    [Fact]
    public async Task Complete_RateLimited_ParsesRetryAfter()
    {
        var response = Json("""{"error":{"message":"slow down"}}""", HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        var client = Client(_ => response);
        var ex = await Assert.ThrowsAsync<AIProviderException>(
            () => client.CompleteAsync(Request(), TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(AIErrorKind.RateLimited, ex.Kind);
        Assert.NotNull(ex.RetryAfter);
        Assert.True(ex.RetryAfter.Value.TotalSeconds is >= 6 and <= 8);
    }

    [Fact]
    public async Task GetModels_ParsesList_AndMarksLongContext()
    {
        var client = Client(_ => Json("""
            {"data":[
              {"id":"small","name":"Small","context_length":8000},
              {"id":"big","name":"Big","context_length":200000},
              {"nope":1}]}
            """));
        var models = (await client.GetModelsAsync(CancellationToken.None)).ToList();
        Assert.Equal(2, models.Count);
        Assert.DoesNotContain(models, m => (m.Capabilities & ProviderCapabilities.LongContext) != 0 && m.Id == "small");
        Assert.Contains(models, m => m.Id == "big" && (m.Capabilities & ProviderCapabilities.LongContext) != 0);
    }

    [Fact]
    public async Task GetModels_ServerError_ReturnsEmpty()
    {
        var client = Client(_ => Json("oops", HttpStatusCode.InternalServerError));
        Assert.Empty(await client.GetModelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stream_YieldsDeltas_AndAccumulatesToolArgs()
    {
        static string Data(object payload) =>
            "data: " + System.Text.Json.JsonSerializer.Serialize(payload) + "\n\n";

        var sse =
            Data(new { choices = new[] { new { delta = new { content = "Hel" } } } }) +
            Data(new { choices = new[] { new { delta = new { content = "lo" } } } }) +
            Data(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    id = "c1",
                                    function = new { name = "ReadFile", arguments = "{\"pa" },
                                },
                            },
                        },
                    },
                },
            }) +
            Data(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new { function = new { arguments = "th\"}" } },
                            },
                        },
                    },
                },
            }) +
            "data: [DONE]\n";
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var chunks = new List<AIStreamChunk>();
        await foreach (var chunk in client.StreamAsync(Request(), TimeSpan.FromSeconds(10), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("Hel", chunks[0].Delta);
        Assert.Equal("lo", chunks[1].Delta);
        var final = Assert.Single(chunks, c => c.IsFinal);
        Assert.NotNull(final.PartialToolCall);
        Assert.Equal("c1", final.PartialToolCall.Id);
        Assert.Equal("ReadFile", final.PartialToolCall.ToolName);
        Assert.Equal("{\"path\"}", final.PartialToolCall.ArgumentsJson);
    }

    [Fact]
    public async Task Complete_SendsReasoningEffort_WhenEnabled()
    {
        string? body = null;
        var client = new OpenAICompatibleClient(
            new HttpClient(new StubHandler(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
            })),
            "test", "https://example.test/v1", _ => Task.FromResult<string?>(null),
            ProviderCapabilities.None, NullLogger<OpenAICompatibleClient>.Instance,
            sendReasoningEffort: true);

        var req = Request();
        await client.CompleteAsync(new AIRequest
        {
            ModelId = req.ModelId,
            Messages = req.Messages,
            Correlation = req.Correlation,
            ReasoningEffort = "xhigh",
        }, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.NotNull(body);
        Assert.Contains("\"reasoning\":{\"effort\":\"xhigh\"}", body);
    }

    [Fact]
    public async Task Complete_OmitsReasoning_ByDefault()
    {
        string? body = null;
        var client = Client(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        var req = Request();
        await client.CompleteAsync(new AIRequest
        {
            ModelId = req.ModelId,
            Messages = req.Messages,
            Correlation = req.Correlation,
            ReasoningEffort = "high",
        }, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.NotNull(body);
        Assert.DoesNotContain("\"reasoning\"", body);
    }

    [Fact]
    public async Task GetModels_ParsesSupportedEfforts()
    {
        var client = Client(_ => Json("""
            {"data":[
              {"id":"a","reasoning":{"supported_efforts":["High","Medium"],"default_effort":"medium"}},
              {"id":"b"}]}
            """));
        var models = (await client.GetModelsAsync(CancellationToken.None)).ToList();
        Assert.Equal(new[] { "high", "medium" }, models[0].SupportedEfforts);
        Assert.Empty(models[1].SupportedEfforts);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_responder(request));
    }
}
