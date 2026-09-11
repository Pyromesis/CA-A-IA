// CA-A-IA · Fase 0 — Secretos con DPAPI (CurrentUser). Las API keys NUNCA van a disco en claro.

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using CaAIA.Domain.Security;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.Security;

/// <summary>
/// Almacena secretos cifrados con DPAPI (<c>CurrentUser</c>) en <c>%LocalAppData%/CA-A-IA/secrets.dat</c>.
/// Solo la cuenta de usuario de Windows puede descifrarlos. Formato: diccionario JSON cifrado.
/// Secundario de <see cref="FallbackSecretStore"/>: conserva los secretos de la era del
/// fichero hasta su consolidación en Credential Manager.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<DpapiSecretStore> _log;

    public DpapiSecretStore(string dataPath, ILogger<DpapiSecretStore> log)
    {
        _filePath = Path.Combine(dataPath, "secrets.dat");
        _log = log;
    }

    public async Task StoreAsync(string key, string secret, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = ReadAllUnprotected_NoLock();
            all[key] = secret;
            var json = System.Text.Json.JsonSerializer.Serialize(all);
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await File.WriteAllBytesAsync(_filePath, protectedBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadAllUnprotected_NoLock().TryGetValue(key, out var v) ? v : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = ReadAllUnprotected_NoLock();
            if (all.Remove(key))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(all);
                var protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
                await File.WriteAllBytesAsync(_filePath, protectedBytes, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, string> ReadAllUnprotected_NoLock()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var protectedBytes = File.ReadAllBytes(_filePath);
            var json = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException or IOException)
        {
            _log.LogWarning(ex, "Secret store unreadable; starting empty (file preserved).");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Secret key is required.", nameof(key));
        }
    }
}
