using CaAIA.Domain.Enums;

namespace CaAIA.Domain.Context;

/// <summary>Fragmento de contexto priorizado para el prompt (archivo, diff, build...).</summary>
public sealed record ContextFragment(string Kind, string Source, string Content, int Priority);

/// <summary>Contexto agregado del proyecto/workspace para una ejecución.</summary>
public sealed record ProjectContext(
    string WorkspacePath,
    IReadOnlyList<ContextFragment> Fragments,
    int TotalChars,
    int TruncatedFragments);

/// <summary>
/// Estrategia de contexto (§16): descubrir, seleccionar, resumir y priorizar sin
/// inundar el prompt. Implementaciones en Infrastructure (lectura + resumido con modelo).
/// TODO(FUTURE_PHASE): caché por hash.
/// </summary>
public interface IContextBuilder
{
    Task<ProjectContext> BuildAsync(
        string workspacePath,
        IReadOnlyList<string> hints,
        int maxChars,
        CancellationToken cancellationToken);
}
