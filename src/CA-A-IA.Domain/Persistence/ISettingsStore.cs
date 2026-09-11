// CA-A-IA — Ajustes de usuario persistentes (clave/valor): workspace, modelo, etc.

namespace CaAIA.Domain.Persistence;

/// <summary>Pares clave/valor para preferencias de usuario. Implementación en Infrastructure.</summary>
public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
    Task RemoveAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Mensaje de chat persistido (historial de conversación).</summary>
public sealed record StoredChatMessage(
    long Id,
    string? SessionId,
    string Role,
    string Text,
    DateTimeOffset At);

/// <summary>Historial de conversación del chat, en orden cronológico. Implementación en Infrastructure.</summary>
public interface IChatMessageStore
{
    Task<IReadOnlyList<StoredChatMessage>> ListRecentAsync(int limit, CancellationToken cancellationToken);
    Task AppendAsync(string? sessionId, string role, string text, DateTimeOffset at, CancellationToken cancellationToken);
}
