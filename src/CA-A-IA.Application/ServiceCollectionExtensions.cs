// CA-A-IA · Fase 0 — Composition de Application (los adaptadores se registran en sus capas).

using CaAIA.Application.Configuration;
using CaAIA.Application.Services;
using CaAIA.Application.UseCases;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaAIA.Application;

/// <summary>Registro DI de Application: opciones + casos de uso + coordinador.</summary>
public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CaAIAOptions>(configuration.GetSection(CaAIAOptions.SectionName));

        services.AddSingleton<IUserPreferences, UserPreferences>();
        services.AddSingleton<ISessionContext, SessionContext>();
        services.AddSingleton<LessonStore>();
        services.AddScoped<SessionUseCase>();
        services.AddScoped<PlanningUseCase>();
        services.AddScoped<ISessionCoordinator, SessionCoordinator>();

        return services;
    }
}
