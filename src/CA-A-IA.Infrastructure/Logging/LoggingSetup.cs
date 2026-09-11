// CA-A-IA · Fase 0 — Logging estructurado con IDs de correlación (§26).

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace CaAIA.Infrastructure.Logging;

/// <summary>
/// Logging para escritorio Windows: Debug (VS) + Console + EventLog en Windows.
/// Formato con scopes: cada ejecución abre un scope con SessionId/ExecutionId/TaskId.
/// </summary>
public static class LoggingSetup
{
    public static IServiceCollection AddCaAIALogging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddDebug();
            logging.AddConsole(options => options.FormatterName = "ca-a-ia");
            logging.AddConsoleFormatter<CorrelationConsoleFormatter, ConsoleFormatterOptions>();
            if (OperatingSystem.IsWindows())
            {
                // El EventLog exige crear el origen (admin) al primer write: si falla,
                // se sigue con consola/debug en lugar de romper arranque o logs.
                // (La guarda de plataforma contiene la API Windows-only para el analizador.)
                try
                {
                    AddWindowsEventLog(logging);
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                    or System.Security.SecurityException or System.ComponentModel.Win32Exception)
                {
                    System.Diagnostics.Debug.WriteLine($"EventLog disabled: {ex.Message}");
                }
            }
        });
        return services;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddWindowsEventLog(ILoggingBuilder logging) =>
        logging.AddEventLog(settings =>
        {
            settings.SourceName = "CA-A-IA";
            settings.LogName = "Application";
        });

    /// <summary>Abre un scope logueable con los IDs de correlación (§26).</summary>
    public static IDisposable? CorrelationScope(
        this ILogger logger, Guid sessionId, Guid executionId, Guid? taskId = null) =>
        logger.BeginScope(new Dictionary<string, object?>
        {
            ["SessionId"] = sessionId,
            ["ExecutionId"] = executionId,
            ["TaskId"] = taskId,
        });
}

/// <summary>Formateador de consola de una línea con timestamp + categoría + scopes.</summary>
internal sealed class CorrelationConsoleFormatter : ConsoleFormatter
{
    public CorrelationConsoleFormatter() : base("ca-a-ia")
    {
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var scope = string.Empty;
        scopeProvider?.ForEachScope((value, _) => scope += $" [{value}]", state: (object?)null);
        textWriter.WriteLine(
            $"{DateTimeOffset.Now:HH:mm:ss} [{logEntry.LogLevel,-11}] {logEntry.Category}{scope} :: {logEntry.Formatter(logEntry.State, logEntry.Exception)}");
        if (logEntry.Exception is not null)
        {
            textWriter.WriteLine(logEntry.Exception);
        }
    }
}
