// CA-A-IA — Resumido de contexto con modelo (§16): cuando el proyecto excede el presupuesto,
// los fragmentos de menor prioridad se condensan en un resumen en lugar de perderse.
// Sin modelo configurado o ante fallo: truncado clásico. Nunca inventa contenido propio.

using CaAIA.Application.Configuration;
using CaAIA.Domain.AI;
using CaAIA.Domain.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Infrastructure.Context;

/// <summary>
/// Decora a <see cref="WorkspaceContextBuilder"/>: conserva los fragmentos prioritarios hasta
/// el 60% del presupuesto y resume el resto con el modelo en un único fragmento `summary`.
/// </summary>
public sealed class SummarizingContextBuilder : IContextBuilder
{
    private readonly IContextBuilder _inner;
    private readonly IProviderRegistry _providers;
    private readonly string _providerId;
    private readonly string _modelId;
    private readonly ILogger<SummarizingContextBuilder> _log;

    public SummarizingContextBuilder(
        IContextBuilder inner,
        IProviderRegistry providers,
        IOptions<CaAIAOptions> options,
        ILogger<SummarizingContextBuilder> log)
    {
        _inner = inner;
        _providers = providers;
        _providerId = options.Value.Providers.DefaultProviderId;
        _modelId = options.Value.Providers.DefaultModelId;
        _log = log;
    }

    public async Task<ProjectContext> BuildAsync(
        string workspacePath, IReadOnlyList<string> hints, int maxChars, CancellationToken ct)
    {
        var full = await _inner.BuildAsync(workspacePath, hints, maxChars * 3, ct).ConfigureAwait(false);
        if (full.TotalChars <= maxChars)
        {
            return full;
        }

        var keepBudget = (int)(maxChars * 0.6);
        var ordered = full.Fragments.OrderByDescending(f => f.Priority).ToList();
        var kept = new List<ContextFragment>();
        var keptChars = 0;
        var dropped = new List<ContextFragment>();
        foreach (var fragment in ordered)
        {
            if (keptChars + fragment.Content.Length <= keepBudget)
            {
                kept.Add(fragment);
                keptChars += fragment.Content.Length;
            }
            else
            {
                dropped.Add(fragment);
            }
        }

        if (dropped.Count == 0)
        {
            return full with { Fragments = kept, TotalChars = keptChars, TruncatedFragments = 0 };
        }

        var summary = await TrySummarizeAsync(dropped, maxChars - keptChars, ct).ConfigureAwait(false);
        if (summary is null)
        {
            return new ProjectContext(workspacePath, kept, keptChars, full.TruncatedFragments + dropped.Count);
        }

        var fragments = kept.Concat(new[] { summary }).ToList();
        return new ProjectContext(workspacePath, fragments,
            keptChars + summary.Content.Length, full.TruncatedFragments);
    }

    private async Task<ContextFragment?> TrySummarizeAsync(
        IReadOnlyList<ContextFragment> dropped, int budget, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_modelId) || budget < 500)
        {
            return null;
        }

        IAIProvider provider;
        try
        {
            provider = _providers.Get(_providerId);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }

        try
        {
            var input = string.Join("\n\n", dropped.Take(30).Select(f =>
                $"=== {f.Source} ===\n{(f.Content.Length > 2000 ? f.Content[..2000] + "\n…[cut]" : f.Content)}"));
            var response = await provider.CompleteAsync(new AIRequest
            {
                ModelId = _modelId,
                Messages = new[]
                {
                    new AIMessage(AIRole.System,
                        "Summarize these code excerpts for a coding agent that cannot see them. " +
                        "Keep file paths, public symbols, and anything needed to navigate the codebase. " +
                        $"Max {Math.Max(200, budget)} characters, plain text, no markdown fences."),
                    new AIMessage(AIRole.User, input),
                },
                Temperature = 0.1,
                Timeout = TimeSpan.FromSeconds(90),
                Correlation = Domain.Correlation.CorrelationContext.Create(Guid.Empty, Guid.Empty),
            }, ct).ConfigureAwait(false);
            var text = response.Content.Length > budget
                ? response.Content[..budget] + "\n…[truncated]"
                : response.Content;
            return new ContextFragment("summary",
                $"{dropped.Count} lower-priority files (model summary)", text, Priority: 0);
        }
        catch (Exception ex) when (ex is AIProviderException or InvalidOperationException
            or System.Collections.Generic.KeyNotFoundException)
        {
            _log.LogWarning(ex, "Context summarization failed; truncating.");
            return null;
        }
    }
}
