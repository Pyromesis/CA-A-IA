// CA-A-IA · Fase 0 — Registro DI de Presentation (Views + ViewModels + MainWindow).

using CaAIA.Presentation.ViewModels;
using CaAIA.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CaAIA.Presentation;

public static class PresentationServiceExtensions
{
    public static IServiceCollection AddPresentation(this IServiceCollection services)
    {
        services.AddSingleton<MainWindow>();
        services.AddSingleton<Diagnostics.UiFlightRecorder>(); // TEMPORARY-DIAGNOSTIC

        // Compartido por todas las vistas (barra de estado global).
        services.AddSingleton<StatusBarViewModel>();

        // Los ViewModels son singleton: el estado (chat, plan, workspace) sobrevive al
        // cambio de pestaña. Las vistas siguen transient (se re-enlazan al mismo VM).
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<AutonomyViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<PlanViewModel>();
        services.AddSingleton<TasksViewModel>();
        services.AddSingleton<FilesViewModel>();
        services.AddSingleton<OutputViewModel>();
        services.AddSingleton<SettingsViewModel>();

        services.AddTransient<ChatView>();
        services.AddTransient<AutonomyView>();
        services.AddTransient<HistoryView>();
        services.AddTransient<PlanView>();
        services.AddTransient<TasksView>();
        services.AddTransient<FilesView>();
        services.AddTransient<OutputView>();
        services.AddTransient<SettingsView>();

        // Sustituye al DenyAllConfirmation de Infrastructure: con UI sí se puede preguntar.
        // (AddPresentation corre después de AddInfrastructure; la última marca gana.)
        // Func<> perezoso: DialogConfirmation NO debe construir MainWindow en su ctor
        // (dependencia circular MainWindow→…→DialogConfirmation→MainWindow).
        services.AddSingleton<Func<MainWindow>>(sp => () => sp.GetRequiredService<MainWindow>());
        services.AddSingleton<Domain.Interaction.IUserConfirmation, Services.DialogConfirmation>();

        return services;
    }
}
