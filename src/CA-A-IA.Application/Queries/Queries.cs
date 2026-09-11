// CA-A-IA · Fase 0 — Queries (lectura; nunca mutan estado).

using CaAIA.Application.DTOs;

namespace CaAIA.Application.Queries;

public interface IQuery<TResult>;

public interface IQueryHandler<TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

public sealed record GetSessionQuery(Guid SessionId) : IQuery<SessionDto?>;
public sealed record ListActiveSessionsQuery() : IQuery<IReadOnlyList<SessionDto>>;
public sealed record GetPlanQuery(Guid PlanId) : IQuery<PlanDto?>;
public sealed record GetSessionPlanQuery(Guid SessionId) : IQuery<PlanDto?>;
public sealed record ListProvidersQuery() : IQuery<IReadOnlyList<ProviderDto>>;
public sealed record ListToolsQuery() : IQuery<IReadOnlyList<ToolDto>>;
