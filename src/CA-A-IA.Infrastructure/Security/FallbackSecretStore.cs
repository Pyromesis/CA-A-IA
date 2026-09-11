// CA-A-IA — Compuesto de secretos: primario + secundario (migración sin perder nada).
// Lee del primario y cae al secundario; escribe en el primario y limpia el secundario.

using CaAIA.Domain.Security;

namespace CaAIA.Infrastructure.Security;

/// <summary>
/// <c>CredentialManager</c> como primario y <c>DPAPI</c> como secundario: los secretos de la
/// era del fichero siguen legibles y se consolidan en la bóveda al reescribirse.
/// </summary>
public sealed class FallbackSecretStore : ISecretStore
{
    private readonly ISecretStore _primary;
    private readonly ISecretStore _secondary;

    public FallbackSecretStore(ISecretStore primary, ISecretStore secondary)
    {
        _primary = primary;
        _secondary = secondary;
    }

    public async Task StoreAsync(string key, string secret, CancellationToken cancellationToken)
    {
        await _primary.StoreAsync(key, secret, cancellationToken).ConfigureAwait(false);
        try
        {
            await _secondary.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Limpieza best-effort del secundario: el primario ya guarda la verdad.
        }
    }

    public async Task<string?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        var value = await _primary.RetrieveAsync(key, cancellationToken).ConfigureAwait(false);
        if (value is not null)
        {
            return value;
        }

        return await _secondary.RetrieveAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        await _primary.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        try
        {
            await _secondary.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
