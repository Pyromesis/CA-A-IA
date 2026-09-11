// CA-A-IA · Fase 0 — Planner heurístico (andamiaje honesto, sin fingir IA).
// Estructura la petición + detecta ambigüedades + deja el plan en revisión SIN tareas:
// la descomposición en tareas la hará el modelo en fases posteriores.

using CaAIA.Domain.Execution;
using CaAIA.Domain.Planning;
using Microsoft.Extensions.Logging;

namespace CaAIA.Agent.Planning;

/// <summary>
/// Genera un <see cref="Plan"/> en <c>Draft→InReview</c> con preguntas de aclaración cuando
/// falta información. Nunca inventa tareas: eso requiere al proveedor (fase posterior).
/// </summary>
public sealed class HeuristicPlanner : IPlanner
{
    private readonly ILogger<HeuristicPlanner> _log;

    public HeuristicPlanner(ILogger<HeuristicPlanner> log)
    {
        _log = log;
    }

    public Task<Plan> CreatePlanAsync(
        Guid sessionId, string userRequest, ProjectContextSnapshot project, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userRequest))
        {
            throw new ArgumentException("User request is required.", nameof(userRequest));
        }

        var questions = new List<Clarification>();
        if (userRequest.Trim().Length < 20)
        {
            questions.Add(new Clarification
            {
                Question = "The request is very short. What is the concrete goal and definition of done?",
                Context = userRequest,
                SuggestedOptions = Array.Empty<string>(),
            });
        }

        if (project.RelevantFiles.Count == 0)
        {
            questions.Add(new Clarification
            {
                Question = "No relevant project files were found. Which workspace path and entry point should be used?",
                Context = project.WorkspacePath,
            });
        }

        questions.Add(new Clarification
        {
            // TODO(FUTURE_PHASE): el modelo derivará criterios concretos; Fase 0 pide confirmación explícita.
            Question = "Which acceptance criteria must the final audit verify?",
            Context = userRequest,
        });

        var plan = new Plan
        {
            SessionId = sessionId,
            Goal = userRequest.Trim(),
            Requirements = new[] { userRequest.Trim() },
            Assumptions = new[] { $"Workspace: {project.WorkspacePath}" },
            FinalAcceptanceCriteria = Array.Empty<string>(),
            Tasks = new List<AgentTask>(),
        };
        plan.SetQuestions(questions);
        plan.MarkInReview();

        _log.LogInformation("Draft plan {PlanId} created for session {SessionId} with {Questions} open questions",
            plan.Id, sessionId, questions.Count);
        return Task.FromResult(plan);
    }
}
