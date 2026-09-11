// CA-A-IA — Tests de LlmPlanner: parseo estructurado + fallback heurístico.

using CaAIA.Agent.Planning;
using CaAIA.Application.Configuration;
using CaAIA.Domain.AI;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Execution;
using CaAIA.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaAIA.Tests.Unit;

public sealed class LlmPlannerTests
{
    private static LlmPlanner Planner(IAIProvider provider, string modelId = "m")
    {
        var options = Options.Create(new CaAIAOptions
        {
            Providers = new ProviderSettings { DefaultProviderId = "fake", DefaultModelId = modelId },
        });
        return new LlmPlanner(new SingleRegistry(provider), options,
            new Application.Services.UserPreferences(options, new MemorySettingsStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Services.UserPreferences>.Instance),
            new HeuristicPlanner(NullLogger<HeuristicPlanner>.Instance),
            NullLogger<LlmPlanner>.Instance);
    }

    private static ProjectContextSnapshot Snapshot(string workspace) =>
        new(workspace, new[] { "a.cs" }, "build ok", "git clean");

    [Fact]
    public async Task CreatePlan_ParsesModelJson()
    {
        var planner = Planner(new ScriptedProvider("""
            {"goal":"Add login","requirements":["Login works"],
             "questions":[{"question":"OAuth or local?","options":["OAuth","local"]}],
             "tasks":[{"title":"Implement login","description":"do it","acceptanceCriteria":["Tests pass"]}],
             "risks":[{"description":"Scope creep","mitigation":"Cut scope"}],
             "finalAcceptanceCriteria":["All tests pass"]}
            """));

        var plan = await planner.CreatePlanAsync(Guid.NewGuid(), "Add login", Snapshot(Path.GetTempPath()), CancellationToken.None);
        Assert.Equal(PlanStatus.InReview, plan.Status);
        Assert.Equal("Add login", plan.Goal);
        var task = Assert.Single(plan.Tasks);
        Assert.Equal("Implement login", task.Title);
        Assert.Contains("Tests pass", task.AcceptanceCriteria);
        var question = Assert.Single(plan.OpenQuestions);
        Assert.Equal("OAuth or local?", question.Question);
        Assert.Single(plan.Risks);
        Assert.Equal(new[] { "All tests pass" }, plan.FinalAcceptanceCriteria);
    }

    [Fact]
    public async Task CreatePlan_ToleratesMarkdownFences()
    {
        var planner = Planner(new ScriptedProvider(
            "```json\n{\"goal\":\"G\",\"tasks\":[{\"title\":\"T\"}]}\n```"));
        var plan = await planner.CreatePlanAsync(Guid.NewGuid(), "G", Snapshot(Path.GetTempPath()), CancellationToken.None);
        Assert.Single(plan.Tasks);
    }

    [Fact]
    public async Task CreatePlan_FallsBack_WhenProviderFails()
    {
        var planner = Planner(new FailingProvider());
        var plan = await planner.CreatePlanAsync(Guid.NewGuid(), "Do something important here",
            Snapshot(Path.GetTempPath()), CancellationToken.None);
        Assert.Equal(PlanStatus.InReview, plan.Status); // plan heurístico, sin tareas
        Assert.Empty(plan.Tasks);
    }

    [Fact]
    public async Task CreatePlan_FallsBack_WhenModelReturnsNoTasks()
    {
        var planner = Planner(new ScriptedProvider("""{"goal":"G","tasks":[]}"""));
        var plan = await planner.CreatePlanAsync(Guid.NewGuid(), "A sufficiently long request for fallback",
            Snapshot(Path.GetTempPath()), CancellationToken.None);
        Assert.Empty(plan.Tasks);
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

    private sealed class ScriptedProvider : IAIProvider
    {
        private readonly string _content;
        public ScriptedProvider(string content) => _content = content;
        public string Id => "fake";
        public string DisplayName => "fake";
        public ProviderCapabilities Capabilities => ProviderCapabilities.StructuredOutput;
        public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct)
        {
            Assert.NotNull(request.StructuredOutputSchema); // el planner pide JSON
            return Task.FromResult(new AIResponse(_content, Array.Empty<AIToolCall>(), "m", new TokenUsage(1, 1)));
        }

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
