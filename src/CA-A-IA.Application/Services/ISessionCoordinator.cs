// CA-A-IA — Fachada de orquestación que consumirán los ViewModels.

using CaAIA.Application.Configuration;
using CaAIA.Application.DTOs;
using CaAIA.Domain.Context;
using CaAIA.Domain.Execution;
using CaAIA.Domain.Git;
using CaAIA.Domain.Persistence;
using Microsoft.Extensions.Options;

namespace CaAIA.Application.Services;

/// <summary>
/// Factoría de motores de ejecución (uno por sesión). La implementa <c>CaAIA.Agent</c>;
/// Application solo declara el contrato para no invertir la dependencia.
/// </summary>
public interface IAgentEngineFactory
{
    IAgentExecutionEngine GetOrCreate(Guid sessionId);

    /// <summary>
    /// Descarta el motor actual y crea uno nuevo en <c>Paused</c>, listo para
    /// <c>ResumeAsync</c>. El CTS interno no es reutilizable tras pausar, así que
    /// reanudar sobre el mismo motor dejaba el próximo Run cancelado de inmediato.
    /// </summary>
    IAgentExecutionEngine Recreate(Guid sessionId);
    bool Remove(Guid sessionId);
}

/// <summary>
/// Coordinador de sesión: punto único de entrada de Presentation hacia
/// casos de uso + motor del agente. Toda operación larga acepta CancellationToken.
/// </summary>
public interface ISessionCoordinator
{
    Task<SessionDto> StartSessionAsync(string workspacePath, string userRequest, CancellationToken cancellationToken);
    Task<PlanDto> CreatePlanAsync(Guid sessionId, string userRequest, CancellationToken cancellationToken);
    Task<PlanDto?> GetPlanAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<PlanDto> ApprovePlanAsync(Guid planId, CancellationToken cancellationToken);
    Task<PlanDto> AnswerQuestionAsync(Guid planId, Guid questionId, string answer, CancellationToken cancellationToken);
    Task RunAsync(Guid sessionId, CancellationToken cancellationToken);
    Task PauseAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// "Seguir" sin finalizar: reencola lo a medias con la indicación del usuario
    /// y deja un motor fresco listo para continuar. Devuelve tareas reencoladas.
    /// </summary>
    Task<int> NudgeAsync(Guid sessionId, string instruction, CancellationToken cancellationToken);
    Task ResumeAsync(Guid sessionId, CancellationToken cancellationToken);
    Task CancelAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SessionDto>> ListActiveSessionsAsync(CancellationToken cancellationToken);
}
