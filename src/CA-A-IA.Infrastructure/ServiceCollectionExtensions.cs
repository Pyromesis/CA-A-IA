// CA-A-IA · Fase 0 — Composition de Infrastructure (adaptadores + stores + herramientas).

using CaAIA.Application.Configuration;
using CaAIA.Domain.AI;
using CaAIA.Domain.Events;
using CaAIA.Domain.Git;
using CaAIA.Domain.Memory;
using CaAIA.Domain.Persistence;
using CaAIA.Domain.Security;
using CaAIA.Domain.Tools;
using CaAIA.Infrastructure.AI;
using CaAIA.Infrastructure.Events;
using CaAIA.Infrastructure.FileSystem;
using CaAIA.Infrastructure.Git;
using CaAIA.Infrastructure.Memory;
using CaAIA.Infrastructure.Persistence;
using CaAIA.Infrastructure.Providers.Local;
using CaAIA.Infrastructure.Providers.OpenCode;
using CaAIA.Infrastructure.Providers.OpenRouter;
using CaAIA.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure;

/// <summary>
/// Registra todas las implementaciones de Infrastructure. Los adaptadores de proveedor se
/// auto-registran en <see cref="IProviderRegistry"/> según <c>CaAIA:Providers:EnabledProviders</c>.
/// </summary>
public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenCodeOptions>(configuration.GetSection("CaAIA:Providers:OpenCode"));
        services.Configure<OpenRouterOptions>(configuration.GetSection("CaAIA:Providers:OpenRouter"));
        services.Configure<LocalModelOptions>(configuration.GetSection("CaAIA:Providers:Local"));
        services.Configure<Providers.Zen.OpenCodeZenOptions>(configuration.GetSection("CaAIA:Providers:OpenCodeZen"));

        // Núcleo
        services.AddSingleton<Events.InMemoryEventBus>();
        services.AddSingleton<IEventBus>(sp => new Events.RecordingEventBus(
            sp.GetRequiredService<Events.InMemoryEventBus>(),
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<ILogger<Events.RecordingEventBus>>()));
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton<IToolRegistry, Tools.ToolRegistry>();
        services.AddSingleton<IToolPermissionService, ToolPermissionService>();
        services.AddSingleton<IMemoryStore, Persistence.Sqlite.SqliteMemoryStore>();

        // Utilidades
        services.AddSingleton<Process.ProcessRunner>();
        services.AddSingleton<Domain.Interaction.IUiAutomation, Process.UiAutomation>();
        services.AddSingleton<Domain.Process.ICommandRunner, Process.CommandRunnerAdapter>();
        services.AddSingleton<WorkspaceReader>();
        services.AddSingleton<IGitService, ProcessGitService>();
        services.AddSingleton<Context.WorkspaceContextBuilder>();
        services.AddSingleton<Domain.Context.IContextBuilder>(sp =>
            new Context.SummarizingContextBuilder(
                sp.GetRequiredService<Context.WorkspaceContextBuilder>(),
                sp.GetRequiredService<IProviderRegistry>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CaAIAOptions>>(),
                sp.GetRequiredService<ILogger<Context.SummarizingContextBuilder>>()));

        // Confirmación headless (la UI real la sustituye por diálogo; ver Presentation).
        services.AddSingleton<Domain.Interaction.IUserConfirmation, Security.DenyAllConfirmation>();

        // Persistencia SQLite (un fichero WAL; ver docs/PersistenceArchitecture.md)
        services.AddSingleton<Persistence.Sqlite.SqliteConnectionFactory>();
        services.AddSingleton<IPlanStore, Persistence.Sqlite.SqlitePlanStore>();
        services.AddSingleton<IAgentSessionStore, Persistence.Sqlite.SqliteAgentSessionStore>();
        services.AddSingleton<ICheckpointStore, Persistence.Sqlite.SqliteCheckpointStore>();
        services.AddSingleton<ISettingsStore, Persistence.Sqlite.SqliteSettingsStore>();
        services.AddSingleton<IChatMessageStore, Persistence.Sqlite.SqliteChatMessageStore>();
        services.AddSingleton<IMemoryStore, Persistence.Sqlite.SqliteMemoryStore>();
        services.AddSingleton<IEventStore, Persistence.Sqlite.SqliteEventStore>();
        services.AddSingleton<Persistence.Migration.LegacyJsonImporter>();
        services.AddSingleton<IStartupTask, MigrationTask>();
        services.AddSingleton<IStartupTask, PreferencesRestoreTask>();

        // Secretos (DPAPI CurrentUser). CA-A-IA es 100% Windows por requisito (§2 del prompt).
        if (OperatingSystem.IsWindows())
        {
            RegisterSecretStore(services);
        }
        else
        {
            throw new PlatformNotSupportedException("CA-A-IA requires Windows (DPAPI secret store).");
        }

        // Herramientas (el ejecutor las autoriza vía IToolPermissionService + scope)
        services.AddSingleton<ITool, Tools.ReadFileTool>();
        services.AddSingleton<ITool, Tools.ListDirectoryTool>();
        services.AddSingleton<ITool, Tools.WriteFileTool>();
        services.AddSingleton<ITool, Tools.EditFileTool>();
        services.AddSingleton<ITool, Tools.ExecuteCommandTool>();
        services.AddSingleton<ITool, Tools.SearchTextTool>();

        services.AddSingleton<ITool, Tools.SearchFilesTool>();

        services.AddSingleton<ITool, Tools.GetScreenSizeTool>();

        services.AddSingleton<ITool, Tools.MoveMouseTool>();

        services.AddSingleton<ITool, Tools.ClickMouseTool>();

        services.AddSingleton<ITool, Tools.ScrollMouseTool>();

        services.AddSingleton<ITool, Tools.TypeTextTool>();

        services.AddSingleton<ITool, Tools.PressKeyTool>();
        // Adaptadores de proveedor (auto-registro)
        services.AddSingleton<OpenCodeProvider>();
        services.AddSingleton<OpenRouterProvider>();
        services.AddSingleton<LocalModelProvider>();
        services.AddSingleton<Providers.Zen.ModelsDevCatalog>(sp =>
            new Providers.Zen.ModelsDevCatalog(
                new HttpClient(new SocketsHttpHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                }),
                ResolveDataPath(sp),
                sp.GetRequiredService<ILogger<Providers.Zen.ModelsDevCatalog>>()));
        services.AddSingleton<Providers.Zen.OpenCodeZenProvider>();
        services.AddSingleton<IStartupTask, ProviderRegistrationTask>();

        // Actualización automática desde GitHub Releases (firma CA: canal fijo).
        services.AddSingleton<Domain.Update.IAppUpdateService>(sp =>
            new Update.GitHubAppUpdater(
                new HttpClient(new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                }),
                sp.GetRequiredService<ILogger<Update.GitHubAppUpdater>>()));

        // Hidrata ToolRegistry con las herramientas registradas en DI.
        services.AddSingleton<IStartupTask, ToolRegistrationTask>();

        return services;
    }

    /// <summary>Tarea de arranque: la ejecuta el host tras construir el contenedor.</summary>
    public interface IStartupTask
    {
        Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken);
    }

    public static async Task RunStartupTasksAsync(IServiceProvider services, CancellationToken ct)
    {
        foreach (var task in services.GetServices<IStartupTask>())
        {
            await task.ExecuteAsync(services, ct).ConfigureAwait(false);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterSecretStore(IServiceCollection services)
    {
        // Bóveda estándar (Credential Manager) + fallback al fichero DPAPI de la Fase 0.
        services.AddSingleton<CredentialManagerSecretStore>();
        services.AddSingleton<DpapiSecretStore>(sp =>
            new DpapiSecretStore(
                ResolveDataPath(sp),
                sp.GetRequiredService<ILogger<DpapiSecretStore>>()));
        services.AddSingleton<ISecretStore>(sp =>
            new FallbackSecretStore(
                sp.GetRequiredService<CredentialManagerSecretStore>(),
                sp.GetRequiredService<DpapiSecretStore>()));
    }

    private static string ResolveDataPath(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CaAIAOptions>>().Value;
        return string.IsNullOrWhiteSpace(options.Persistence.DataPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CA-A-IA")
            : options.Persistence.DataPath;
    }

    private sealed class ProviderRegistrationTask : IStartupTask
    {
        public Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            var providers = services
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<CaAIAOptions>>().Value.Providers;
            var enabled = new HashSet<string>(providers.EnabledProviders, StringComparer.OrdinalIgnoreCase);
            var registry = services.GetRequiredService<IProviderRegistry>();
            var resilientLog = services.GetRequiredService<ILogger<ResilientAIProvider>>();

            void Maybe(string id, IAIProvider provider)
            {
                if (enabled.Count == 0 || enabled.Contains(id))
                {
                    // Toda llamada al proveedor pasa por timeout+reintentos+backoff.
                    registry.Register(new ResilientAIProvider(provider, providers.MaxRetries, resilientLog));
                }
            }

            Maybe(OpenCodeProvider.ProviderId, services.GetRequiredService<OpenCodeProvider>());
            Maybe(OpenRouterProvider.ProviderId, services.GetRequiredService<OpenRouterProvider>());
            Maybe(LocalModelProvider.ProviderId, services.GetRequiredService<LocalModelProvider>());
            Maybe(Providers.Zen.OpenCodeZenProvider.ProviderId, services.GetRequiredService<Providers.Zen.OpenCodeZenProvider>());
            return Task.CompletedTask;
        }
    }

    private sealed class ToolRegistrationTask : IStartupTask
    {
        public Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            var registry = services.GetRequiredService<IToolRegistry>();
            foreach (var tool in services.GetServices<ITool>())
            {
                registry.Register(tool);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class MigrationTask : IStartupTask
    {
        public async Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            var importer = services.GetRequiredService<Persistence.Migration.LegacyJsonImporter>();
            var db = services.GetRequiredService<Persistence.Sqlite.SqliteConnectionFactory>();
            await importer.MigrateOnceAsync(db, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class PreferencesRestoreTask : IStartupTask
    {
        public async Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            var prefs = services.GetRequiredService<Application.Services.IUserPreferences>();
            await prefs.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
