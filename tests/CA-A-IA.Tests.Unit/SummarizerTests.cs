// CA-A-IA — Tests del resumido de contexto (decorador con proveedor simulado).

using CaAIA.Application.Configuration;
using CaAIA.Domain.AI;
using CaAIA.Domain.Context;
using CaAIA.Infrastructure.AI;
using CaAIA.Infrastructure.Context;
using CaAIA.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaAIA.Tests.Unit;

public sealed class SummarizerTests
{
    private static SummarizingContextBuilder Decorator(
        IContextBuilder inner, IAIProvider? provider, string modelId = "m")
    {
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        if (provider is not null)
        {
            registry.Register(provider);
        }

        var realInner = inner as IContextBuilder
            ?? throw new InvalidOperationException("test needs IContextBuilder doubles");
        return new SummarizingContextBuilder(realInner, registry,
            Options.Create(new CaAIAOptions
            {
                Providers = new ProviderSettings { DefaultProviderId = "fake", DefaultModelId = modelId },
            }),
            NullLogger<SummarizingContextBuilder>.Instance);
    }

    [Fact]
    public async Task UnderBudget_Passthrough()
    {
        // Sin modelo configurado y por debajo del presupuesto: no toca nada.
        var inner = new CappedReader(new Dictionary<string, string> { ["a.cs"] = "class A {}" });
        var decorator = Decorator(inner, provider: null, modelId: string.Empty);
        var context = await decorator.BuildAsync("/ws", Array.Empty<string>(), 10_000, CancellationToken.None);
        Assert.Single(context.Fragments);
        Assert.Equal(0, context.TruncatedFragments);
    }

    [Fact]
    public async Task OverBudget_SummarizesDropped_WithModel()
    {
        var files = new Dictionary<string, string>();
        for (var i = 0; i < 10; i++)
        {
            files[$"f{i}.cs"] = new string('x', 5_000);
        }

        var inner = new CappedReader(files);
        var provider = new EchoProvider("SUMMARY");
        var decorator = Decorator(inner, provider);
        var context = await decorator.BuildAsync("/ws", Array.Empty<string>(), 10_000, CancellationToken.None);

        Assert.Contains(context.Fragments, f => f.Kind == "summary" && f.Content == "SUMMARY");
        Assert.True(context.TotalChars <= 10_000 + 5_000); // resumen acotado al resto del presupuesto
    }

    [Fact]
    public async Task OverBudget_Truncates_WhenModelFails()
    {
        var files = new Dictionary<string, string>();
        for (var i = 0; i < 10; i++)
        {
            files[$"f{i}.cs"] = new string('y', 5_000);
        }

        var inner = new CappedReader(files);
        var decorator = Decorator(inner, new FailingProvider());
        var context = await decorator.BuildAsync("/ws", Array.Empty<string>(), 10_000, CancellationToken.None);

        Assert.DoesNotContain(context.Fragments, f => f.Kind == "summary");
        Assert.True(context.TruncatedFragments > 0);
    }

    // ---- Doubles ----

    private sealed class CappedReader : IContextBuilder
    {
        private readonly Dictionary<string, string> _files;
        public CappedReader(Dictionary<string, string> files) => _files = files;

        public Task<ProjectContext> BuildAsync(
            string workspacePath, IReadOnlyList<string> hints, int maxChars, CancellationToken ct)
        {
            var fragments = _files.Select(kv => new ContextFragment("file", kv.Key, kv.Value, 1)).ToList();
            return Task.FromResult(new ProjectContext(workspacePath, fragments,
                fragments.Sum(f => f.Content.Length), 0));
        }
    }

    private sealed class EchoProvider : IAIProvider
    {
        private readonly string _text;
        public EchoProvider(string text) => _text = text;
        public string Id => "fake";
        public string DisplayName => "fake";
        public ProviderCapabilities Capabilities => ProviderCapabilities.None;
        public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct) =>
            Task.FromResult(new AIResponse(_text, Array.Empty<AIToolCall>(), "m", new TokenUsage(1, 1)));
        public IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken ct) =>
            AsyncEnumerable.Empty<AIStreamChunk>();
        public Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<AIModel>>(Array.Empty<AIModel>());
        public Task<bool> CheckHealthAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FailingProvider : IAIProvider
    {
        public string Id => "fake";
        public string DisplayName => "fake";
        public ProviderCapabilities Capabilities => ProviderCapabilities.None;
        public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct) =>
            throw new AIProviderException("fake", AIErrorKind.Network, "down", isRetryable: true);
        public IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken ct) =>
            AsyncEnumerable.Empty<AIStreamChunk>();
        public Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<AIModel>>(Array.Empty<AIModel>());
        public Task<bool> CheckHealthAsync(CancellationToken ct) => Task.FromResult(false);
    }
}
