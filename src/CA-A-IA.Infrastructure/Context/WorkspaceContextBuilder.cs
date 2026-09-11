// CA-A-IA · Fase 0 — Constructor de contexto (§16).
// Vive en Infrastructure porque lee el sistema de ficheros; el contrato IContextBuilder
// es de Domain y el Agent lo consume sin conocer esta implementación.

using CaAIA.Domain.Context;
using CaAIA.Infrastructure.FileSystem;

namespace CaAIA.Infrastructure.Context;

/// <summary>
/// Descubre archivos relevantes, prioriza por hints y respeta el presupuesto de caracteres.
/// TODO(FUTURE_PHASE): ranking por relevancia y caché por hash (el resumido vive en el decorador).
/// </summary>
public sealed class WorkspaceContextBuilder : IContextBuilder
{
    private readonly WorkspaceReader _reader;

    public WorkspaceContextBuilder(WorkspaceReader reader)
    {
        _reader = reader;
    }

    public Task<ProjectContext> BuildAsync(
        string workspacePath, IReadOnlyList<string> hints, int maxChars, CancellationToken cancellationToken) =>
        _reader.ReadFragmentsAsync(workspacePath, hints, maxChars, cancellationToken);
}
