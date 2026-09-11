// CA-A-IA — Tests de secretos: Credential Manager real (clave única + limpieza) y compuesto.

using CaAIA.Domain.Security;
using CaAIA.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaAIA.Tests.Integration;

public sealed class SecretsTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task CredentialManager_Roundtrips_WithCleanup()
    {
        var store = new CredentialManagerSecretStore(NullLogger<CredentialManagerSecretStore>.Instance);
        var key = $"test-{Guid.NewGuid():N}";
        try
        {
            Assert.Null(await store.RetrieveAsync(key, CancellationToken.None));
            await store.StoreAsync(key, "s3cr3t-ñ", CancellationToken.None);
            Assert.Equal("s3cr3t-ñ", await store.RetrieveAsync(key, CancellationToken.None));
            await store.RemoveAsync(key, CancellationToken.None);
            Assert.Null(await store.RetrieveAsync(key, CancellationToken.None));
            await store.RemoveAsync(key, CancellationToken.None); // idempotente
        }
        finally
        {
            try { await store.RemoveAsync(key, CancellationToken.None); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task Fallback_ReadsSecondary_AndConsolidatesOnWrite()
    {
        var primary = new MemorySecrets();
        var secondary = new MemorySecrets();
        await secondary.StoreAsync("k", "old", CancellationToken.None);

        var store = new FallbackSecretStore(primary, secondary);
        Assert.Equal("old", await store.RetrieveAsync("k", CancellationToken.None));

        await store.StoreAsync("k", "new", CancellationToken.None);
        Assert.Equal("new", await store.RetrieveAsync("k", CancellationToken.None));
        Assert.Null(await secondary.RetrieveAsync("k", CancellationToken.None));

        await store.RemoveAsync("k", CancellationToken.None);
        Assert.Null(await store.RetrieveAsync("k", CancellationToken.None));
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _data = new();
        public Task StoreAsync(string key, string secret, CancellationToken ct)
        {
            _data[key] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> RetrieveAsync(string key, CancellationToken ct) =>
            Task.FromResult<string?>(_data.TryGetValue(key, out var v) ? v : null);

        public Task RemoveAsync(string key, CancellationToken ct)
        {
            _data.Remove(key);
            return Task.CompletedTask;
        }
    }
}
