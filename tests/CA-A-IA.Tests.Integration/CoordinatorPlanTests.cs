// CA-A-IA — Tests del flujo coordinado: sesión → plan (fallback heurístico sin modelo).

using CaAIA.Agent;
using CaAIA.Application;
using CaAIA.Application.Services;
using CaAIA.Domain.Enums;
using CaAIA.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaAIA.Tests.Integration;

public sealed class CoordinatorPlanTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"ca-a-ia-cp-{Guid.NewGuid():N}");
    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public async Task SendFlow_CreatesSession_PlanWithQuestions_NoModel()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaAIA:Persistence:DataPath"] = _dataPath,
                ["CaAIA:Providers:EnabledProviders:0"] = "opencode",
                ["CaAIA:Providers:DefaultModelId"] = "",
                ["Logging:LogLevel:Default"] = "Warning",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication(config);
        services.AddInfrastructure(config);
        services.AddAgent();
        await using var provider = services.BuildServiceProvider();
        await Infrastructure.InfrastructureServiceExtensions
            .RunStartupTasksAsync(provider, CancellationToken.None);

        var coordinator = provider.GetRequiredService<ISessionCoordinator>();
        var session = await coordinator.StartSessionAsync(_dataPath, "Do something important here", CancellationToken.None);
        var plan = await coordinator.CreatePlanAsync(session.Id, "Do something important here", CancellationToken.None);

        Assert.Equal(PlanStatus.InReview, plan.Status);
        Assert.Empty(plan.Tasks); // heurístico: estructura sin tareas inventadas
        Assert.NotEmpty(plan.OpenQuestions);

        var stored = await coordinator.GetPlanAsync(session.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(plan.Id, stored.Id);

        // Responder bloquea-aprobación hasta contestar; con tareas vacías no aprobable.
        var first = plan.OpenQuestions[0];
        var answered = await coordinator.AnswerQuestionAsync(plan.Id, first.Id, "criterio: compila", CancellationToken.None);
        Assert.True(answered.OpenQuestions.First(q => q.Id == first.Id).IsAnswered);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ApprovePlanAsync(plan.Id, CancellationToken.None));
    }
}
