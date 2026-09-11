// CA-A-IA — Ejecutor real de tareas: bucle modelo→herramientas con permisos,
// confirmación humana, contexto de proyecto y clasificación de fallos (§20).

using CaAIA.Application.Configuration;
using CaAIA.Application.Services;
using CaAIA.Domain.AI;
using CaAIA.Domain.Context;
using CaAIA.Domain.Correlation;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Events;
using CaAIA.Domain.Interaction;
using CaAIA.Domain.Memory;
using CaAIA.Domain.Persistence;
using CaAIA.Domain.Planning;
using CaAIA.Domain.Security;
using CaAIA.Domain.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Agent.Execution;

/// <summary>
/// Bucle agéntico por tarea: contexto → provider (+tools) → autorizar → ejecutar →
/// resultados → repetir hasta resumen final o <c>MaxToolIterations</c>.
/// Solo depende de contratos de Domain/Application: funciona con cualquier proveedor
/// y herramienta registrados, y es testeable con dobles.
/// </summary>
public sealed class LlmTaskExecutor : ITaskExecutor
{
    private readonly IAgentSessionStore _sessions;
    private readonly IProviderRegistry _providers;
    private readonly IUserPreferences _selection;
    private readonly IToolRegistry _tools;
    private readonly IToolPermissionService _permissions;
    private readonly IContextBuilder _context;
    private readonly IUserConfirmation _confirmation;
    private readonly IMemoryStore _memory;
    private readonly IEventBus _events;
    private readonly CaAIAOptions _options;
    private readonly ILogger<LlmTaskExecutor> _log;

    public LlmTaskExecutor(
        IAgentSessionStore sessions,
        IProviderRegistry providers,
        IUserPreferences selection,
        IToolRegistry tools,
        IToolPermissionService permissions,
        IContextBuilder context,
        IUserConfirmation confirmation,
        IMemoryStore memory,
        IEventBus events,
        IOptions<CaAIAOptions> options,
        ILogger<LlmTaskExecutor> log)
    {
        _sessions = sessions;
        _providers = providers;
        _selection = selection;
        _tools = tools;
        _permissions = permissions;
        _context = context;
        _confirmation = confirmation;
        _memory = memory;
        _events = events;
        _options = options.Value;
        _log = log;
    }

    public async Task<TaskExecutionOutcome> ExecuteTaskAsync(Plan plan, AgentTask task, CancellationToken ct)
    {
        var session = await _sessions.LoadAsync(plan.SessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return new TaskExecutionOutcome(false, $"Session {plan.SessionId} not found.", FailureCategory.EnvironmentFailure);
        }

        if (string.IsNullOrWhiteSpace(_selection.ModelId))
        {
            return new TaskExecutionOutcome(false,
                "No model selected (choose one in Chat, or set CaAIA:Providers:DefaultModelId).",
                FailureCategory.EnvironmentFailure);
        }

        IAIProvider provider;
        try
        {
            provider = _providers.Get(_selection.ProviderId);
        }
        catch (KeyNotFoundException ex)
        {
            return new TaskExecutionOutcome(false, ex.Message, FailureCategory.EnvironmentFailure);
        }

        var scope = BuildScope(session.WorkspacePath);
        var correlation = CorrelationContext.Create(plan.SessionId, Guid.NewGuid(), task.Id);
        var tools = _tools.ListDefinitionsFor(scope.GrantedPermissions);

        ProjectContext context;
        try
        {
            context = await _context.BuildAsync(session.WorkspacePath,
                new[] { task.Title }, _options.Agent.MaxTaskContextChars, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TaskExecutionOutcome(false, $"Cannot read workspace: {ex.Message}", FailureCategory.EnvironmentFailure);
        }

        var history = new List<AIMessage>
        {
            new(AIRole.System, SystemPrompt(session.WorkspacePath, tools)),
            new(AIRole.User, TaskPrompt(task, context)),
        };

        var maxIterations = Math.Max(1, _options.Agent.MaxToolIterations);
        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            AIResponse response;
            try
            {
                response = await provider.CompleteAsync(new AIRequest
                {
                    ModelId = _selection.ModelId,
                    Messages = history,
                    Tools = tools.ToList(),
                    Temperature = 0.2,
                    ReasoningEffort = _selection.ReasoningEffort,
                    Timeout = TimeSpan.FromSeconds(_options.Providers.RequestTimeoutSeconds),
                    Correlation = correlation,
                }, ct).ConfigureAwait(false);
            }
            catch (AIProviderException ex)
            {
                return new TaskExecutionOutcome(false, $"Provider error: {ex.Message}", MapProviderError(ex.Kind));
            }

            if (response.ToolCalls.Count == 0)
            {
                await RememberAsync(session.Id, task.Id,
                    $"task-result: {task.Title}", Truncate(response.Content, 2000), ct).ConfigureAwait(false);
                return new TaskExecutionOutcome(true);
            }

            history.Add(new AIMessage(AIRole.Assistant, response.Content, response.ToolCalls));
            foreach (var call in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var result = await InvokeToolAsync(call, tools, scope, correlation, ct).ConfigureAwait(false);
                history.Add(new AIMessage(AIRole.Tool,
                    result.Success ? result.Output : $"ERROR [{result.FailureCategory}]: {result.Error}",
                    ToolCallId: call.Id));
            }
        }

        return new TaskExecutionOutcome(false,
            $"Task did not finish within {maxIterations} tool iterations.", FailureCategory.ToolFailure);
    }

    private async Task<ToolResult> InvokeToolAsync(
        AIToolCall call, IReadOnlyCollection<ToolDefinition> visible, ExecutionScope scope,
        CorrelationContext correlation, CancellationToken ct)
    {
        ToolDefinition definition;
        try
        {
            definition = _tools.Get(call.ToolName).Definition;
        }
        catch (KeyNotFoundException)
        {
            // Determinista: reintentarlo 3× es inútil. Unknown no lo repara la política.
            return new ToolResult(Guid.NewGuid(), call.ToolName, false, string.Empty,
                FailureCategory: FailureCategory.Unknown, Error: $"Unknown tool '{call.ToolName}'.");
        }

        var invocation = new ToolInvocation(Guid.NewGuid(), definition.Id, call.ArgumentsJson, correlation,
            TimeoutOverride: TimeSpan.FromSeconds(Math.Max(10, _options.Execution.ToolTimeoutSeconds)));

        var decision = _permissions.Authorize(invocation, definition, scope);
        if (!decision.Allowed)
        {
            _log.LogWarning("Tool {Tool} denied: {Reason}", definition.Id, decision.Reason);
            return new ToolResult(invocation.InvocationId, definition.Id, false, string.Empty,
                PermissionDenied: true, FailureCategory: FailureCategory.PermissionFailure, Error: decision.Reason);
        }

        if (decision.RequiresUserConfirmation)
        {
            bool confirmed;
            try
            {
                confirmed = await _confirmation.RequestAsync(
                    $"Allow {definition.Id}?", $"{definition.Description}\n{call.ArgumentsJson}", ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelar la run en el prompt no es "denegado por el usuario": si el
                // token del llamador pide cancelación, propagar para salir limpiamente
                // en vez de generar un TaskFailed + reparación fantasma.
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                confirmed = false;
            }

            if (!confirmed)
            {
                return new ToolResult(invocation.InvocationId, definition.Id, false, string.Empty,
                    PermissionDenied: true, FailureCategory: FailureCategory.PermissionFailure,
                    Error: "Denied by user confirmation.");
            }
        }

        _events.Publish(AgentEvent.Create(AgentEventType.ToolStarted, correlation,
            $"{definition.Id} started.", call.ArgumentsJson));
        ToolResult result;
        try
        {
            result = await _tools.Get(definition.Id).ExecuteAsync(invocation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new ToolResult(invocation.InvocationId, definition.Id, false, string.Empty,
                FailureCategory: FailureCategory.ToolFailure, Error: ex.Message);
        }

        _events.Publish(AgentEvent.Create(AgentEventType.ToolCompleted, correlation,
            $"{definition.Id}: {(result.Success ? "ok" : result.Error)}", call.ArgumentsJson));
        return result;
    }

    private ExecutionScope BuildScope(string workspace) =>
        ExecutionScope.FromAuthorizationLevel(
            workspace,
            _options.Security.DeniedPaths,
            _selection.AuthorizationLevel,
            _options.Security.AllowPackageInstall);

    private static FailureCategory MapProviderError(AIErrorKind kind) => kind switch
    {
        AIErrorKind.Network or AIErrorKind.Timeout => FailureCategory.NetworkFailure,
        // Auth/cuota/modelo/contexto: problema de CONFIGURACIÓN, no del código.
        // Reintentarlo mutilaría el proyecto a ciegas: escala sin reparar.
        AIErrorKind.Authentication or AIErrorKind.Authorization or AIErrorKind.ModelNotFound
            or AIErrorKind.ContextTooLong => FailureCategory.EnvironmentFailure,
        _ => FailureCategory.ProviderFailure,
    };

    private async Task RememberAsync(Guid sessionId, Guid taskId, string key, string content, CancellationToken ct)
    {
        try
        {
            await _memory.PutAsync(new MemoryEntry(Guid.NewGuid(), MemoryScope.Task, sessionId, taskId,
                key, content, DateTimeOffset.UtcNow, ExpiresAt: DateTimeOffset.UtcNow.AddHours(24)), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Memory write failed (non-fatal)");
        }
    }

    private static string SystemPrompt(string workspace, IReadOnlyCollection<ToolDefinition> tools) =>
        $"""
        You are CA-A-IA, an autonomous coding agent working in the workspace:
        {workspace}

        Rules:
        - Work ONLY inside the workspace using absolute paths. Never touch anything outside.
        - Use the available tools ({tools.Count}: {string.Join(", ", tools.Select(t => t.Id))}) to inspect, edit, build and test. Do not guess file contents.
        - For ExecuteCommand, always set workdir to the workspace path.
        - Verify your work: build the project and run relevant tests before finishing.
        - When the task is done, reply with a concise summary including: files changed, build result, test result, and how each acceptance criterion is met.
        - If blocked by a permission denial, explain and stop that approach instead of retrying it.
        """;

    private static string TaskPrompt(AgentTask task, ProjectContext context)
    {
        var files = string.Join("\n", context.Fragments.Select(f => $"- {f.Source} ({f.Content.Length} chars)"));
        var bodies = string.Join("\n\n", context.Fragments
            .OrderByDescending(f => f.Priority)
            .Take(15)
            .Select(f => $"=== {f.Source} ===\n{Truncate(f.Content, 6000)}"));
        return $"""
            TASK: {task.Title}
            {task.Description}

            Acceptance criteria:
            {string.Join("\n", task.AcceptanceCriteria.Select(c => $"- {c}"))}

            Relevant files ({context.Fragments.Count} shown, {context.TruncatedFragments} truncated):
            {files}

            File contents:
            {bodies}
            """;
    }

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "\n…[truncated]" : text;
}
