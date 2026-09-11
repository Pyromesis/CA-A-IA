namespace CaAIA.Domain.Git;

/// <summary>Cambio detectado en el working tree.</summary>
public sealed record GitChange(string Path, string Status);

/// <summary>Estado del repositorio para una ruta de workspace.</summary>
public sealed record GitStatus(
    string WorkspacePath,
    string? Branch,
    bool HasChanges,
    IReadOnlyList<GitChange> Changes);

/// <summary>Entrada del historial (git log).</summary>
public sealed record GitCommit(string Hash, string ShortHash, string Author, DateTimeOffset Date, string Subject);

/// <summary>Rama local.</summary>
public sealed record GitBranch(string Name, bool IsCurrent);

/// <summary>
/// Abstracción Git (§21). El núcleo del agente nunca invoca `git` directamente;
/// la implementación con procesos vive en Infrastructure.
/// </summary>
public interface IGitService
{
    Task<bool> IsRepositoryAsync(string workspacePath, CancellationToken cancellationToken);
    Task<GitStatus> GetStatusAsync(string workspacePath, CancellationToken cancellationToken);
    Task<string> GetDiffAsync(string workspacePath, int maxChars, CancellationToken cancellationToken);
    Task<IReadOnlyList<GitCommit>> GetLogAsync(string workspacePath, int maxEntries, CancellationToken cancellationToken);

    /// <summary>
    /// Commit con mensaje. Solo staging ya preparado (`git commit`, sin `add -A` implícito):
    /// el agente decide qué entra vía herramientas futuras; el servicio no añade nada por su cuenta.
    /// </summary>
    Task<string> CommitAsync(string workspacePath, string message, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string workspacePath, CancellationToken cancellationToken);
    Task CreateBranchAsync(string workspacePath, string name, CancellationToken cancellationToken);

    /// <summary>Cambia de rama. Se niega con working tree sucio (el agente debe guardar/stash antes).</summary>
    Task CheckoutAsync(string workspacePath, string name, CancellationToken cancellationToken);
}
