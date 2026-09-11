// CA-A-IA · Fase 0 — DTOs de Application (lo que consumen los ViewModels; nunca entidades).

using CaAIA.Domain.Enums;

namespace CaAIA.Application.DTOs;

public sealed record SessionDto(
    Guid Id,
    string WorkspacePath,
    string UserRequest,
    Guid? PlanId,
    AgentState LastKnownState,
    bool IsActive,
    DateTimeOffset UpdatedAt);

public sealed record TaskDto(
    Guid Id,
    string Title,
    string Description,
    AgentTaskStatus Status,
    int Attempts,
    int MaxAttempts,
    IReadOnlyList<Guid> DependsOn,
    IReadOnlyList<string> AcceptanceCriteria,
    string? LastError);

public sealed record PlanDto(
    Guid Id,
    Guid SessionId,
    string Goal,
    PlanStatus Status,
    int Revision,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<TaskDto> Tasks,
    IReadOnlyList<string> FinalAcceptanceCriteria,
    bool HasOpenQuestions,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<OpenQuestionDto> OpenQuestions);

public sealed record OpenQuestionDto(
    Guid Id,
    string Question,
    string? Context,
    IReadOnlyList<string> SuggestedOptions,
    bool IsAnswered);

public sealed record ProviderDto(
    string Id,
    string DisplayName,
    ProviderCapabilitiesDto Capabilities,
    IReadOnlyList<ModelDto> Models);

public sealed record ModelDto(
    string Id,
    string DisplayName,
    int? ContextWindowTokens,
    ProviderCapabilitiesDto Capabilities,
    bool IsFree);

[Flags]
public enum ProviderCapabilitiesDto
{
    None = 0,
    Streaming = 1 << 0,
    Tools = 1 << 1,
    Vision = 1 << 2,
    StructuredOutput = 1 << 3,
    Reasoning = 1 << 4,
    Embeddings = 1 << 5,
    LongContext = 1 << 6,
    CodeExecution = 1 << 7,
}

public sealed record ToolDto(
    string Id,
    string Name,
    string Description,
    string Kind,
    TimeSpan DefaultTimeout);

/// <summary>Traducción entidad → DTO (unidireccional; los comandos van en sentido contrario).</summary>
public static class DtoMapper
{
    public static SessionDto ToDto(this Domain.Planning.AgentSession s) =>
        new(s.Id, s.WorkspacePath, s.UserRequest, s.PlanId, s.LastKnownState, s.IsActive, s.UpdatedAt);

    public static TaskDto ToDto(this Domain.Planning.AgentTask t) =>
        new(t.Id, t.Title, t.Description, t.Status, t.Attempts, t.MaxAttempts,
            t.DependsOn, t.AcceptanceCriteria, t.LastError);

    public static PlanDto ToDto(this Domain.Planning.Plan p) =>
        new(p.Id, p.SessionId, p.Goal, p.Status, p.Revision, p.Requirements,
            p.Tasks.Select(ToDto).ToList(), p.FinalAcceptanceCriteria, p.HasOpenQuestions, p.UpdatedAt,
            p.OpenQuestions.Select(q => new OpenQuestionDto(
                q.Id, q.Question, q.Context, q.SuggestedOptions, q.IsAnswered)).ToList());

    public static ProviderDto ToDto(this Domain.AI.IAIProvider p, IReadOnlyCollection<Domain.AI.AIModel> models) =>
        new(p.Id, p.DisplayName, (ProviderCapabilitiesDto)(int)p.Capabilities,
            models.Where(m => m.ProviderId == p.Id)
                  .Select(m => new ModelDto(m.Id, m.DisplayName, m.ContextWindowTokens,
                      (ProviderCapabilitiesDto)(int)m.Capabilities, m.IsFree))
                  .ToList());

    public static ToolDto ToDto(this Domain.Tools.ToolDefinition d) =>
        new(d.Id, d.Name, d.Description, d.Kind.ToString(), d.DefaultTimeout);
}
