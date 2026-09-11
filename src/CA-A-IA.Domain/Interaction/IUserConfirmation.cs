// CA-A-IA — Confirmación del usuario ante operaciones sensibles (§15).
// El motor la pide cuando PermissionDecision.RequiresUserConfirmation; en headless se deniega.

namespace CaAIA.Domain.Interaction;

/// <summary>
/// Puerta humana para escrituras/ejecuciones sensibles. Implementaciones: diálogo WinUI
/// (Presentation) y denegación automática (Infrastructure, tests/headless).
/// </summary>
public interface IUserConfirmation
{
    /// <summary>Devuelve true si el usuario autoriza. Un token cancelado equivale a denegar.</summary>
    Task<bool> RequestAsync(string title, string details, CancellationToken cancellationToken);
}
