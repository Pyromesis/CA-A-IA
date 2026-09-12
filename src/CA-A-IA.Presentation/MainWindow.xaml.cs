// CA-A-IA · Fase 0 — Shell principal: Sidebar + contenido + StatusBar.
// Navegación por resolución DI (las páginas son transient con su ViewModel).

using CaAIA.Presentation.ViewModels;
using CaAIA.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CaAIA.Presentation;

public sealed partial class MainWindow : Window
{
    public StatusBarViewModel Status { get; }
    private readonly Diagnostics.UiFlightRecorder _flight; // TEMPORARY-DIAGNOSTIC

    public MainWindow(StatusBarViewModel status, Diagnostics.UiFlightRecorder flight)
    {
        Status = status;
        _flight = flight;
        WarmUpViewModels();
        InitializeComponent();
        Title = "CA-A-IA";
        Navigate("chat");
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    /// <summary>
    /// Los ViewModels se suscriben al bus de eventos en su constructor. Si solo se
    /// crearan al navegar, Salida/Tareas/Plan perderían todo lo anterior (p. ej.
    /// Salida vacía tras ejecutar). Se crean una vez aquí, en el hilo UI.
    /// </summary>
    private static void WarmUpViewModels()
    {
        _ = App.Services.GetRequiredService<ChatViewModel>();
        _ = App.Services.GetRequiredService<AutonomyViewModel>();
        _ = App.Services.GetRequiredService<HistoryViewModel>();
        _ = App.Services.GetRequiredService<PlanViewModel>();
        _ = App.Services.GetRequiredService<TasksViewModel>();
        _ = App.Services.GetRequiredService<FilesViewModel>();
        _ = App.Services.GetRequiredService<MemoriaViewModel>();
        _ = App.Services.GetRequiredService<OutputViewModel>();
        _ = App.Services.GetRequiredService<SettingsViewModel>();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            Navigate(tag);
        }
    }

    public void Navigate(string tag)
    {
        _flight.Action("navigate:" + tag); // TEMPORARY-DIAGNOSTIC
        object page = tag switch
        {
            "chat" => App.Services.GetRequiredService<ChatView>(),
            "autonomy" => App.Services.GetRequiredService<AutonomyView>(),
            "history" => App.Services.GetRequiredService<HistoryView>(),
            "plan" => App.Services.GetRequiredService<PlanView>(),
            "tasks" => App.Services.GetRequiredService<TasksView>(),
            "files" => App.Services.GetRequiredService<FilesView>(),
            "memoria" => App.Services.GetRequiredService<MemoriaView>(),
            "output" => App.Services.GetRequiredService<OutputView>(),
            "settings" => App.Services.GetRequiredService<SettingsView>(),
            _ => App.Services.GetRequiredService<ChatView>(),
        };
        ContentHost.Content = page;
        HeaderText.Text = tag switch
        {
            "chat" => "Chat",
            "autonomy" => "Autonomía",
            "history" => "Historial",
            "plan" => "Plan",
            "tasks" => "Tareas",
            "files" => "Archivos",
            "memoria" => "Memoria",
            "output" => "Salida",
            "settings" => "Ajustes",
            _ => "Chat",
        };
        HeaderSub.Text = tag switch
        {
            "chat" => "Habla con tu agente",
            "autonomy" => "Controla tu PC como lo harías tú",
            "history" => "Tus conversaciones guardadas",
            "plan" => "Revisa, pregunta y ejecuta",
            "tasks" => "El paso a paso del plan activo",
            "files" => "Lo que ve el agente en tu carpeta",
            "memoria" => "La red de conocimiento de la IA",
            "output" => "El diario de la ejecución",
            "settings" => "Claves, modelos y conexión",
            _ => "Habla con tu agente",
        };
    }
}
