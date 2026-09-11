// CA-A-IA · Fase 0 — Composition del Agent (máquina de estados + motor + planner + verifier).

using CaAIA.Agent.Execution;
using CaAIA.Agent.Memory;
using CaAIA.Agent.Planning;
using CaAIA.Agent.Policies;
using CaAIA.Agent.Verification;
using CaAIA.Application.Services;
using CaAIA.Domain.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace CaAIA.Agent;

/// <summary>Registro DI del Agent. No registra ni consume implementaciones de Infrastructure.</summary>
public static class AgentServiceExtensions
{
    public static IServiceCollection AddAgent(this IServiceCollection services)
    {
        services.AddSingleton<Planning.HeuristicPlanner>();
        services.AddSingleton<IPlanner, Planning.LlmPlanner>();
        services.AddSingleton<Verification.FinalPlanAuditor>();
        services.AddSingleton<IPlanVerifier, Verification.LlmPlanVerifier>();
        services.AddSingleton<IRepairPolicy, DefaultRepairPolicy>();
        services.AddSingleton<ITaskExecutor, Execution.LlmTaskExecutor>();
        services.AddSingleton<AgentMemory>();
        services.AddSingleton<IAgentEngineFactory, AgentEngineFactory>();
        return services;
    }
}
