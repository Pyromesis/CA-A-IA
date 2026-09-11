// CA-A-IA — Confirmación headless: deniega por defecto (tests, servicios sin UI).
// La UI real registra DialogConfirmation (Presentation).

using CaAIA.Domain.Interaction;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.Security;

/// <summary>
/// Sin usuario delante no hay confirmación posible: deniega y lo registra.
/// El ejecutor convierte la denegación en PermissionFailure (escalado, sin reintentos).
/// </summary>
public sealed class DenyAllConfirmation : IUserConfirmation
{
    private readonly ILogger<DenyAllConfirmation> _log;

    public DenyAllConfirmation(ILogger<DenyAllConfirmation> log)
    {
        _log = log;
    }

    public Task<bool> RequestAsync(string title, string details, CancellationToken cancellationToken)
    {
        _log.LogWarning("Confirmation denied (headless): {Title}", title);
        return Task.FromResult(false);
    }
}
