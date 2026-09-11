// CA-A-IA — Planner con modelo: descompone la petición en tareas verificables (JSON).
// Ante cualquier fallo (proveedor, parseo, validación) cae al HeuristicPlanner: siempre hay plan.

using System.Text.Json;
using CaAIA.Application.Configuration;
using CaAIA.Application.Services;
using CaAIA.Domain.AI;
using CaAIA.Domain.Execution;
using CaAIA.Domain.Planning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Agent.Planning;

/// <summary>
/// Genera el <c>Plan</c> en <c>Draft→InReview</c> pidiendo al modelo una descomposición
/// estructurada (<c>response_format: json_object</c>). Valida forma y límites; si algo falla,
/// delega en <see cref="HeuristicPlanner"/> en lugar de inventar tareas.
/// </summary>
public sealed class LlmPlanner : IPlanner
{
    private readonly IProviderRegistry _providers;
    private readonly IUserPreferences _selection;
    private readonly int _timeoutSeconds;
    private readonly HeuristicPlanner _fallback;
    private readonly ILogger<LlmPlanner> _log;

    public LlmPlanner(
        IProviderRegistry providers,
        IOptions<CaAIAOptions> options,
        IUserPreferences selection,
        HeuristicPlanner fallback,
        ILogger<LlmPlanner> log)
    {
        var ids = options.Value.Providers;
        _providers = providers;
        _selection = selection;
        _timeoutSeconds = ids.RequestTimeoutSeconds;
        _fallback = fallback;
        _log = log;
    }

    public async Task<Plan> CreatePlanAsync(
        Guid sessionId, string userRequest, ProjectContextSnapshot project, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userRequest))
        {
            throw new ArgumentException("User request is required.", nameof(userRequest));
        }

        if (string.IsNullOrWhiteSpace(_selection.ModelId))
        {
            _log.LogWarning("No model selected; using heuristic planner.");
            return await _fallback.CreatePlanAsync(sessionId, userRequest, project, ct).ConfigureAwait(false);
        }

        IAIProvider provider;
        try
        {
            provider = _providers.Get(_selection.ProviderId);
        }
        catch (KeyNotFoundException ex)
        {
            _log.LogWarning(ex, "Selected provider missing; using heuristic planner.");
            return await _fallback.CreatePlanAsync(sessionId, userRequest, project, ct).ConfigureAwait(false);
        }

        try
        {
            var response = await provider.CompleteAsync(new AIRequest
            {
                ModelId = _selection.ModelId,
                Messages = new[]
                {
                    new AIMessage(AIRole.System, PlannerPrompt),
                    new AIMessage(AIRole.User,
                        $"REQUEST: {userRequest}\nWORKSPACE: {project.WorkspacePath}\n" +
                        $"RELEVANT FILES:\n{string.Join("\n", project.RelevantFiles.Take(100))}\n" +
                        $"BUILD: {project.BuildSummary ?? "unknown"}\nGIT: {project.GitSummary ?? "unknown"}"),
                },
                Temperature = 0.2,
                ReasoningEffort = _selection.ReasoningEffort,
                StructuredOutputSchema = "plan",
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds),
                Correlation = new Domain.Correlation.CorrelationContext(
                    Guid.NewGuid(), sessionId, Guid.NewGuid()),
            }, ct).ConfigureAwait(false);

            return BuildPlan(sessionId, userRequest, response.Content);
        }
        catch (Exception ex) when (ex is AIProviderException or JsonException or InvalidOperationException)
        {
            _log.LogWarning(ex, "LLM planning failed; falling back to heuristic planner.");
            return await _fallback.CreatePlanAsync(sessionId, userRequest, project, ct).ConfigureAwait(false);
        }
    }

    internal Plan BuildPlan(Guid sessionId, string userRequest, string json)
    {
        using var doc = JsonDocument.Parse(ExtractJson(json));
        var root = doc.RootElement;

        var tasks = new List<AgentTask>();
        if (root.TryGetProperty("tasks", out var jt) && jt.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in jt.EnumerateArray())
            {
                var title = t.TryGetProperty("title", out var ti) ? ti.GetString() : null;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                tasks.Add(new AgentTask
                {
                    Title = title.Trim(),
                    Description = t.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                    AcceptanceCriteria = Strings(t, "acceptanceCriteria"),
                });
            }
        }

        if (tasks.Count == 0)
        {
            throw new InvalidOperationException("Model returned no usable tasks.");
        }

        if (tasks.Count > 50)
        {
            tasks = tasks.Take(50).ToList();
        }

        var plan = new Plan
        {
            SessionId = sessionId,
            Goal = Str(root, "goal") ?? userRequest.Trim(),
            Requirements = Strings(root, "requirements", fallback: new[] { userRequest.Trim() }),
            Assumptions = Strings(root, "assumptions"),
            Constraints = Strings(root, "constraints"),
            Risks = Risks(root),
            FinalAcceptanceCriteria = Strings(root, "finalAcceptanceCriteria"),
            Tasks = tasks,
        };
        plan.SetQuestions(Questions(root));
        plan.MarkInReview();
        _log.LogInformation("LLM plan {PlanId} created: {Tasks} tasks, {Questions} questions",
            plan.Id, tasks.Count, plan.OpenQuestions.Count);
        return plan;
    }

    private const string PlannerPrompt =
        """
        You are a senior software planner. Answer with ONE JSON object only (no markdown, no prose):
        {
          "goal": "one-sentence goal",
          "requirements": ["verifiable requirement", ...],
          "assumptions": [...],
          "constraints": [...],
          "questions": [{"question": "...", "context": "...", "options": ["a", "b"]}],
          "tasks": [{"title": "...", "description": "...", "acceptanceCriteria": ["..."]}],
          "risks": [{"description": "...", "mitigation": "..."}],
          "finalAcceptanceCriteria": ["..."]
        }
        Rules: tasks must be small, ordered, independently verifiable; every requirement covered by >=1 task;
        ask questions instead of guessing when information is missing; max 50 tasks.
        """;

    private static string ExtractJson(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var first = t.IndexOf('\n');
            var last = t.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && last > first)
            {
                t = t[(first + 1)..last].Trim();
            }
        }

        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        return start >= 0 && end > start ? t[start..(end + 1)] : t;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement el, string name, IReadOnlyList<string>? fallback = null)
    {
        if (el.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var list = arr.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            if (list.Count > 0)
            {
                return list;
            }
        }

        return fallback ?? Array.Empty<string>();
    }

    private static IReadOnlyList<Clarification> Questions(JsonElement root)
    {
        var list = new List<Clarification>();
        if (root.TryGetProperty("questions", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in arr.EnumerateArray())
            {
                var text = q.TryGetProperty("question", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                list.Add(new Clarification
                {
                    Question = text.Trim(),
                    Context = Str(q, "context"),
                    SuggestedOptions = Strings(q, "options"),
                });
            }
        }

        return list;
    }

    private static IReadOnlyList<PlanRisk> Risks(JsonElement root)
    {
        var list = new List<PlanRisk>();
        if (root.TryGetProperty("risks", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in arr.EnumerateArray())
            {
                var d = Str(r, "description");
                if (!string.IsNullOrWhiteSpace(d))
                {
                    list.Add(new PlanRisk(d.Trim(), Str(r, "mitigation") ?? string.Empty));
                }
            }
        }

        return list;
    }
}
