// CA-A-IA — Tests de UserPreferences (defaults, persistencia, restauración).

using CaAIA.Application.Configuration;
using CaAIA.Application.Services;
using CaAIA.Domain.Persistence;
using CaAIA.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaAIA.Tests.Unit;

public sealed class UserPreferencesTests
{
    private static UserPreferences Create(
        MemorySettingsStore store, string workspace = "", string provider = "p", string model = "m") =>
        new(Options.Create(new CaAIAOptions
        {
            Providers = new ProviderSettings { DefaultProviderId = provider, DefaultModelId = model },
        }), store, NullLogger<UserPreferences>.Instance);

    [Fact]
    public async Task Initialize_RestoresPersistedValues()
    {
        var store = new MemorySettingsStore();
        await store.SetAsync(UserPreferences.WorkspaceKey, Path.GetTempPath(), CancellationToken.None);
        await store.SetAsync(UserPreferences.ProviderKey, "openrouter", CancellationToken.None);
        await store.SetAsync(UserPreferences.ModelKey, "x", CancellationToken.None);

        var prefs = Create(store);
        await prefs.InitializeAsync(CancellationToken.None);
        Assert.Equal(Path.GetTempPath().TrimEnd('\\'), prefs.WorkspacePath.TrimEnd('\\'));
        Assert.Equal("openrouter", prefs.ProviderId);
        Assert.Equal("x", prefs.ModelId);
    }

    [Fact]
    public async Task Set_Persists_AndRaisesChanged()
    {
        var store = new MemorySettingsStore();
        var prefs = Create(store);
        await prefs.InitializeAsync(CancellationToken.None);
        var raised = 0;
        prefs.Changed += (_, _) => raised++;

        prefs.SetModel("local", "m2");
        Assert.Equal("local", prefs.ProviderId);
        Assert.Equal(1, raised);
        await prefs.FlushAsync(CancellationToken.None); // sin esperas arbitrarias
        Assert.Equal("local", await store.GetAsync(UserPreferences.ProviderKey, CancellationToken.None));
        Assert.Equal("m2", await store.GetAsync(UserPreferences.ModelKey, CancellationToken.None));
    }

    [Fact]
    public async Task ShowFreeOnly_Roundtrips_ThroughStore()
    {
        var store = new MemorySettingsStore();
        var prefs = Create(store);
        await prefs.InitializeAsync(CancellationToken.None);
        Assert.False(prefs.ShowFreeOnly);

        prefs.SetShowFreeOnly(true);
        await prefs.FlushAsync(CancellationToken.None);
        Assert.Equal("1", await store.GetAsync(UserPreferences.FreeOnlyKey, CancellationToken.None));

        var reloaded = Create(store);
        await reloaded.InitializeAsync(CancellationToken.None);
        Assert.True(reloaded.ShowFreeOnly);
    }

    [Fact]
    public async Task TeamModels_Roundtrip_ThroughStore()
    {
        var store = new MemorySettingsStore();
        var prefs = Create(store);
        await prefs.InitializeAsync(CancellationToken.None);
        Assert.Equal(string.Empty, prefs.AutonomyActorModel);
        Assert.Equal(string.Empty, prefs.AutonomyAnalystModel);

        prefs.SetAutonomyActor("zen", "flash-free");
        prefs.SetAutonomyAnalyst("router", "vision-1");
        await prefs.FlushAsync(CancellationToken.None);

        var reloaded = Create(store);
        await reloaded.InitializeAsync(CancellationToken.None);
        Assert.Equal("zen", reloaded.AutonomyActorProvider);
        Assert.Equal("flash-free", reloaded.AutonomyActorModel);
        Assert.Equal("router", reloaded.AutonomyAnalystProvider);
        Assert.Equal("vision-1", reloaded.AutonomyAnalystModel);
    }

    [Fact]
    public void Workspace_DefaultsToDocuments_WhenPresent()
    {
        var prefs = Create(new MemorySettingsStore());
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var expected = Directory.Exists(documents) ? documents : Environment.CurrentDirectory;
        Assert.Equal(expected, prefs.WorkspacePath);
    }

    [Fact]
    public async Task Effort_SetRestore_Persists()
    {
        var store = new MemorySettingsStore();
        await store.SetAsync(UserPreferences.EffortKey, "high", CancellationToken.None);

        var prefs = Create(store);
        Assert.Equal(string.Empty, prefs.ReasoningEffort);
        await prefs.InitializeAsync(CancellationToken.None);
        Assert.Equal("high", prefs.ReasoningEffort);

        prefs.SetReasoningEffort("XHIGH");
        Assert.Equal("xhigh", prefs.ReasoningEffort);
        await Task.Delay(200);
        Assert.Equal("xhigh", await store.GetAsync(UserPreferences.EffortKey, CancellationToken.None));
    }

    [Fact]
    public async Task AuthLevel_DefaultsToConfirmChanges_SetRestore_Persists()
    {
        var store = new MemorySettingsStore();
        await store.SetAsync(UserPreferences.AuthLevelKey, "3", CancellationToken.None);

        var prefs = Create(store);
        Assert.Equal(AuthorizationLevel.ConfirmChanges, prefs.AuthorizationLevel);
        await prefs.InitializeAsync(CancellationToken.None);
        Assert.Equal(AuthorizationLevel.FullControl, prefs.AuthorizationLevel);

        prefs.SetAuthorizationLevel(AuthorizationLevel.EditWithoutPcControl);
        Assert.Equal(AuthorizationLevel.EditWithoutPcControl, prefs.AuthorizationLevel);
        await Task.Delay(200);
        Assert.Equal("2", await store.GetAsync(UserPreferences.AuthLevelKey, CancellationToken.None));

        prefs.SetAuthorizationLevel((AuthorizationLevel)99); // inválido → lo más seguro
        Assert.Equal(AuthorizationLevel.ConfirmChanges, prefs.AuthorizationLevel);
    }

    private sealed class MemorySettingsStore : ISettingsStore    {
        private readonly Dictionary<string, string> _data = new();
        public Task<string?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult<string?>(_data.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct)
        {
            _data[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct)
        {
            _data.Remove(key);
            return Task.CompletedTask;
        }
    }
}
