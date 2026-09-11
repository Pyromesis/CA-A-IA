// CA-A-IA — Tests de LlmPlanVerifier: unión conservadora + fallback.

using CaAIA.Agent.Verification;
using CaAIA.Application.Configuration;
using CaAIA.Domain.AI;
using CaAIA.Domain.Planning;
using CaAIA.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaAIA.Tests.Unit;

public sealed class LlmVerifierTests
{
    private static LlmPlanVerifier Verifier(IAIProvider provider, string modelId = "m")
    {
        var options = Options.Create(new CaAIAOptions
        {
            Providers = new ProviderSettings { DefaultProviderId = "fake", DefaultModelId = modelId },
        });
        return new LlmPlanVerifier(new SingleRegistry(provider), options,
            new Application.Services.UserPreferences(options, new MemorySettingsStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Services.UserPreferences>.Instance),
            new FinalPlanAuditor(NullLogger<FinalPlanAuditor>.Instance),
            NullLogger<LlmPlanVerifier>.Instance);
    }

    private static Plan CoveredPlan() => new()
    {
        SessionId = Guid.NewGuid(),
        Goal = "Add login",
        Requirements = new[] { "Implement user login flow" },
        Tasks = new List<AgentTask>
        {
            new() { Title = "Implement user login flow", Description = "login flow" },
        },
    };

    [Fact]
    public async Task Union_MechanicalAndSemanticGaps()
    {
        var verifier = Verifier(new JsonProvider(
            """{"satisfied": false, "missingRequirements": ["Docs not updated"]}"""));
        var report = await verifier.AuditAsync("Add login", CoveredPlan(),
            Array.Empty<string>(), CancellationToken.None);
        Assert.False(report.IsSatisfied);
        // La heurística no ve gaps (cobertura ok), el modelo sí: unión.
        Assert.Contains(report.MissingRequirements, m => m.Contains("Docs not updated"));
    }

    [Fact]
    public async Task SatisfiedModel_StillSatisfied()
    {
        var verifier = Verifier(new JsonProvider("""{"satisfied": true, "missingRequirements": []}"""));
        var report = await verifier.AuditAsync("Add login", CoveredPlan(),
            Array.Empty<string>(), CancellationToken.None);
        Assert.True(report.IsSatisfied);
    }

    [Fact]
    public async Task ProviderFailure_FallsBackToHeuristic()
    {
        var verifier = Verifier(new BoomProvider());
        var report = await verifier.AuditAsync("Add login", CoveredPlan(),
            Array.Empty<string>(), CancellationToken.None);
        Assert.True(report.IsSatisfied); // la heurística no ve gaps
    }

    [Fact]
    public async Task NoModel_UsesHeuristicOnly()
    {
        var verifier = Verifier(new JsonProvider("""{"satisfied": false}"""), modelId: string.Empty);
        var report = await verifier.AuditAsync("Add login", CoveredPlan(),
            Array.Empty<string>(), CancellationToken.None);
        Assert.True(report.IsSatisfied);
    }

    // ---- Doubles ----

    private sealed class MemorySettingsStore : Domain.Persistence.ISettingsStore
    {
        private readonly Dictionary<string, string> _data = new();
        public Task<string?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult<string?>(_data.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct)
        {
            _data[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct)
        {
            _data.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class SingleRegistry : IProviderRegistry
    {
        private readonly IAIProvider _provider;
        public SingleRegistry(IAIProvider provider) => _provider = provider;
        public void Register(IAIProvider provider) => throw new NotSupportedException();
        public bool Remove(string providerId) => throw new NotSupportedException();
        public IAIProvider Get(string providerId) => _provider;
        public IReadOnlyCollection<IAIProvider> GetAll() => new[] { _provider };
        public IReadOnlyCollection<AIModel> GetAvailableModels() => Array.Empty<AIModel>();
        public Task<IReadOnlyCollection<AIModel>> GetAvailableModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<AIModel>>(Array.Empty<AIModel>());
        public IReadOnlyCollection<IAIProvider> GetProvidersSupporting(ProviderCapabilities required) =>
            Array.Empty<IAIProvider>();
    }

    private sealed class JsonProvider : IAIProvider
    {
        private readonly string _json;
        public JsonProvider(string json) => _json = json;
        public string Id => "fake";
        public string DisplayName => "fake";
        public ProviderCapabilities Capabilities => ProviderCapabilities.StructuredOutput;
        public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct) =>
            Task.FromResult(new AIResponse(_json, Array.Empty<AIToolCall>(), "m", new TokenUsage(1, 1)));
        public IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken ct) =>
            AsyncEnumerable.Empty<AIStreamChunk>();
        public Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<AIModel>>(Array.Empty<AIModel>());
        public Task<bool> CheckHealthAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class BoomProvider : IAIProvider
    {
        public string Id => "fake";
        public string DisplayName => "fake";
        public ProviderCapabilities Capabilities => ProviderCapabilities.None;
        public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct) =>
            throw new AIProviderException("fake", AIErrorKind.Timeout, "t", isRetryable: true);
        public IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken ct) =>
            AsyncEnumerable.Empty<AIStreamChunk>();
        public Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<AIModel>>(Array.Empty<AIModel>());
        public Task<bool> CheckHealthAsync(CancellationToken ct) => Task.FromResult(false);
    }
}
