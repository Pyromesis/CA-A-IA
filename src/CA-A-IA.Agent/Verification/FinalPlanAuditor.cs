// CA-A-IA · Fase 0 — Auditoría final heurística (§9): request vs plan vs implementación vs tests.
// Completar TODAS las tareas NO implica éxito: se verifica cobertura de requisitos + criterios.

using CaAIA.Domain.Execution;
using CaAIA.Domain.Planning;
using Microsoft.Extensions.Logging;

namespace CaAIA.Agent.Verification;

/// <summary>
/// Heurística de cobertura por palabras clave: cada requisito debe estar mencionado en alguna
/// tarea (título/descripción/criterios) y cada criterio final en la evidencia o en tareas.
/// Suelo mecánico de cobertura para <see cref="LlmPlanVerifier"/>.
/// TODO(FUTURE_PHASE): inspección de diffs y resultados de tests como evidencia.
/// </summary>
public sealed class FinalPlanAuditor : IPlanVerifier
{
    private readonly ILogger<FinalPlanAuditor> _log;

    public FinalPlanAuditor(ILogger<FinalPlanAuditor> log)
    {
        _log = log;
    }

    public Task<VerificationReport> AuditAsync(
        string userRequest, Plan plan, IReadOnlyList<string> testEvidence, CancellationToken cancellationToken)
    {
        var taskText = string.Join("\n", plan.Tasks.Select(t =>
            $"{t.Title}\n{t.Description}\n{string.Join(" ", t.AcceptanceCriteria)}"));
        var evidenceText = string.Join("\n", testEvidence);

        var missing = new List<string>();
        foreach (var requirement in plan.Requirements)
        {
            if (!Covers(taskText, requirement))
            {
                missing.Add($"Requirement not covered by any task: {requirement}");
            }
        }

        foreach (var criterion in plan.FinalAcceptanceCriteria)
        {
            if (!Covers(taskText, criterion) && !Covers(evidenceText, criterion))
            {
                missing.Add($"Acceptance criterion without evidence: {criterion}");
            }
        }

        var evidence = new List<string>
        {
            $"Tasks closed: {plan.Tasks.Count(t => t.Status is Domain.Enums.AgentTaskStatus.Completed or Domain.Enums.AgentTaskStatus.Skipped)}/{plan.Tasks.Count}",
            $"Plan revision: {plan.Revision}",
            $"Test evidence entries: {testEvidence.Count}",
        };

        var report = new VerificationReport(plan.Id, plan.Revision, missing.Count == 0, missing, evidence, DateTimeOffset.UtcNow);
        _log.LogInformation("Final audit of plan {PlanId} rev {Revision}: {Result} ({Missing} gaps)",
            plan.Id, plan.Revision, report.IsSatisfied ? "SATISFIED" : "GAPS", missing.Count);
        return Task.FromResult(report);
    }

    /// <summary>
    /// Filtra gaps ya abordados por tareas existentes (mismo texto normalizado en
    /// título/descripción): sin esto, un juez que repite la queja regenera la misma
    /// tarea ronda tras ronda eternamente. Devuelve solo los gaps NUEVOS.
    /// </summary>
    public static IReadOnlyList<string> FilterAlreadyAddressed(
        Plan plan, IReadOnlyList<string> gaps)
    {
        var addressed = plan.Tasks
            .SelectMany(t => new[] { t.Title, t.Description })
            .Select(NormalizeGap)
            .Where(s => s.Length >= 20)
            .ToList();
        if (addressed.Count == 0)
        {
            return gaps;
        }

        // Solo candidatos con chicha (≥20 chars): un fragmento diminuto matchearía
        // por casualidad. El gap en cambio puede ser corto: si aparece literal
        // dentro de una tarea existente, es duplicado con total seguridad.
        return gaps
            .Where(g => !addressed.Any(a => a.Contains(NormalizeGap(g), StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>Normaliza un gap para comparar: minúsculas, sin prefijos, espacios colapsados.</summary>
    internal static string NormalizeGap(string gap)
    {
        var s = (gap ?? string.Empty).ToLowerInvariant().Trim();
        while (s.StartsWith("audit gap:", StringComparison.Ordinal))
        {
            s = s["audit gap:".Length..].Trim();
        }

        s = s.TrimEnd('.', ',', ';', ':', '!', '?');
        return string.Join(" ", s.Split(
            new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Cobertura: ≥60% de las palabras significativas del requisito aparecen en el texto.</summary>
    internal static bool Covers(string haystack, string requirement)
    {
        var keywords = requirement
            .Split(new[] { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 3)
            .Distinct()
            .ToList();
        if (keywords.Count == 0)
        {
            return true;
        }

        var lower = haystack.ToLowerInvariant();
        var hits = keywords.Count(k => lower.Contains(k, StringComparison.Ordinal));
        return hits * 1.0 / keywords.Count >= 0.6;
    }
}
