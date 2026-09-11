// CA-A-IA · Fase 0 — Tests de composición DI + persistencia recuperable entre reinicios.

using CaAIA.Agent;
using CaAIA.Application;
using CaAIA.Application.Services;
using CaAIA.Domain.AI;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Persistence;
using CaAIA.Domain.Tools;
using CaAIA.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaAIA.Tests.Integration;

public sealed class CompositionTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(
        Path.GetTempPath(), $"ca-a-ia-it-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch (Exception) { }
    }

    private ServiceProvider Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaAIA:Persistence:DataPath"] = _dataPath,
                ["CaAIA:Providers:EnabledProviders:0"] = "opencode",
                ["CaAIA:Providers:EnabledProviders:1"] = "opencode-zen",
                ["CaAIA:Providers:EnabledProviders:2"] = "openrouter",
                ["CaAIA:Execution:GlobalTimeoutMinutes"] = "1",
                ["CaAIA:Execution:HeartbeatSeconds"] = "5",
                ["Logging:LogLevel:Default"] = "Warning",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(); // espejo de App: logging antes que el resto de capas
        services.AddApplication(config);
        services.AddInfrastructure(config);
        services.AddAgent();
        var provider = services.BuildServiceProvider();
        InfrastructureServiceExtensions.RunStartupTasksAsync(provider, CancellationToken.None)
            .GetAwaiter().GetResult();
        return provider;
    }

    [Fact]
    public async Task Container_ResolvesCoreGraph_AndRegistersAdapters()
    {
        await using var provider = Build();
        var registry = provider.GetRequiredService<IProviderRegistry>();
        var ids = registry.GetAll().Select(p => p.Id).ToList();
        Assert.Contains("opencode", ids);
        Assert.Contains("openrouter", ids);

        var tools = provider.GetRequiredService<IToolRegistry>();
        Assert.Equal(17, tools.ListDefinitions().Count); // Read, List, Write, Edit, Execute, Search×2 + UI×10
        Assert.Contains(tools.ListDefinitions(), d => d.RequiredPermissions.HasFlag(ToolPermission.ProcessControl));

        // OpenCode local: sin binario, indisponible con mensaje accionable (no vacío mudo);
        // con binario (p. ej. CLI instalado), responde al --version sin levantar servidor.
        if (Infrastructure.Providers.OpenCode.ProcessServerLifecycle.FindBinary("opencode") is null)
        {
            Assert.False(await registry.Get("opencode").CheckHealthAsync(CancellationToken.None));
            var missing = await Assert.ThrowsAsync<NotSupportedException>(
                () => registry.Get("opencode").GetModelsAsync(CancellationToken.None));
            Assert.Contains("opencode-zen", missing.Message);
        }
        else
        {
            Assert.True(await registry.Get("opencode").CheckHealthAsync(CancellationToken.None));
        }

        // OpenCode Zen y resto resuelven (sin llamadas de red en tests: la cobertura HTTP
        // vive en Unit con handler simulado).
        Assert.NotNull(registry.Get("opencode-zen"));
        Assert.NotNull(registry.Get("openrouter"));
        Assert.NotNull(provider.GetRequiredService<ISessionCoordinator>());
    }

    [Fact]
    public async Task SessionLifecycle_CreatePauseResumeCancel_Persists()
    {
        await using var provider = Build();
        var coordinator = provider.GetRequiredService<ISessionCoordinator>();

        var session = await coordinator.StartSessionAsync(_dataPath, "Test request", CancellationToken.None);
        Assert.True(session.IsActive);

        var active = await coordinator.ListActiveSessionsAsync(CancellationToken.None);
        Assert.Single(active);

        await coordinator.PauseAsync(session.Id, CancellationToken.None);
        var paused = (await coordinator.ListActiveSessionsAsync(CancellationToken.None)).Single();
        Assert.Equal(Domain.Enums.AgentState.Paused, paused.LastKnownState);

        await coordinator.ResumeAsync(session.Id, CancellationToken.None);
        await coordinator.CancelAsync(session.Id, CancellationToken.None);

        var after = await coordinator.ListActiveSessionsAsync(CancellationToken.None);
        Assert.Empty(after);
    }

    [Fact]
    public async Task Stores_SurviveContainerRebuild_SameDataPath()
    {
        Guid sessionId;
        await using (var provider = Build())
        {
            var coordinator = provider.GetRequiredService<ISessionCoordinator>();
            var session = await coordinator.StartSessionAsync(_dataPath, "Recover me", CancellationToken.None);
            sessionId = session.Id;
        }

        // "Reinicio de la app": nuevo contenedor, mismo DataPath.
        await using (var provider2 = Build())
        {
            var sessions = provider2.GetRequiredService<Domain.Persistence.IAgentSessionStore>();
            var recovered = await sessions.LoadAsync(sessionId, CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal("Recover me", recovered.UserRequest);

            var checkpoints = provider2.GetRequiredService<ICheckpointStore>();
            var log = await checkpoints.ReadExecutionLogAsync(sessionId, CancellationToken.None);
            Assert.NotEmpty(log);
        }
    }
}
