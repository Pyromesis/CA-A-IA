// CA-A-IA — Auditoría final semántica (§9): el modelo juzga request vs plan vs
// implementación vs tests; la heurística mecánica aporta el suelo de cobertura.
// Veredicto = unión de gaps (conservador por diseño); ante fallo, solo heurística.

using System.Text.Json;
using CaAIA.Application.Configuration;
using CaAIA.Application.Services;
using CaAIA.Domain.AI;
using CaAIA.Domain.Execution;
using CaAIA.Domain.Planning;
using CaAIA.Agent.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Agent.Verification;

/// <summary>
/// Combina juicio semántico del modelo con cobertura mecánica: el modelo detecta criterios
/// satisfechos con otra redacción; la heurística impide que cuele un requisito sin tarea.
/// </summary>
public sealed class LlmPlanVerifier : IPlanVerifier
{
    private readonly IProviderRegistry _providers;
    private readonly IUserPreferences _selection;
    private readonly int _timeoutSeconds;
    private readonly FinalPlanAuditor _heuristic;
    private readonly ILogger<LlmPlanVerifier> _log;

    public LlmPlanVerifier(
        IProviderRegistry providers,
        IOptions<CaAIAOptions> options,
        IUserPreferences selection,
        FinalPlanAuditor heuristic,
        ILogger<LlmPlanVerifier> log)
    {
        var ids = options.Value.Providers;
        _providers = providers;
        _selection = selection;
        _timeoutSeconds = ids.RequestTimeoutSeconds;
        _heuristic = heuristic;
        _log = log;
    }

    public async Task<VerificationReport> AuditAsync(
        string userRequest, Plan plan, IReadOnlyList<string> testEvidence, CancellationToken ct)
    {
        var mechanical = await _heuristic.AuditAsync(userRequest, plan, testEvidence, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(_selection.ModelId))
        {
            return mechanical;
        }

        IReadOnlyList<string> semanticMissing;
        try
        {
            semanticMissing = await JudgeAsync(userRequest, plan, testEvidence, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AIProviderException or JsonException or InvalidOperationException
            or System.Collections.Generic.KeyNotFoundException)
        {
            _log.LogWarning(ex, "Semantic audit failed; keeping mechanical verdict.");
            return mechanical;
        }

        var missing = mechanical.MissingRequirements
            .Concat(semanticMissing)
            .Distinct()
            .ToList();
        var evidence = mechanical.Evidence
            .Concat(new[] { $"Semantic gaps: {semanticMissing.Count}" })
            .ToList();
        return mechanical with
        {
            IsSatisfied = missing.Count == 0,
            MissingRequirements = missing,
            Evidence = evidence,
        };
    }

    internal async Task<IReadOnlyList<string>> JudgeAsync(
        string userRequest, Plan plan, IReadOnlyList<string> testEvidence, CancellationToken ct)
    {
        var provider = string.IsNullOrWhiteSpace(_selection.ProviderId)
            ? _providers.GetAll().Select(p => p).FirstOrDefault()
                ?? throw new InvalidOperationException("No providers registered.")
            : _providers.Get(_selection.ProviderId);
        var tasks = string.Join("\n", plan.Tasks.Select(t =>
            $"- [{t.Status}] {t.Title}: {t.Description} (criteria: {string.Join("; ", t.AcceptanceCriteria)})"));
        var response = await provider.CompleteAsync(new AIRequest
        {
            ModelId = _selection.ModelId,
            Messages = new[]
            {
                new AIMessage(AIRole.System,
                    "You audit completed work. Reply with ONE JSON object only: " +
                    "{\"satisfied\": true|false, \"missingRequirements\": [\"...\"], \"notes\": \"...\"}. " +
                    "List every requirement or acceptance criterion NOT demonstrably satisfied; empty list if all good."),
                new AIMessage(AIRole.User,
                    $"REQUEST: {userRequest}\nGOAL: {plan.Goal}\n" +
                    $"REQUIREMENTS:\n{string.Join("\n", plan.Requirements.Select(r => "- " + r))}\n" +
                    $"FINAL CRITERIA:\n{string.Join("\n", plan.FinalAcceptanceCriteria.Select(c => "- " + c))}\n" +
                    $"TASKS:\n{tasks}\nTEST EVIDENCE:\n{string.Join("\n", testEvidence)}"),
            },
            Temperature = 0.1,
            ReasoningEffort = _selection.ReasoningEffort,
            StructuredOutputSchema = "audit",
            Timeout = TimeSpan.FromSeconds(_timeoutSeconds),
            Correlation = new Domain.Correlation.CorrelationContext(Guid.NewGuid(), plan.SessionId, Guid.NewGuid()),
        }, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(ExtractJson(response.Content));
        var root = doc.RootElement;
        if (root.TryGetProperty("missingRequirements", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            return arr.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        if (root.TryGetProperty("satisfied", out var s) && s.ValueKind == JsonValueKind.True)
        {
            return Array.Empty<string>();
        }

        throw new InvalidOperationException("Audit response has no usable verdict.");
    }

    private static string ExtractJson(string text)
    {
        var t = text.Trim();
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        return start >= 0 && end > start ? t[start..(end + 1)] : t;
    }
}
